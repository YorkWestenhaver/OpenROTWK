using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using OpenSage.FileFormats.Big;
using OpenSage.IO;
using Xunit;

namespace OpenSage.Tests.IO;

/// <summary>
/// Data-free tests for the engine's BIG registration semantics: enumeration order
/// (lowercase-folded, archives before subdirectories) and conflict resolution
/// (first-wins for base layers, last-wins for <c>-mod</c> layers).
/// </summary>
public sealed class BigFileSystemPrecedenceTests : IDisposable
{
    private readonly string _root;

    public BigFileSystemPrecedenceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "opensage-bigprec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
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

    /// <summary>Writes a .big archive containing one text entry per (path, contents) pair.</summary>
    private string WriteArchive(string relativePath, params (string EntryPath, string Contents)[] entries)
    {
        var fullPath = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        using (var archive = new BigArchive(fullPath, BigArchiveMode.Create))
        {
            foreach (var (entryPath, contents) in entries)
            {
                var entry = archive.CreateEntry(entryPath);
                using var stream = entry.Open();
                var bytes = Encoding.ASCII.GetBytes(contents);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        return fullPath;
    }

    private static string ReadAllText(FileSystemEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }

    [Fact]
    public void FirstRegisteredArchiveKeepsAContestedPath()
    {
        // '#' (0x23) folds below 'i' (0x69), so #patch registers first and first-wins keeps it.
        WriteArchive("#patch.big", (@"data\ini\armor.ini", "patch"));
        WriteArchive("INI.big", (@"data\ini\armor.ini", "stale"));

        using var fileSystem = new BigFileSystem(_root, BigFileSystemOptions.BaseGame);

        Assert.Equal("patch", ReadAllText(fileSystem.GetFile(@"data\ini\armor.ini")!));
        Assert.Equal("#patch.big", Path.GetFileName(fileSystem.GetArchiveFilePath(@"data\ini\armor.ini")));
    }

    [Fact]
    public void PathIdentityIsCaseAndSeparatorInsensitive()
    {
        WriteArchive("#patch.big", (@"Data\INI\Armor.ini", "patch"));
        WriteArchive("INI.big", (@"data/ini/armor.ini", "stale"));

        using var fileSystem = new BigFileSystem(_root, BigFileSystemOptions.BaseGame);

        Assert.Equal("patch", ReadAllText(fileSystem.GetFile(@"DATA/ini/ARMOR.INI")!));
    }

    [Fact]
    public void BangSortsBeforeHashSortsBeforeUnderscoreSortsBeforeLetters()
    {
        // The empirical ROTWK order: !202timer, !new202music, #aotr_patch202, _202_widescreen,
        // _patch201, Audio, ... Each archive claims the same path; only the first may win.
        WriteArchive("!202timer.big", (@"data\x.ini", "bang"));
        WriteArchive("#aotr_patch202.big", (@"data\x.ini", "hash"));
        WriteArchive("_patch201.big", (@"data\x.ini", "underscore"));
        WriteArchive("Audio.big", (@"data\x.ini", "letter"));

        using var fileSystem = new BigFileSystem(_root, BigFileSystemOptions.BaseGame);

        Assert.Equal("bang", ReadAllText(fileSystem.GetFile(@"data\x.ini")!));
    }

    /// <summary>
    /// The trap that made two OpenSAGE bugs cancel out: .NET's OrdinalIgnoreCase folds to
    /// uppercase, putting '_' (0x5F) <em>after</em> letters; the engine's _memicmp folds to
    /// lowercase, putting it <em>before</em> them.
    /// </summary>
    [Fact]
    public void UnderscorePrefixedArchiveSortsBeforeLetteredArchives()
    {
        WriteArchive("_patch201.big", (@"art\w3d\x.w3d", "patch"));
        WriteArchive("W3D.big", (@"art\w3d\x.w3d", "stale"));

        using var fileSystem = new BigFileSystem(_root, BigFileSystemOptions.BaseGame);

        Assert.Equal("patch", ReadAllText(fileSystem.GetFile(@"art\w3d\x.w3d")!));
    }

    [Fact]
    public void ArchivesOfADirectoryAreRegisteredBeforeArchivesOfItsSubdirectories()
    {
        // 'apt' sorts before 'zzz.big' as a plain string, but the engine scans the root's *.big
        // first and only then apt\*.big, so the root archive must win.
        WriteArchive("zzz.big", (@"data\y.ini", "root"));
        WriteArchive(Path.Combine("apt", "a.big"), (@"data\y.ini", "subdirectory"));

        using var fileSystem = new BigFileSystem(_root, BigFileSystemOptions.BaseGame);

        Assert.Equal("root", ReadAllText(fileSystem.GetFile(@"data\y.ini")!));
    }

    [Fact]
    public void ModOverlayLayerIsLastWins()
    {
        // -mod archives register with overwrite = TRUE, so within that layer the last archive
        // registered — the alphabetically last one — wins.
        WriteArchive("aaa.big", (@"data\z.ini", "first"));
        WriteArchive("zzz.big", (@"data\z.ini", "last"));

        using var baseLayer = new BigFileSystem(_root, BigFileSystemOptions.BaseGame);
        Assert.Equal("first", ReadAllText(baseLayer.GetFile(@"data\z.ini")!));

        using var modLayer = new BigFileSystem(_root, BigFileSystemOptions.ModOverlay);
        Assert.Equal("last", ReadAllText(modLayer.GetFile(@"data\z.ini")!));
    }

    [Fact]
    public void ArchiveExtensionMatchingIsCaseInsensitive()
    {
        // The engine's -mod mask is literally "*.BIG"; on Win32 both masks match either casing.
        WriteArchive("Mod.BIG", (@"data\w.ini", "upper"));

        using var fileSystem = new BigFileSystem(_root, BigFileSystemOptions.BaseGame);

        Assert.NotNull(fileSystem.GetFile(@"data\w.ini"));
    }

    [Fact]
    public void NonContestedEntriesFromEveryArchiveRemainVisible()
    {
        WriteArchive("#patch.big", (@"data\a.ini", "a"));
        WriteArchive("INI.big", (@"data\a.ini", "stale"), (@"data\b.ini", "b"));

        using var fileSystem = new BigFileSystem(_root, BigFileSystemOptions.BaseGame);

        Assert.Equal("a", ReadAllText(fileSystem.GetFile(@"data\a.ini")!));
        Assert.Equal("b", ReadAllText(fileSystem.GetFile(@"data\b.ini")!));

        var listed = fileSystem
            .GetFilesInDirectory("data", "*.ini", SearchOption.TopDirectoryOnly)
            .Select(x => Path.GetFileName(x.FilePath))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(new List<string> { "a.ini", "b.ini" }, listed);
    }
}
