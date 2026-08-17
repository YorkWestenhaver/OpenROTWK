using OpenSage.FileFormats.Big;

namespace OpenSage.IO;

public sealed class BigFileSystem : FileSystem
{
    private readonly BigDirectory _rootDirectory;
    private readonly BigFileSystemOptions _options;

    public BigFileSystemOptions Options => _options;

    public BigFileSystem(string rootDirectory)
        : this(rootDirectory, BigFileSystemOptions.BaseGame)
    {
    }

    public BigFileSystem(string rootDirectory, BigFileSystemOptions options)
    {
        _options = options;
        _rootDirectory = new BigDirectory();

        SkudefReader.Read(rootDirectory, options.LoadOrder, AddBigArchive);
    }

    /// <summary>
    /// Registers a single <c>.big</c> archive, in the same way the engine's
    /// <c>ArchiveFileSystem::loadBigFilesFromDirectory</c> does: every entry claims its normalised
    /// path in one shared tree, and an already-claimed path is only replaced when this layer
    /// registers with <c>overwrite = TRUE</c>.
    /// </summary>
    private void AddBigArchive(string path)
    {
        var overwrite = _options.ConflictResolution == BigArchiveConflictResolution.LastWins;

        var bigArchive = AddDisposable(new BigArchive(path));

        foreach (var bigArchiveEntry in bigArchive.Entries)
        {
            var directoryParts = bigArchiveEntry.FullName.Split('\\', '/');

            var bigDirectory = _rootDirectory;
            for (var i = 0; i < directoryParts.Length - 1; i++)
            {
                bigDirectory = bigDirectory.GetOrCreateDirectory(directoryParts[i]);
            }

            var fileName = directoryParts[directoryParts.Length - 1];

            // Engine equivalent: if (find(name) == end() || overwrite) { store }
            if (overwrite || !bigDirectory.Files.ContainsKey(fileName))
            {
                bigDirectory.Files[fileName] = bigArchiveEntry;
            }
        }
    }

    /// <summary>
    /// The path of the <c>.big</c> archive that owns <paramref name="filePath"/>, or null if no
    /// archive in this layer provides it. Diagnostics / conformance-test hook.
    /// </summary>
    public string? GetArchiveFilePath(string filePath)
    {
        return FindEntry(filePath)?.Archive.FilePath;
    }

    public override IEnumerable<FileSystemEntry> GetFilesInDirectory(
        string directoryPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        var search = new SearchPattern(searchPattern);

        var bigDirectory = _rootDirectory;

        if (directoryPath != "")
        {
            var directoryParts = NormalizeFilePath(directoryPath).Split(Path.DirectorySeparatorChar);
            for (var i = 0; i < directoryParts.Length; i++)
            {
                if (!bigDirectory.Directories.TryGetValue(directoryParts[i], out bigDirectory))
                {
                    return Enumerable.Empty<FileSystemEntry>();
                }
            }
        }

        return GetFilesInDirectory(bigDirectory, search, searchOption);
    }

    private IEnumerable<FileSystemEntry> GetFilesInDirectory(
        BigDirectory bigDirectory,
        SearchPattern searchPattern,
        SearchOption searchOption)
    {
        foreach (var file in bigDirectory.Files.Values)
        {
            if (!searchPattern.Match(file.FullName))
            {
                continue;
            }

            yield return CreateFileSystemEntry(file);
        }

        if (searchOption == SearchOption.AllDirectories)
        {
            foreach (var directory in bigDirectory.Directories.Values)
            {
                foreach (var file in GetFilesInDirectory(directory, searchPattern, searchOption))
                {
                    yield return file;
                }
            }
        }
    }

    public override FileSystemEntry? GetFile(string filePath)
    {
        var file = FindEntry(filePath);

        return file != null
            ? CreateFileSystemEntry(file)
            : null;
    }

    private BigArchiveEntry? FindEntry(string filePath)
    {
        var directoryParts = NormalizeFilePath(filePath).Split(Path.DirectorySeparatorChar);

        var bigDirectory = _rootDirectory;
        for (var i = 0; i < directoryParts.Length - 1; i++)
        {
            if (!bigDirectory.Directories.TryGetValue(directoryParts[i], out bigDirectory))
            {
                return null;
            }
        }

        var fileName = directoryParts[directoryParts.Length - 1];

        return bigDirectory.Files.TryGetValue(fileName, out var file)
            ? file
            : null;
    }

    private FileSystemEntry CreateFileSystemEntry(BigArchiveEntry entry)
    {
        return new FileSystemEntry(
            this,
            NormalizeFilePath(entry.FullName),
            entry.Length,
            entry.Open);
    }

    private sealed class BigDirectory
    {
        public readonly Dictionary<string, BigDirectory> Directories = new Dictionary<string, BigDirectory>(StringComparer.InvariantCultureIgnoreCase);
        public readonly Dictionary<string, BigArchiveEntry> Files = new Dictionary<string, BigArchiveEntry>(StringComparer.InvariantCultureIgnoreCase);

        public BigDirectory GetOrCreateDirectory(string directoryName)
        {
            if (!Directories.TryGetValue(directoryName, out var directory))
            {
                directory = new BigDirectory();
                Directories.Add(directoryName, directory);
            }
            return directory;
        }
    }
}
