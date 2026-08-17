using System;
using System.IO;
using System.Linq;
using OpenSage.IO;
using Xunit;

namespace OpenSage.Tests.IO;

public sealed class CompositeFileSystemTests : IDisposable
{
    private readonly string _root;

    public CompositeFileSystemTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "opensage-composite-" + Guid.NewGuid().ToString("N"));
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

    private string WriteFile(string layer, string relativePath, string contents)
    {
        var fullPath = Path.Combine(_root, layer, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, contents);
        return fullPath;
    }

    /// <summary>
    /// A mod's loose tree spells paths differently from the archives it shadows
    /// ('data/ini/weapon.ini' vs 'Data\INI\Weapon.ini'). Under a case-sensitive dedup both leak
    /// through and the shadowed copy gets parsed as well.
    /// </summary>
    [Fact]
    public void EnumerationDedupesPathsCaseInsensitively()
    {
        WriteFile("mod", Path.Combine("data", "ini", "weapon.ini"), "mod");
        WriteFile("base", Path.Combine("data", "ini", "Weapon.ini"), "base");

        using var fileSystem = new CompositeFileSystem(
            new DiskFileSystem(Path.Combine(_root, "mod")),
            new DiskFileSystem(Path.Combine(_root, "base")));

        var entries = fileSystem
            .GetFilesInDirectory(Path.Combine("data", "ini"), "*.ini", SearchOption.AllDirectories)
            .ToList();

        var entry = Assert.Single(entries);

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        Assert.Equal("mod", reader.ReadToEnd());
    }

    [Fact]
    public void DistinctFilesFromEveryLayerAreStillEnumerated()
    {
        WriteFile("mod", Path.Combine("data", "ini", "weapon.ini"), "mod");
        WriteFile("base", Path.Combine("data", "ini", "Weapon.ini"), "base");
        WriteFile("base", Path.Combine("data", "ini", "armor.ini"), "base");

        using var fileSystem = new CompositeFileSystem(
            new DiskFileSystem(Path.Combine(_root, "mod")),
            new DiskFileSystem(Path.Combine(_root, "base")));

        var names = fileSystem
            .GetFilesInDirectory(Path.Combine("data", "ini"), "*.ini", SearchOption.AllDirectories)
            .Select(x => Path.GetFileName(x.FilePath).ToLowerInvariant())
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(new[] { "armor.ini", "weapon.ini" }, names);
    }

    [Fact]
    public void PathComparerTreatsSeparatorsAndCaseAsEqual()
    {
        Assert.True(FileSystem.PathComparer.Equals(@"data/ini/armor.ini", @"Data\INI\Armor.ini"));
        Assert.Equal(
            FileSystem.PathComparer.GetHashCode(@"data/ini/armor.ini"),
            FileSystem.PathComparer.GetHashCode(@"Data\INI\Armor.ini"));
        Assert.False(FileSystem.PathComparer.Equals(@"data\ini\armor.ini", @"data\ini\armour.ini"));
    }
}
