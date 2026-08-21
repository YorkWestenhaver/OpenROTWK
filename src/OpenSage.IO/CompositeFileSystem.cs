namespace OpenSage.IO;

public sealed class CompositeFileSystem : FileSystem
{
    private readonly FileSystem[] _fileSystems;

    public CompositeFileSystem(params FileSystem[] fileSystems)
    {
        _fileSystems = fileSystems;

        foreach (var fileSystem in _fileSystems)
        {
            AddDisposable(fileSystem);
        }
    }

    public override FileSystemEntry? GetFile(string filePath)
    {
        foreach (var fileSystem in _fileSystems)
        {
            var fileSystemEntry = fileSystem.GetFile(filePath);
            if (fileSystemEntry != null)
            {
                return Rebind(fileSystemEntry);
            }
        }

        return null;
    }

    /// <summary>
    /// The engine has one file system, not one per archive: a file it opens through the layer
    /// stack can name a sibling — an <c>.apt</c>'s <c>.const</c>/<c>.dat</c>/<c>_geometry</c>
    /// sidecars, an <c>.ini</c>'s <c>9x</c> twin or its <c>#include</c> targets — and that sibling
    /// is resolved through the same stack, not through whichever layer happened to provide the
    /// first file. Callers do that by asking <see cref="FileSystemEntry.FileSystem"/>, so an entry
    /// handed out by this composite has to point back at the composite; leaving it pointing at the
    /// layer that produced it scopes sidecar lookups to that one layer, which is why a
    /// <c>-mod</c> that ships a loose <c>MainMenu.apt</c> without a <c>MainMenu.dat</c> could not
    /// see the base game's copy.
    /// </summary>
    private FileSystemEntry Rebind(FileSystemEntry entry)
    {
        return ReferenceEquals(entry.FileSystem, this)
            ? entry
            : new FileSystemEntry(this, entry.FilePath, entry.Length, entry.Open);
    }

    public override IEnumerable<FileSystemEntry> GetFilesInDirectory(
        string directoryPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        // The engine's path identity is case- and separator-insensitive (paths are lowercased and
        // '/'-to-'\'-normalised on both insert and lookup), so a file provided by a higher-priority
        // layer must shadow the lower-priority one even when the two spell it differently - as a
        // mod's loose 'data/ini/weapon.ini' and an archive's 'Data\INI\Weapon.ini' do.
        var paths = new HashSet<string>(PathComparer);

        foreach (var fileSystem in _fileSystems)
        {
            foreach (var fileSystemEntry in fileSystem.GetFilesInDirectory(directoryPath, searchPattern, searchOption))
            {
                if (paths.Contains(fileSystemEntry.FilePath))
                {
                    continue;
                }

                paths.Add(fileSystemEntry.FilePath);

                yield return Rebind(fileSystemEntry);
            }
        }
    }
}
