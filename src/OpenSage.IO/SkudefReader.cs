namespace OpenSage.IO;

internal static class SkudefReader
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly record struct SkudefVersion(string LanguageName, int VersionMajor, int VersionMinor) : IComparable<SkudefVersion>
    {
        public static SkudefVersion Parse(string fileName)
        {
            var body = Path.GetFileNameWithoutExtension(fileName);
            body = body[(body.IndexOf('_') + 1)..];
            var lastUnderscore = body.LastIndexOf('_');
            string languageName;
            string[] versions;
            if (lastUnderscore != -1)
            {
                languageName = body.Substring(0, lastUnderscore);
                versions = body[(lastUnderscore + 1)..].Split('.');
            }
            else
            {
                languageName = body;
                versions = ["0", "0"];
            }

            return new SkudefVersion
            {
                LanguageName = languageName,
                VersionMajor = int.Parse(versions[0]),
                VersionMinor = int.Parse(versions[1]),
            };
        }

        public int CompareTo(SkudefVersion other)
        {
            var result = VersionMajor - other.VersionMajor;
            return result == 0
                ? VersionMinor - other.VersionMinor
                : result;
        }
    }

    /// <summary>
    /// Case-insensitive ordinal comparer that folds to <em>lowercase</em>, matching the engine's
    /// <c>_memicmp</c>-based <c>less_than_nocase</c>. Differs from
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> (which folds to uppercase) exactly on the
    /// characters between <c>'Z'</c> (0x5A) and <c>'a'</c> (0x61) — most importantly <c>'_'</c>
    /// (0x5F), which the engine sorts <em>before</em> letters and .NET sorts after them.
    /// </summary>
    internal sealed class LowerInvariantOrdinalComparer : IComparer<string?>
    {
        public static readonly LowerInvariantOrdinalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            return string.CompareOrdinal(x?.ToLowerInvariant(), y?.ToLowerInvariant());
        }
    }

    public static void Read(string rootDirectory, Action<string> addBigArchive)
    {
        Read(rootDirectory, BigArchiveLoadOrder.Sage, addBigArchive);
    }

    public static void Read(string rootDirectory, BigArchiveLoadOrder loadOrder, Action<string> addBigArchive)
    {

        var skudefFiles = Directory.GetFiles(rootDirectory, "*.skudef", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });

        var skudefFile = skudefFiles
            .OrderBy(SkudefVersion.Parse)
            .LastOrDefault(); // TODO: This is not the right logic. needs to take into account the language.

        if (skudefFile is not null)
        {
            Logger.Info($"Selected Skudef file {skudefFile}");
        }

        // If no skudef (i.e. for pre-C&C3 games), use default one.
        using (var skudefFileContents = (skudefFile != null)
            ? (TextReader)new StreamReader(skudefFile)
            : new StringReader("add-bigs-recurse ."))
        {
            Read(rootDirectory, skudefFileContents, loadOrder, addBigArchive);
        }
    }

    private static void Read(string skudefDirectory, TextReader skudefReader, BigArchiveLoadOrder loadOrder, Action<string> addBigArchive)
    {
        while (skudefReader.ReadLine() is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var spaceIndex = line.IndexOf(' ');
            var command = line.Substring(0, spaceIndex);
            var parameter = line.Substring(spaceIndex + 1);
            var fullPath = FileSystem.NormalizeFilePath(Path.Combine(skudefDirectory, parameter));

            switch (command)
            {
                case "add-big":
                    addBigArchive(fullPath);
                    break;

                case "add-bigs-recurse":
                    foreach (var bigPath in GetBigFiles(fullPath, loadOrder))
                    {
                        addBigArchive(bigPath);
                    }
                    break;

                case "add-config":
                    using (var reader = new StreamReader(fullPath))
                    {
                        Read(Path.GetDirectoryName(fullPath)!, reader, loadOrder, addBigArchive);
                    }
                    break;
            }
        }
    }


    /// <summary>
    /// Enumerates all .big files in the specified directory and its subdirectories, in the order they should be loaded.
    /// </summary>
    internal static IEnumerable<string> GetBigFiles(string directory, BigArchiveLoadOrder loadOrder)
    {
        return loadOrder switch
        {
            BigArchiveLoadOrder.Sage => GetBigFilesSage(directory),
            BigArchiveLoadOrder.ZeroHour => GetBigFilesZeroHour(directory),
            _ => throw new ArgumentOutOfRangeException(nameof(loadOrder)),
        };
    }

    private static bool IsBigFile(string path)
    {
        // The engine's directory masks are "*.big" for the base layers and "*.BIG" for the -mod
        // layer; on Win32 both are case-insensitive, so a case-sensitive extension test here would
        // simply miss archives.
        return Path.GetExtension(path).Equals(".big", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The SAGE engine's enumeration order.
    /// <para>
    /// <c>ArchiveFileSystem::init</c> issues one <c>loadBigFilesFromDirectory</c> call per directory
    /// — the game root's <c>*.big</c> first, then <c>apt\*.big</c> — and each call walks an ordered
    /// <c>std::set</c> keyed on the <c>tolower</c>-folded file name. So: every archive of a
    /// directory, lower-fold-sorted, before any archive of a subdirectory.
    /// </para>
    /// </summary>
    private static IEnumerable<string> GetBigFilesSage(string directory)
    {
        var entries = Directory.GetFileSystemEntries(directory);

        foreach (var entry in entries
            .Where(x => !Directory.Exists(x) && IsBigFile(x))
            .OrderBy(Path.GetFileName, LowerInvariantOrdinalComparer.Instance))
        {
            yield return entry;
        }

        foreach (var subDirectory in entries
            .Where(Directory.Exists)
            .OrderBy(Path.GetFileName, LowerInvariantOrdinalComparer.Instance))
        {
            foreach (var bigFile in GetBigFilesSage(subDirectory))
            {
                yield return bigFile;
            }
        }
    }

    private static IEnumerable<string> GetBigFilesZeroHour(string directory)
    {
        // The OSS version of Generals / ZH loads .big files with FindFirstFile / FindNextFile, which means they are returned
        // in whatever order the file system decides to return them. In practice on Windows & NTFS this is case-insensitive
        // alphabetical order. For cross-platform compatibility, we need to sort the files ourselves.
        // However, it seems that even that is not enough, as the Steam Workshop update made some changes which add other
        // sorting criteria. We don't actually know what those criteria are as the source code for the update is not available.
        // So this is a guess based on the behavior of the Steam version of the game.
        var entries = Directory
            .GetFileSystemEntries(directory)
            // In the CD / Origin release of ZH, .big files from Generals were included in the same directory as the other .big files.
            // In the current Steam version, they are in a subdirectory. We need to make sure that the Generals .big files are loaded first,
            // so that Zero Hour can override them.
            .OrderByDescending(entry => entry.Contains("ZH_Generals"))
            .ThenBy(entry =>
            {
                var fileName = Path.GetFileNameWithoutExtension(entry);
                if (fileName == null)
                {
                    return 0;
                }
                if (fileName.EndsWith("ZH", StringComparison.OrdinalIgnoreCase))
                {
                    // The Zero Hour .big files need to be loaded after the Generals .big files.
                    // This can be a problem with pre-Steam versions.
                    return 1;
                }
                if (fileName.StartsWith("Patch", StringComparison.OrdinalIgnoreCase))
                {
                    // The Steam Workshop update added a couple of new PatchX.big files, which need to be loaded after the main .big files.
                    // Older versions of the game also had patch .big files, but it seems they either didn't override the main .big files
                    // or they happened to accidentally be loaded in the right order thanks to their names (& NTFS).
                    return 2;
                }
                return 0;
            })
            // And finally we sort alphabetically & case-insensitively to match the Windows behavior.
            .ThenBy(entry => entry, StringComparer.OrdinalIgnoreCase);

        // The final order for Zero Hour should be:
        // 1. Generals .big files
        // 2. Generals Patch .big files
        // 3. Zero Hour .big files
        // 4. Zero Hour Patch .big files
        foreach (var entry in entries)
        {
            if (Directory.Exists(entry))
            {
                // Handle directories recursively to ensure the correct order in subdirectories
                foreach (var bigFile in GetBigFilesZeroHour(entry))
                {
                    yield return bigFile;
                }
            }
            else if (IsBigFile(entry))
            {
                yield return entry;
            }
        }
    }
}
