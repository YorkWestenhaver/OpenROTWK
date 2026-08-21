using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace OpenSage.SimCore.Tests;

/// <summary>
/// Build-order step 3 gate (api-freeze-v1 §6): the stray-<c>System.Random</c> census, kept as a
/// test rather than as a one-off grep so it cannot silently regrow.
/// <para>
/// The SIMCORE003 analyzer rule already bans ambient randomness inside OpenSage.SimCore and
/// inside anything listed in OpenSage.Game's <c>SimCoreScopedDirs.txt</c> - but that list is still
/// empty (no OpenSage.Game directory is float-free yet), so nothing mechanical guards OpenSage.Game
/// today. This source scan is the stand-in, and it retires the moment the analyzer's scope covers
/// the same ground. It lives in the SimCore suite because that is the determinism suite and it runs
/// clean on every platform.
/// </para>
/// </summary>
public class RandomSourceCensusTests
{
    /// <summary>
    /// Files in OpenSage.Game allowed to construct a <c>System.Random</c>. Each is client-side
    /// only: its draws can never reach simulation state, so they cost nothing in lockstep.
    /// Adding a line here is a determinism decision and should be argued for in review.
    /// </summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["Diagnostics/MainView.cs"] =
            "developer-only diagnostics UI; not compiled into any sim path",
        ["Graphics/ParticleSystems/ParticleSystemUtility.cs"] =
            "client-side particle rendering, already fixed-seeded; never observed by the sim",
    };

    [Fact]
    public void OpenSageGameHasNoUnvettedAmbientRandomSources()
    {
        var gameRoot = Path.Combine(FindRepositorySourceRoot(), "OpenSage.Game");
        Assert.True(Directory.Exists(gameRoot), $"expected OpenSage.Game at {gameRoot}");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(gameRoot, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(gameRoot, file).Replace('\\', '/');

            if (relative.StartsWith("obj/", StringComparison.Ordinal)
                || relative.StartsWith("bin/", StringComparison.Ordinal)
                || Allowed.ContainsKey(relative))
            {
                continue;
            }

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;

                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)
                    || code.StartsWith("///", StringComparison.Ordinal)
                    || code.StartsWith("*", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains("new Random(", StringComparison.Ordinal)
                    || line.Contains("new SystemRandom(", StringComparison.Ordinal))
                {
                    offenders.Add($"{relative}:{lineNumber}: {line.Trim()}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "ambient System.Random in OpenSage.Game (api-freeze-v1 F5: draw from IGame.CreateRandom, "
            + "or add the file to the allow-list with a reason):"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void AllowListEntriesStillExist()
    {
        // A stale allow-list is a quarantine hole: if a file moves, its entry must move with it.
        var gameRoot = Path.Combine(FindRepositorySourceRoot(), "OpenSage.Game");

        foreach (var (relative, reason) in Allowed)
        {
            Assert.True(
                File.Exists(Path.Combine(gameRoot, relative)),
                $"allow-listed file '{relative}' ({reason}) no longer exists; prune or repoint it");
        }
    }

    /// <summary>Walks up from the test binaries to the 'src' directory holding OpenSage.sln.</summary>
    private static string FindRepositorySourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (directory.EnumerateFiles("OpenSage.sln").Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"could not locate OpenSage.sln above {AppContext.BaseDirectory}");
    }
}
