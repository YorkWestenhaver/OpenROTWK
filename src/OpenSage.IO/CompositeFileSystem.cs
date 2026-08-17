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
                return fileSystemEntry;
            }
        }

        return null;
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

                yield return fileSystemEntry;
            }
        }
    }
}
