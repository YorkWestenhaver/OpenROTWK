namespace OpenSage.IO;

public abstract class FileSystem : DisposableBase
{
    public static string NormalizeFilePath(string filePath)
    {
        return filePath
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
    }

    /// <summary>
    /// Path identity as the engine defines it: every path is rewritten to a single separator and
    /// folded to lowercase before it is used as a key, at both insert and lookup time. So
    /// <c>data/ini/armor.ini</c> and <c>Data\INI\Armor.ini</c> are one and the same path.
    /// </summary>
    public static readonly IEqualityComparer<string> PathComparer = new NormalizedPathComparer();

    private sealed class NormalizedPathComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return ReferenceEquals(x, y);
            }

            return string.Equals(NormalizeFilePath(x), NormalizeFilePath(y), StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode(string obj)
        {
            return StringComparer.OrdinalIgnoreCase.GetHashCode(NormalizeFilePath(obj));
        }
    }

    public abstract FileSystemEntry? GetFile(string filePath);

    public abstract IEnumerable<FileSystemEntry> GetFilesInDirectory(
        string directoryPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly);
}
