using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using NLog;
using OpenSage.IO;
using OpenSage.Utilities;
using OpenSage.Utilities.Extensions;

namespace OpenSage.Data;

public sealed class RegistryKeyPath(string key, string valueName, string append = null)
{
    public readonly string Key = key;
    public readonly string ValueName = valueName;

    // This is required because one possible registry key for the Generals + ZH bundle points to the
    // root directory of the bundle.
    public readonly string Append = append;
}

public sealed class GameInstallation
{
    private static readonly Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public static IEnumerable<GameInstallation> FindAll(IEnumerable<IGameDefinition> gameDefinitions)
    {
        return InstallationLocators
            .GetAllForPlatform()
            .SelectMany(x => gameDefinitions.SelectMany(y => x.FindInstallations(y)));
    }

    public IGameDefinition Game { get; }
    public string Path { get; }

    /// <summary>
    /// The engine's <c>-mod</c> argument: either a directory (loose files plus its <c>*.BIG</c>
    /// archives) or a single <c>.big</c> archive. Null when no mod is active.
    /// </summary>
    public string ModPath { get; }

    private readonly GameInstallation _baseGameInstallation;

    public GameInstallation(IGameDefinition game, string path, GameInstallation baseGame = null, string modPath = null)
    {
        Game = game;
        Path = path;
        ModPath = modPath;
        _baseGameInstallation = baseGame;
    }

    /// <summary>The same installation with a <c>-mod</c> overlay applied.</summary>
    public GameInstallation WithMod(string modPath)
    {
        return new GameInstallation(Game, Path, _baseGameInstallation, modPath);
    }

    /// <summary>
    /// Generals and Zero Hour keep OpenSAGE's existing (empirically tuned) archive ordering and
    /// last-wins registration; every other SAGE title uses the engine-faithful first-wins model.
    /// </summary>
    private static BigFileSystemOptions BaseGameBigOptions(IGameDefinition game)
    {
        return game.Game is SageGame.CncGenerals or SageGame.CncGeneralsZeroHour
            ? BigFileSystemOptions.GeneralsZeroHour
            : BigFileSystemOptions.BaseGame;
    }

    /// <summary>
    /// The engine keeps one "mod or loose install" flag. It is set by a <c>-mod</c> argument, and
    /// also when <c>shaders.big</c> is absent from the game directory (a developer / loose install).
    /// While the flag is set, loose files are probed <em>before</em> the archives; while it is
    /// clear — a stock retail install — the archives win and the loose file is only a fallback.
    /// </summary>
    private bool IsModFlagSet
    {
        get
        {
            if (ModPath != null)
            {
                return true;
            }

            try
            {
                return !Directory
                    .EnumerateFiles(Path, "shaders.big", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                    .Any();
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    public FileSystem CreateFileSystem()
    {
        var looseLayers = new List<FileSystem>();
        var archiveLayers = new List<FileSystem>();

        // -mod layers sit above everything. Both of the engine's mod archive paths register with
        // overwrite = TRUE, i.e. last-wins within the mod layer.
        if (ModPath != null)
        {
            if (Directory.Exists(ModPath))
            {
                looseLayers.Add(new DiskFileSystem(ModPath));
                archiveLayers.Add(BigFileSystem.FromArchives(
                    BigFileSystem.EnumerateModArchives(ModPath),
                    BigFileSystemOptions.ModOverlay));
            }
            else if (File.Exists(ModPath))
            {
                archiveLayers.Add(BigFileSystem.FromArchives(
                    new[] { ModPath },
                    BigFileSystemOptions.ModOverlay));
            }
            else
            {
                Logger.Warn($"-mod path does not exist: {ModPath}");
            }
        }

        archiveLayers.Add(new BigFileSystem(Path, BaseGameBigOptions(Game)));

        if (_baseGameInstallation != null)
        {
            archiveLayers.Add(new BigFileSystem(_baseGameInstallation.Path, BaseGameBigOptions(_baseGameInstallation.Game)));
        }

        var gameDirectory = new DiskFileSystem(Path);

        // Exactly one loose-file attempt happens, and the mod flag decides which side of the
        // archives it sits on.
        var layers = IsModFlagSet
            ? looseLayers.Append(gameDirectory).Concat(archiveLayers)
            : looseLayers.Concat(archiveLayers).Append(gameDirectory);

        return new CompositeFileSystem(layers.ToArray());
    }
}

public interface IInstallationLocator
{
    IEnumerable<GameInstallation> FindInstallations(IGameDefinition game);
}

public class EnvironmentInstallationLocator : IInstallationLocator
{
    public IEnumerable<GameInstallation> FindInstallations(IGameDefinition game)
    {
        var identifier = game.Identifier.ToUpperInvariant() + "_PATH";
        var path = Environment.GetEnvironmentVariable(identifier) ??
                   Environment.GetEnvironmentVariable(identifier, EnvironmentVariableTarget.User);
        if (path == null || !Directory.Exists(path))
        {
            return [];
        }

        var installations = new GameInstallation[] { new(game, path, game.BaseGame != null ? FindInstallations(game.BaseGame).First() : null) };

        return installations;
    }
}

public class RegistryInstallationLocator : IInstallationLocator
{

    // Validates paths to directories. Removes duplicates.
    private static IEnumerable<string> GetValidPaths(IEnumerable<string> paths)
    {
        return paths
            .WhereNot(string.IsNullOrWhiteSpace)
            .Distinct()
            .Where(Directory.Exists);
    }

    private static readonly Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public IEnumerable<GameInstallation> FindInstallations(IGameDefinition game)
    {
        GameInstallation baseGameInstallation = null;

        if (game.BaseGame != null)
        {
            // TODO: Allow selecting one of these?
            baseGameInstallation = FindInstallations(game.BaseGame).FirstOrDefault();

            if (baseGameInstallation == null)
            {
                Logger.Warn("No game installations found");
                return Enumerable.Empty<GameInstallation>();
            }
        }

        var paths = game.RegistryKeys.Select(key => RegistryUtility.GetRegistryValue(key));

        var installations = GetValidPaths(paths)
            .Select(p => new GameInstallation(game, p, baseGameInstallation))
            .ToList();

        return installations;
    }
}

public static class InstallationLocators
{
    public static IEnumerable<IInstallationLocator> GetAllForPlatform()
    {
        yield return new EnvironmentInstallationLocator();
        yield return new SteamInstallationLocator();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return new RegistryInstallationLocator();
        }
    }

    public static IEnumerable<GameInstallation> FindAllInstallations(IGameDefinition game)
    {
        var locators = GetAllForPlatform();
        var result = new List<GameInstallation>();
        foreach (var locator in locators)
        {
            var installations = locator.FindInstallations(game);
            foreach (var installation in installations)
            {
                if (!result.Contains(installation))
                {
                    result.Add(installation);
                }
            }
        }
        return result;
    }
}
