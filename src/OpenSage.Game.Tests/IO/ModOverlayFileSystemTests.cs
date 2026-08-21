using System;
using System.IO;
using System.Linq;
using System.Text;
using OpenSage.Data;
using OpenSage.FileFormats.Big;
using OpenSage.IO;
using OpenSage.Mods.BuiltIn;
using Xunit;

namespace OpenSage.Tests.IO;

/// <summary>
/// Data-free tests for the engine's precedence chain: <c>-mod</c> loose files, then the game
/// directory's loose files, then the <c>-mod</c> archives, then the game's archives, then the base
/// game's archives — with the loose layer moving below the archives when the mod flag is clear.
/// </summary>
public sealed class ModOverlayFileSystemTests : IDisposable
{
    private const string ProbePath = @"data\ini\armor.ini";
    private const string SiblingPath = @"data\ini\weapon.ini";

    private readonly string _root;
    private readonly string _game;
    private readonly string _baseGame;
    private readonly string _mod;

    public ModOverlayFileSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "opensage-modvfs-" + Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_root, "game");
        _baseGame = Path.Combine(_root, "basegame");
        _mod = Path.Combine(_root, "mod");
        Directory.CreateDirectory(_game);
        Directory.CreateDirectory(_baseGame);
        Directory.CreateDirectory(_mod);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }

    private void WriteArchive(string directory, string name, string contents, string entryPath = ProbePath)
    {
        using var archive = new BigArchive(Path.Combine(directory, name), BigArchiveMode.Create);
        var entry = archive.CreateEntry(entryPath);
        using var stream = entry.Open();
        var bytes = Encoding.ASCII.GetBytes(contents);
        stream.Write(bytes, 0, bytes.Length);
    }

    private void WriteLooseFile(string directory, string contents)
    {
        var fullPath = Path.Combine(directory, "data", "ini", "armor.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
    }

    /// <summary>Present in every stock install; its absence is what sets the engine's mod flag.</summary>
    private void WriteShadersBig()
    {
        WriteArchive(_game, "Shaders.big", "shaders", @"shaders\dummy.fx");
    }

    private GameInstallation CreateInstallation(string modPath = null)
    {
        var definition = GameDefinition.FromGame(SageGame.Bfme2Rotwk);
        var baseGame = new GameInstallation(definition.BaseGame, _baseGame);
        return new GameInstallation(definition, _game, baseGame, modPath);
    }

    private static string Read(FileSystemEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    private string Resolve(GameInstallation installation)
    {
        using var fileSystem = installation.CreateFileSystem();
        var entry = fileSystem.GetFile(ProbePath);
        Assert.NotNull(entry);
        return Read(entry);
    }

    [Fact]
    public void ModLooseFilesBeatEverythingElse()
    {
        WriteShadersBig();
        WriteLooseFile(_mod, "mod-loose");
        WriteArchive(_mod, "modstuff.big", "mod-archive");
        WriteLooseFile(_game, "game-loose");
        WriteArchive(_game, "INI.big", "game-archive");
        WriteArchive(_baseGame, "INI.big", "base-archive");

        Assert.Equal("mod-loose", Resolve(CreateInstallation(_mod)));
    }

    [Fact]
    public void ModFlagSetPutsTheGameDirectoryLooseFileAboveTheArchives()
    {
        WriteShadersBig();
        WriteLooseFile(_game, "game-loose");
        WriteArchive(_mod, "modstuff.big", "mod-archive");
        WriteArchive(_game, "INI.big", "game-archive");

        Assert.Equal("game-loose", Resolve(CreateInstallation(_mod)));
    }

    [Fact]
    public void ModArchivesBeatTheGameAndBaseGameArchives()
    {
        WriteShadersBig();
        WriteArchive(_mod, "modstuff.big", "mod-archive");
        WriteArchive(_game, "INI.big", "game-archive");
        WriteArchive(_baseGame, "INI.big", "base-archive");

        Assert.Equal("mod-archive", Resolve(CreateInstallation(_mod)));
    }

    [Fact]
    public void GameArchivesBeatBaseGameArchives()
    {
        WriteShadersBig();
        WriteArchive(_game, "INI.big", "game-archive");
        WriteArchive(_baseGame, "INI.big", "base-archive");

        Assert.Equal("game-archive", Resolve(CreateInstallation()));
    }

    /// <summary>Within the mod layer the engine registers with overwrite = TRUE, so last wins.</summary>
    [Fact]
    public void TheLastModArchiveWinsWithinTheModLayer()
    {
        WriteShadersBig();
        WriteArchive(_mod, "aaa.big", "mod-first");
        WriteArchive(_mod, "zzz.big", "mod-last");

        Assert.Equal("mod-last", Resolve(CreateInstallation(_mod)));
    }

    /// <summary>
    /// A stock install — mod flag clear — resolves archives first and only falls back to the loose
    /// file.
    /// </summary>
    [Fact]
    public void WithoutAModTheArchivesBeatTheLooseFile()
    {
        WriteShadersBig();
        WriteLooseFile(_game, "game-loose");
        WriteArchive(_game, "INI.big", "game-archive");

        Assert.Equal("game-archive", Resolve(CreateInstallation()));
    }

    /// <summary>
    /// A loose / developer install (no shaders.big) sets the same flag a -mod argument does, so the
    /// loose file wins again.
    /// </summary>
    [Fact]
    public void AnInstallWithoutShadersBigTreatsLooseFilesAsModded()
    {
        WriteLooseFile(_game, "game-loose");
        WriteArchive(_game, "INI.big", "game-archive");

        Assert.Equal("game-loose", Resolve(CreateInstallation()));
    }

    /// <summary>
    /// A file the engine opened through the stack can name a sibling — an <c>.apt</c>'s
    /// <c>.dat</c>, an <c>.ini</c>'s <c>9x</c> twin or <c>#include</c> target — and callers reach
    /// for <see cref="FileSystemEntry.FileSystem"/> to resolve it. That has to be the whole stack:
    /// scoping it to the layer that provided the first file is what stopped a mod's loose
    /// <c>MainMenu.apt</c> from finding the base game's <c>MainMenu.dat</c>.
    /// </summary>
    [Fact]
    public void ASiblingOfAModFileIsResolvedThroughEveryLayer()
    {
        WriteShadersBig();
        WriteLooseFile(_mod, "mod-loose");
        WriteArchive(_baseGame, "INI.big", "base-sibling", SiblingPath);

        using var fileSystem = CreateInstallation(_mod).CreateFileSystem();

        var entry = fileSystem.GetFile(ProbePath);
        Assert.NotNull(entry);
        Assert.Equal("mod-loose", Read(entry));

        var sibling = entry.FileSystem.GetFile(SiblingPath);
        Assert.NotNull(sibling);
        Assert.Equal("base-sibling", Read(sibling));
    }

    /// <summary>Sibling lookups keep the layer precedence: the mod's copy still wins.</summary>
    [Fact]
    public void ASiblingLookupKeepsTheLayerPrecedence()
    {
        WriteShadersBig();
        WriteLooseFile(_mod, "mod-loose");
        WriteArchive(_mod, "modstuff.big", "mod-sibling", SiblingPath);
        WriteArchive(_baseGame, "INI.big", "base-sibling", SiblingPath);

        using var fileSystem = CreateInstallation(_mod).CreateFileSystem();

        var entry = fileSystem.GetFile(ProbePath);
        Assert.NotNull(entry);

        Assert.Equal("mod-sibling", Read(entry.FileSystem.GetFile(SiblingPath)));
    }

    /// <summary>Entries handed out by a directory listing resolve siblings the same way.</summary>
    [Fact]
    public void ASiblingOfAListedFileIsResolvedThroughEveryLayer()
    {
        WriteShadersBig();
        WriteLooseFile(_mod, "mod-loose");
        WriteArchive(_baseGame, "INI.big", "base-sibling", SiblingPath);

        using var fileSystem = CreateInstallation(_mod).CreateFileSystem();

        var entry = fileSystem
            .GetFilesInDirectory(@"data\ini", "armor.ini")
            .Single();

        Assert.Equal("base-sibling", Read(entry.FileSystem.GetFile(SiblingPath)));
    }

    [Fact]
    public void ASingleBigArchiveIsAcceptedAsTheModPath()
    {
        WriteShadersBig();
        WriteArchive(_root, "standalone.big", "mod-archive");
        WriteArchive(_game, "INI.big", "game-archive");

        Assert.Equal("mod-archive", Resolve(CreateInstallation(Path.Combine(_root, "standalone.big"))));
    }
}
