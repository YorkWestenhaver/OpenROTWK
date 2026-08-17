#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.IO;
using OpenSage.Mods.BuiltIn;
using OpenSage.Utilities;

namespace OpenSage.Tests.Data.Ini;

/// <summary>
/// A simple in-memory <see cref="FileSystem"/> for parser tests that need
/// mock INI files without a real game installation.
/// </summary>
internal sealed class InMemoryFileSystem : FileSystem
{
    private readonly Dictionary<string, string> _files = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryFileSystem AddFile(string filePath, string contents)
    {
        _files[NormalizeFilePath(filePath)] = contents;
        return this;
    }

    public override FileSystemEntry? GetFile(string filePath)
    {
        filePath = NormalizeFilePath(filePath);
        return _files.TryGetValue(filePath, out var contents)
            ? CreateEntry(filePath, contents)
            : null;
    }

    public override IEnumerable<FileSystemEntry> GetFilesInDirectory(
        string directoryPath,
        string searchPattern = "*",
        SearchOption searchOption = SearchOption.TopDirectoryOnly)
    {
        directoryPath = NormalizeFilePath(directoryPath);
        foreach (var (path, contents) in _files)
        {
            if (path.StartsWith(directoryPath, StringComparison.OrdinalIgnoreCase))
            {
                yield return CreateEntry(path, contents);
            }
        }
    }

    private FileSystemEntry CreateEntry(string filePath, string contents)
    {
        return new FileSystemEntry(
            this,
            filePath,
            (uint)contents.Length,
            () => new MemoryStream(Encoding.ASCII.GetBytes(contents)));
    }
}

/// <summary>
/// Drives the real <see cref="IniParser"/> + <see cref="AssetStore"/> over
/// in-memory INI text, with no game data or graphics device required.
/// </summary>
internal sealed class IniParseTestContext
{
    private static readonly Encoding LocaleSpecificEncoding;

    static IniParseTestContext()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        LocaleSpecificEncoding = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
    }

    public InMemoryFileSystem FileSystem { get; }
    public AssetStore AssetStore { get; }
    public IniDataContext DataContext { get; }
    public SageGame Game { get; }

    public IniParseTestContext(SageGame game = SageGame.Bfme2Rotwk)
    {
        Game = game;
        FileSystem = new InMemoryFileSystem();
        DataContext = new IniDataContext();

        // INI parsing only creates lazy asset references, so no graphics objects are needed.
        AssetStore = new AssetStore(
            game,
            FileSystem,
            LanguageUtility.ReadCurrentLanguage(GameDefinition.FromGame(game), FileSystem),
            null,
            null,
            null,
            null,
            GameDefinition.FromGame(game).CreateAssetLoadStrategy());
        AssetStore.PushScope();
    }

    public IniParser CreateParser(string source, string filePath = @"Data\INI\test.ini")
    {
        return new IniParser(
            source,
            filePath,
            Path.GetDirectoryName(filePath)!,
            FileSystem,
            AssetStore,
            Game,
            DataContext,
            LocaleSpecificEncoding);
    }

    /// <summary>
    /// Parses <paramref name="source"/> as a whole INI file, sharing this
    /// context's asset store and #define table.
    /// </summary>
    public IniParser ParseFileText(string source, string filePath = @"Data\INI\test.ini")
    {
        var parser = CreateParser(source, filePath);
        parser.ParseFile();
        return parser;
    }
}
