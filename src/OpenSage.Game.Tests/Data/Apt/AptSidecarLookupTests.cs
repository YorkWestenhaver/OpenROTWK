using System;
using System.IO;
using System.Text;
using OpenSage.Data;
using OpenSage.Data.Apt;
using OpenSage.FileFormats.Big;
using OpenSage.IO;
using OpenSage.Mods.BuiltIn;
using Xunit;

namespace OpenSage.Tests.Data.Apt;

/// <summary>
/// Data-free tests for the sidecar files an <c>.apt</c> pulls in — its <c>.const</c>, its
/// <c>.dat</c> image map and its <c>_geometry</c> directory. They are named relative to the
/// installation root and must be resolved through the whole layer stack, exactly like the
/// <c>.apt</c> itself: Age of the Ring ships a loose <c>MainMenu.apt</c> with no
/// <c>MainMenu.dat</c> beside it and expects the base game's copy to answer.
/// </summary>
public sealed class AptSidecarLookupTests : IDisposable
{
    private const uint AptDataEntryOffset = 0x10;
    private const uint ZeroRegionOffset = 0x60;

    private readonly string _root;
    private readonly string _game;
    private readonly string _baseGame;
    private readonly string _mod;

    public AptSidecarLookupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "opensage-aptsidecar-" + Guid.NewGuid().ToString("N"));
        _game = Path.Combine(_root, "game");
        _baseGame = Path.Combine(_root, "basegame");
        _mod = Path.Combine(_root, "mod");
        Directory.CreateDirectory(_game);
        Directory.CreateDirectory(_baseGame);
        Directory.CreateDirectory(_mod);

        // Present in every stock install; its absence is what sets the engine's mod flag.
        WriteArchive(_game, "Shaders.big", @"shaders\dummy.fx", Array.Empty<byte>());
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

    /// <summary>
    /// The smallest thing <see cref="AptFile.FromFileSystemEntry"/> accepts: an "Apt Data" header,
    /// a movie with no frames, no imports, no exports, and a single null character slot (which the
    /// parser overwrites with the movie itself). No shapes, so no <c>.ru</c> geometry is read.
    /// </summary>
    private static byte[] CreateAptData()
    {
        var data = new byte[0x80];

        using var stream = new MemoryStream(data);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        writer.Write(Encoding.ASCII.GetBytes("Apt Data"));

        stream.Seek(AptDataEntryOffset, SeekOrigin.Begin);
        writer.Write(9u); // CharacterType.Movie
        writer.Write(0x09876543u); // Character.SIGNATURE

        writer.Write(0); // frame count
        writer.Write(ZeroRegionOffset); // frame list offset
        writer.Write(0u); // unknown
        writer.Write(1); // character count
        writer.Write(ZeroRegionOffset); // character pointer list offset - one null pointer
        writer.Write(1024u); // screen width
        writer.Write(768u); // screen height
        writer.Write(33u); // milliseconds per frame
        writer.Write(0); // import count
        writer.Write(ZeroRegionOffset); // import list offset
        writer.Write(0); // export count
        writer.Write(ZeroRegionOffset); // export list offset

        return data;
    }

    private static byte[] CreateConstData()
    {
        var data = new byte[32];

        using var stream = new MemoryStream(data);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);

        writer.Write(Encoding.ASCII.GetBytes("Apt constant file"));

        stream.Seek(20, SeekOrigin.Begin);
        writer.Write(AptDataEntryOffset);
        writer.Write(0u); // entry count
        writer.Write(32u); // header size

        return data;
    }

    /// <summary>An image map mapping image 0 to the given texture id.</summary>
    private static byte[] CreateDatData(int textureId)
    {
        return Encoding.ASCII.GetBytes($"0 -> {textureId}\n");
    }

    private static void WriteLooseFile(string directory, string fileName, byte[] contents)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, fileName), contents);
    }

    private static void WriteArchive(string directory, string archiveName, string entryPath, byte[] contents)
    {
        using var archive = new BigArchive(Path.Combine(directory, archiveName), BigArchiveMode.Create);
        var entry = archive.CreateEntry(entryPath);
        using var stream = entry.Open();
        stream.Write(contents, 0, contents.Length);
    }

    private FileSystem CreateFileSystem(bool withMod = true)
    {
        var definition = GameDefinition.FromGame(SageGame.Bfme2Rotwk);
        var baseGame = new GameInstallation(definition.BaseGame, _baseGame);
        var installation = new GameInstallation(definition, _game, baseGame, withMod ? _mod : null);
        return installation.CreateFileSystem();
    }

    private AptFile LoadMainMenu(FileSystem fileSystem)
    {
        var entry = fileSystem.GetFile("MainMenu.apt");
        Assert.NotNull(entry);
        return AptFile.FromFileSystemEntry(entry);
    }

    /// <summary>The Age of the Ring shape: a loose mod .apt whose .dat only the base game has.</summary>
    [Fact]
    public void AModsLooseAptFallsBackToTheBaseGameForItsImageMap()
    {
        WriteLooseFile(_mod, "MainMenu.apt", CreateAptData());
        WriteLooseFile(_mod, "MainMenu.const", CreateConstData());
        WriteArchive(_baseGame, "MainMenu.big", "MainMenu.dat", CreateDatData(7));

        using var fileSystem = CreateFileSystem();

        var aptFile = LoadMainMenu(fileSystem);

        Assert.Equal(7, aptFile.ImageMap.Mapping[0].TextureId);
    }

    /// <summary>The fallback is a fallback, not a preference: a mod's own .dat still wins.</summary>
    [Fact]
    public void AModsOwnImageMapBeatsTheBaseGames()
    {
        WriteLooseFile(_mod, "MainMenu.apt", CreateAptData());
        WriteLooseFile(_mod, "MainMenu.const", CreateConstData());
        WriteLooseFile(_mod, "MainMenu.dat", CreateDatData(3));
        WriteArchive(_baseGame, "MainMenu.big", "MainMenu.dat", CreateDatData(7));

        using var fileSystem = CreateFileSystem();

        var aptFile = LoadMainMenu(fileSystem);

        Assert.Equal(3, aptFile.ImageMap.Mapping[0].TextureId);
    }

    /// <summary>A stock install resolves the sidecars out of the same archive as before.</summary>
    [Fact]
    public void WithoutAModTheSidecarsStillComeFromTheGameArchive()
    {
        WriteArchive(_game, "MainMenu.big", "MainMenu.apt", CreateAptData());
        WriteArchive(_game, "MainMenuConst.big", "MainMenu.const", CreateConstData());
        WriteArchive(_game, "MainMenuDat.big", "MainMenu.dat", CreateDatData(11));

        using var fileSystem = CreateFileSystem(withMod: false);

        var aptFile = LoadMainMenu(fileSystem);

        Assert.Equal(11, aptFile.ImageMap.Mapping[0].TextureId);
    }

    /// <summary>
    /// The image map is optional. Age of the Ring's SkyrimMenu.apt — imported by its MainMenu — has
    /// no .dat in the mod, in the base game or in any archive, so a movie without one has to load
    /// with an empty mapping rather than fail.
    /// </summary>
    [Fact]
    public void AnAptWithNoImageMapAnywhereLoadsWithAnEmptyMapping()
    {
        WriteLooseFile(_mod, "MainMenu.apt", CreateAptData());
        WriteLooseFile(_mod, "MainMenu.const", CreateConstData());

        using var fileSystem = CreateFileSystem();

        var aptFile = LoadMainMenu(fileSystem);

        Assert.Empty(aptFile.ImageMap.Mapping);
    }

    [Fact]
    public void AMissingConstantFileNamesThePathItLookedFor()
    {
        WriteLooseFile(_mod, "MainMenu.apt", CreateAptData());

        using var fileSystem = CreateFileSystem();

        var exception = Assert.Throws<FileNotFoundException>(() => LoadMainMenu(fileSystem));

        Assert.Equal("MainMenu.const", exception.FileName);
    }

    [Fact]
    public void AnImageMapCannotBeLoadedFromAMissingEntry()
    {
        var exception = Assert.Throws<ArgumentNullException>(() => ImageMap.FromFileSystemEntry(null));

        Assert.Equal("entry", exception.ParamName);
        Assert.Contains(".dat", exception.Message);
    }
}
