// N14a driver CLI: --exclude, --stream-only, --arch-stamp, --retail-lobby-wipe.
//
// These tests drive the REAL Program.Main entry point in-process (internal, granted via
// InternalsVisibleTo) rather than spawning a subprocess, so they exercise the actual arg
// parser and file-writing path a real invocation goes through. All facts in this class share
// one xunit test class (=> one collection => sequential by default), which matters because
// they redirect the process-wide Console.Out/Error; RunDriver additionally serializes under a
// lock in case that default ever changes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using OpenSage.SimCore.DumpDiff;
using Xunit;

namespace OpenSage.SimCore.ScenarioDriver.Tests;

public class ScenarioDriverCliTests
{
    private static readonly object ConsoleLock = new();

    // The stand-in templates job005_spawn_fight.map's authored scripts spawn - copied
    // verbatim from OpenSage.Game.Tests/Logic/Script/SimMapRunTests.cs's `Definitions` so the
    // CLI-level test exercises the identical fixture the engine-level test already proved.
    private const string Job005Definitions = @"
Weapon MapTestSword
  AttackRange = 500
  DamageNugget
    Damage = 10
    Radius = 0.0
    DamageType = SLASH
    DeathType = NORMAL
  End
End

Object GondorFighterHorde
  KindOf = INFANTRY CAN_ATTACK
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 300
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY MapTestSword
  End
End

Object MordorFighterHorde
  KindOf = INFANTRY CAN_ATTACK
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 300
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY MapTestSword
  End
End
";

    private static (int ExitCode, string StdOut, string StdErr) RunDriver(params string[] args)
    {
        lock (ConsoleLock)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            var outWriter = new StringWriter { NewLine = "\n" };
            var errWriter = new StringWriter { NewLine = "\n" };
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);
                var exitCode = Program.Main(args);
                return (exitCode, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    private static string NewTempFile(string extension)
    {
        return Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + extension);
    }

    private static string WriteEmptySchedule()
    {
        var path = NewTempFile(".json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schema = "bfme2-harness/injection-schedule/v1",
            frames = Array.Empty<object>(),
        }));
        return path;
    }

    // -----------------------------------------------------------------------
    // --exclude
    // -----------------------------------------------------------------------

    [Fact]
    public void Exclude_ZeroesVectorSlotAndOmitsChannelRecords()
    {
        var schedule = WriteEmptySchedule();
        var outPath = NewTempFile(".ddump");

        var (exitCode, _, _) = RunDriver(
            "--schedule", schedule, "--out", outPath,
            "--until-frame", "10", "--checkpoint-interval", "10",
            "--exclude", "Players");
        Assert.Equal(0, exitCode);

        var lines = File.ReadAllLines(outPath);

        // Players is ordinal 7 of CrcChannels.Count=10; the V line lays out
        // "V <frame> <combined> <c0> <c1> ... <c9>", so index 3+7 in the split line.
        var vLines = lines.Where(l => l.StartsWith("V ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(vLines);
        foreach (var line in vLines)
        {
            var tokens = line.Split(' ');
            Assert.Equal("00000000", tokens[3 + 7]);
        }

        Assert.DoesNotContain(lines, l => l.StartsWith("C 7 ", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.StartsWith("E 7 ", StringComparison.Ordinal));

        // Objects (0) and LogicRandom (1) are unaffected: still walked.
        Assert.Contains(lines, l => l.StartsWith("C 0 Objects", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("C 1 LogicRandom", StringComparison.Ordinal));
    }

    [Fact]
    public void Exclude_UnknownChannelName_ExitsTwo()
    {
        var schedule = WriteEmptySchedule();
        var outPath = NewTempFile(".ddump");

        var (exitCode, _, stdErr) = RunDriver(
            "--schedule", schedule, "--out", outPath,
            "--until-frame", "5", "--exclude", "NotAChannel");

        Assert.Equal(2, exitCode);
        Assert.Contains("unknown --exclude channel: NotAChannel", stdErr, StringComparison.Ordinal);
        Assert.False(File.Exists(outPath));
    }

    [Fact]
    public void Exclude_SummaryLineEchoesExclusionSet()
    {
        var schedule = WriteEmptySchedule();
        var outPath = NewTempFile(".ddump");

        var (exitCode, stdOut, _) = RunDriver(
            "--schedule", schedule, "--out", outPath,
            "--until-frame", "5", "--exclude", "Players", "--exclude", "Taint");
        Assert.Equal(0, exitCode);
        Assert.Contains("excluded=Players,Taint", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void NoExclude_SummaryLineReportsNone()
    {
        var schedule = WriteEmptySchedule();
        var outPath = NewTempFile(".ddump");

        var (exitCode, stdOut, _) = RunDriver(
            "--schedule", schedule, "--out", outPath, "--until-frame", "5");
        Assert.Equal(0, exitCode);
        Assert.Contains("excluded=none", stdOut, StringComparison.Ordinal);
    }

    // -----------------------------------------------------------------------
    // Run-twice determinism, WITH an exclusion applied (the seam must not break it)
    // -----------------------------------------------------------------------

    [Fact]
    public void ExcludedRun_TwiceIsByteIdentical()
    {
        var schedule = WriteEmptySchedule();
        var outA = NewTempFile(".ddump");
        var outB = NewTempFile(".ddump");

        var argsA = new[]
        {
            "--schedule", schedule, "--out", outA, "--seed", "0xC0FFEE",
            "--until-frame", "30", "--checkpoint-interval", "10", "--exclude", "Players",
        };
        var argsB = new[]
        {
            "--schedule", schedule, "--out", outB, "--seed", "0xC0FFEE",
            "--until-frame", "30", "--checkpoint-interval", "10", "--exclude", "Players",
        };

        Assert.Equal(0, RunDriver(argsA).ExitCode);
        Assert.Equal(0, RunDriver(argsB).ExitCode);

        Assert.Equal(File.ReadAllText(outA), File.ReadAllText(outB));
    }

    // -----------------------------------------------------------------------
    // --stream-only
    // -----------------------------------------------------------------------

    [Fact]
    public void StreamOnly_EqualsHeaderPlusVLinesOfFullDump()
    {
        var schedule = WriteEmptySchedule();
        var fullPath = NewTempFile(".ddump");
        var streamPath = NewTempFile(".ddump");

        var baseArgs = new[]
        {
            "--schedule", schedule, "--seed", "0xB00",
            "--until-frame", "20", "--checkpoint-interval", "10",
        };

        Assert.Equal(0, RunDriver(baseArgs.Concat(new[] { "--out", fullPath }).ToArray()).ExitCode);
        Assert.Equal(0, RunDriver(baseArgs.Concat(new[] { "--out", streamPath, "--stream-only" }).ToArray()).ExitCode);

        var fullLines = File.ReadAllLines(fullPath);
        var expected = new List<string> { fullLines[0] }; // header
        expected.AddRange(fullLines.Where(l => l.StartsWith("V ", StringComparison.Ordinal)));

        var actual = File.ReadAllLines(streamPath);
        Assert.Equal(expected, actual);

        // Sanity: the full dump actually had field records to strip, or this test would pass
        // vacuously.
        Assert.Contains(fullLines, l => l.StartsWith("R ", StringComparison.Ordinal) || l.StartsWith("C ", StringComparison.Ordinal));
    }

    // -----------------------------------------------------------------------
    // --arch-stamp
    // -----------------------------------------------------------------------

    [Fact]
    public void ArchStamp_EmitsArchCommentAfterHeader()
    {
        var schedule = WriteEmptySchedule();
        var outPath = NewTempFile(".ddump");

        var (exitCode, _, _) = RunDriver(
            "--schedule", schedule, "--out", outPath,
            "--until-frame", "5", "--arch-stamp");
        Assert.Equal(0, exitCode);

        var lines = File.ReadAllLines(outPath);
        Assert.Equal("# opensage-deepdump v2", lines[0]);
        // Canonical metadata shape is "# key=value" (DumpDiff.DumpParser.TryHarvestMetadata) --
        // arch and rid are separate lines, never packed onto one line together.
        Assert.StartsWith("# arch=", lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain(" ", lines[1].Substring("# arch=".Length), StringComparison.Ordinal);
        Assert.StartsWith("# rid=", lines[2], StringComparison.Ordinal);
    }

    // Cross-tool round-trip: the driver (this project) and DumpDiff.DumpParser (a sibling
    // project, referenced only by this test project) each independently document the "# key=
    // value" metadata convention in their own comments. Nothing enforced they agree -- that's
    // exactly how the arch/exclude shape mismatch shipped to main unreconciled. This test
    // proves the two sides actually interoperate: it runs the real driver and feeds its real
    // output file through the real parser, not a hand-authored fixture string standing in for
    // either side.
    [Fact]
    public void ArchAndExcludeMetadata_RoundTripsThroughDumpDiffParser()
    {
        var schedule = WriteEmptySchedule();
        var outPath = NewTempFile(".ddump");

        var (exitCode, _, _) = RunDriver(
            "--schedule", schedule, "--out", outPath,
            "--until-frame", "5", "--arch-stamp",
            "--exclude", "Players", "--exclude", "Taint");
        Assert.Equal(0, exitCode);

        var dump = DumpParser.Parse(File.ReadAllText(outPath));

        Assert.Equal(RuntimeInformation.ProcessArchitecture.ToString(), dump.Metadata["arch"]);
        Assert.Equal(RuntimeInformation.RuntimeIdentifier, dump.Metadata["rid"]);
        Assert.Equal("Players,Taint", dump.Metadata["exclude"]);
    }

    // -----------------------------------------------------------------------
    // --retail-lobby-wipe (map-v1, job005_spawn_fight.map)
    // -----------------------------------------------------------------------

    private static string WriteJob005Ini()
    {
        var path = NewTempFile(".ini");
        File.WriteAllText(path, Job005Definitions);
        return path;
    }

    [Fact]
    public void RetailLobbyWipe_Job005_SuppressesAuthoredScriptSpawns()
    {
        var mapPath = Path.Combine("Assets", "job005_spawn_fight.map");
        var iniPath = WriteJob005Ini();

        var unwipedOut = NewTempFile(".ddump");
        var wipedOut = NewTempFile(".ddump");

        var (unwipedExit, unwipedStdOut, _) = RunDriver(
            "--scenario", "map-v1", "--map", mapPath, "--ini", iniPath,
            "--out", unwipedOut, "--seed", "0xB00", "--until-frame", "5", "--ignore-map-exit");
        Assert.Equal(0, unwipedExit);

        var (wipedExit, wipedStdOut, _) = RunDriver(
            "--scenario", "map-v1", "--map", mapPath, "--ini", iniPath,
            "--out", wipedOut, "--seed", "0xB00", "--until-frame", "5", "--ignore-map-exit",
            "--retail-lobby-wipe");
        Assert.Equal(0, wipedExit);

        var unwipedObjects = ExtractObjectCount(unwipedStdOut);
        var wipedObjects = ExtractObjectCount(wipedStdOut);

        // job005's ObjectsList holds only waypoints (SimMapRunTests.
        // Job005Map_RegistersMapTeamsAndWaypoints) - every spawned object comes from the
        // authored scripts. The wipe strips ScnAttacker/ScnDefender's script lists
        // (SimMapRunTests.Job005Map_RetailLobbyWipe_AuthoredScenarioScriptsDoNotRun), so the
        // wiped run must spawn strictly fewer objects than the unwiped one over the same
        // window - the CLI-level proof that the flag reaches SimMapRun's compiled program.
        Assert.True(wipedObjects < unwipedObjects,
            $"expected wiped ({wipedObjects}) < unwiped ({unwipedObjects})");
    }

    [Fact]
    public void RetailLobbyWipe_DefaultOff_SummaryReportsFalse()
    {
        var mapPath = Path.Combine("Assets", "job005_spawn_fight.map");
        var iniPath = WriteJob005Ini();
        var outPath = NewTempFile(".ddump");

        var (exitCode, stdOut, _) = RunDriver(
            "--scenario", "map-v1", "--map", mapPath, "--ini", iniPath,
            "--out", outPath, "--until-frame", "1", "--ignore-map-exit");
        Assert.Equal(0, exitCode);
        Assert.Contains("retailLobbyWipe=False", stdOut, StringComparison.Ordinal);
    }

    [Fact]
    public void RetailLobbyWipe_Flag_SummaryReportsTrue()
    {
        var mapPath = Path.Combine("Assets", "job005_spawn_fight.map");
        var iniPath = WriteJob005Ini();
        var outPath = NewTempFile(".ddump");

        var (exitCode, stdOut, _) = RunDriver(
            "--scenario", "map-v1", "--map", mapPath, "--ini", iniPath,
            "--out", outPath, "--until-frame", "1", "--ignore-map-exit", "--retail-lobby-wipe");
        Assert.Equal(0, exitCode);
        Assert.Contains("retailLobbyWipe=True", stdOut, StringComparison.Ordinal);

        // This CLI flag is the only thing the workbench-side run-manifest (run-manifest-v1
        // schema, engine.retailLobbyWipe) was waiting on - see this flag's engineFollowUp
        // note. The manifest JSON itself is produced by the workbench's scenariogen, outside
        // this repo; the driver's own contract ends at exposing the flag and echoing its
        // effect here, which is what this assertion pins.
    }

    private static int ExtractObjectCount(string summary)
    {
        // "...checkpoints=N objects=M finalCombined=..."
        const string marker = "objects=";
        var start = summary.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = summary.IndexOf(' ', start);
        return int.Parse(summary[start..end]);
    }
}
