// R15 packet 5 gate tests (workbench research/design-sim-presentation-bridge.md §2 packet 5):
// the headed game can now write a deep-CRC dump behind --headed-crc.
//
// What these pin, in the order the acceptance states them:
//   1. HeadedCrcChannels builds the SAME three sources, in the SAME frozen CrcChannel order,
//      that the headless map-v1 scenario builds - Objects (0), LogicRandom (1), OracleView on
//      the Taint ordinal (6) - and refuses to build before a Scene3D exists.
//   2. The CrcCheckpoint phase body writes byte-for-byte what MapScenario.CrcCheckpoint writes
//      (ComputeDeepCheckpoint followed by CrcVector), so DumpDiff can compare a headed dump
//      against a headless one without either side special-casing the other. The test asserts
//      that by running BOTH code paths over two identically-seeded hosts and comparing the
//      resulting text, not by re-describing the format.
//   3. Flag OFF is inert: nothing attached, the loop's interval stays 0, the phase body is
//      never reached, and no bytes are produced.
//   4. The start preconditions refuse a scaled logic update and a missing dump path.
//
// Render-free by construction, like the packet-1..4 tests next door: the host is
// HeadlessSimGame (real GameLogic, real PartitionCellManager, no renderer, no files) and every
// dump goes to a StringWriter. Nothing here touches a GraphicsDevice or the filesystem.
//
// NOT tested here, deliberately: headed-vs-headless CRC EQUALITY on a real map. That is a
// round-3 item - it needs the L1 map fixes and a deterministic stimulus first, and the float
// AIUpdate/Locomotor chain a shipped AotR map moves units through is still unported. This
// packet delivers plumbing and format match only.

using System;
using System.Collections.Generic;
using System.IO;
using OpenSage.Logic.Sim;
using OpenSage.Logic.Sync;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Sync;

// Advancing a SimLoop runs HeadedSimSystems' EndFrame heartbeat, which reads GameTrace's
// session state - the same reason the packet-1..4 tests next door join this collection.
[Collection(GameTraceCollection.Name)]
public class HeadedCrcTests
{
    private const uint MatchSeed = 0x81D6E;

    private static HeadlessSimGame CreateHost() => new(SageGame.Bfme2, matchSeed: MatchSeed);

    /// <summary>
    /// The same wiring Game's constructor builds, with the CRC interval the caller asks for.
    /// 0 is the flag-off default: SimLoop then never calls the CrcCheckpoint body at all.
    /// </summary>
    private static (SimLoop Loop, HeadedSimSystems Systems) CreateLoop(
        HeadlessSimGame game,
        uint crcInterval = 0)
    {
        var systems = new HeadedSimSystems(game);
        var loop = new SimLoop(systems, systems)
        {
            CrcCheckpointIntervalInFrames = crcInterval,
        };
        return (loop, systems);
    }

    private static StringWriter NewDumpTarget() => new() { NewLine = "\n" };

    // ------------------------------------------------------------------ the channel set

    [Fact]
    public void TheChannelSetIsTheThreeSourcesInTheFrozenWalkOrder()
    {
        var game = CreateHost();

        var sources = HeadedCrcChannels.Build(game);

        Assert.Equal(
            new[] { CrcChannel.Objects, CrcChannel.LogicRandom, CrcChannel.Taint },
            new List<CrcChannel>(GetChannels(sources)));

        static IEnumerable<CrcChannel> GetChannels(IReadOnlyList<ICrcChannelSource> sources)
        {
            for (var i = 0; i < sources.Count; i++)
            {
                yield return sources[i].Channel;
            }
        }
    }

    [Fact]
    public void EveryBuiltChannelIsActive()
    {
        var game = CreateHost();

        foreach (var source in HeadedCrcChannels.Build(game))
        {
            Assert.True(source.IsActive, CrcChannels.NameOf(source.Channel));
        }
    }

    [Fact]
    public void TheCheckerAcceptsTheSetAsRegisteredInWalkOrder()
    {
        var game = CreateHost();

        // SyncChecker re-validates the frozen order itself and throws if it is ever perturbed
        // in HeadedCrcChannels; constructing one is therefore a real assertion, not a smoke test.
        var checker = HeadedCrcChannels.CreateChecker(game);

        Assert.False(checker.IsExcluded(CrcChannel.Objects));
        Assert.False(checker.IsExcluded(CrcChannel.LogicRandom));
        Assert.False(checker.IsExcluded(CrcChannel.Taint));
    }

    [Fact]
    public void BuildRefusesBeforeASceneExists()
    {
        var game = CreateHost();
        game.Scene3D = null;

        // IGame.GameEngine resolves through Scene3D, and Scene3D construction resets GameLogic:
        // building the channels early is a wrong-state bug, and it says so instead of NREing.
        var exception = Assert.Throws<InvalidOperationException>(() => HeadedCrcChannels.Build(game));
        Assert.Contains("Scene3D", exception.Message);
    }

    // ------------------------------------------------------- flag OFF is inert

    [Fact]
    public void WithNothingAttachedTheCheckpointBodyIsANoOp()
    {
        var game = CreateHost();
        var systems = new HeadedSimSystems(game);

        systems.CrcCheckpoint(new LogicFrame(0));

        Assert.Equal(0, systems.Checkpoints);
        Assert.Equal(0u, systems.FinalCombined);
    }

    [Fact]
    public void WithTheFlagOffTheLoopNeverReachesTheCheckpointBody()
    {
        var game = CreateHost();
        var (loop, systems) = CreateLoop(game, crcInterval: 0);

        loop.Advance();
        loop.Advance();
        loop.Advance();

        // Frame 0 is a checkpoint frame under any non-zero interval, so a run that reached the
        // body even once would show up here.
        Assert.Equal(0, systems.Checkpoints);
    }

    // ------------------------------------------------------- flag ON: cadence and format

    [Fact]
    public void AttachedCheckpointsFollowTheLoopInterval()
    {
        var game = CreateHost();
        var (loop, systems) = CreateLoop(game, crcInterval: 3);
        using var target = NewDumpTarget();
        var writer = new DeepCrcWriter(target, leaveOpen: true);
        systems.AttachCrc(HeadedCrcChannels.CreateChecker(game), writer);

        for (var i = 0; i < 7; i++)
        {
            loop.Advance();
        }

        // Frames 0, 3 and 6 of 0..6.
        Assert.Equal(3, systems.Checkpoints);
        Assert.Equal(new[] { 0u, 3u, 6u }, VectorFrames(target.ToString()));
    }

    [Fact]
    public void TheDumpWalksExactlyTheThreeRegisteredChannels()
    {
        var game = CreateHost();
        var systems = new HeadedSimSystems(game);
        using var target = NewDumpTarget();
        var writer = new DeepCrcWriter(target, leaveOpen: true);
        systems.AttachCrc(HeadedCrcChannels.CreateChecker(game), writer);

        systems.CrcCheckpoint(new LogicFrame(0));
        writer.Flush();

        var channelLines = new List<string>();
        foreach (var line in Lines(target.ToString()))
        {
            if (line.StartsWith("C ", StringComparison.Ordinal))
            {
                channelLines.Add(line);
            }
        }

        Assert.Equal(new[] { "C 0 Objects", "C 1 LogicRandom", "C 6 Taint" }, channelLines);
    }

    [Fact]
    public void TheVectorLineCarriesOneEntryPerChannelOrdinalWithZeroesWhereNoSourceIsRegistered()
    {
        var game = CreateHost();
        var systems = new HeadedSimSystems(game);
        using var target = NewDumpTarget();
        var writer = new DeepCrcWriter(target, leaveOpen: true);
        systems.AttachCrc(HeadedCrcChannels.CreateChecker(game), writer);

        systems.CrcCheckpoint(new LogicFrame(11));
        writer.Flush();

        var vector = LastVectorLine(target.ToString()).Split(' ');

        // "V", frame, combined, then one 8-hex entry per CrcChannel ordinal.
        Assert.Equal(3 + CrcChannels.Count, vector.Length);
        Assert.Equal("11", vector[1]);
        Assert.Equal($"{systems.FinalCombined:x8}", vector[2]);

        // Unregistered ordinals hold the positional zero the checkpoint message specifies.
        foreach (var ordinal in new[] { 2, 3, 4, 5, 7, 8, 9 })
        {
            Assert.Equal("00000000", vector[3 + ordinal]);
        }
    }

    [Fact]
    public void ThePhaseBodyWritesExactlyWhatTheHeadlessMapScenarioWrites()
    {
        // Two identically-seeded hosts, one dumped through the headed phase body and one
        // dumped through the literal MapScenario.CrcCheckpoint call sequence. Byte equality of
        // the two texts IS the format-match acceptance for this packet: it is what lets
        // DumpDiff compare a headed dump against a headless one.
        var headedGame = CreateHost();
        var headedSystems = new HeadedSimSystems(headedGame);
        using var headedTarget = NewDumpTarget();
        var headedWriter = new DeepCrcWriter(headedTarget, leaveOpen: true);
        headedSystems.AttachCrc(HeadedCrcChannels.CreateChecker(headedGame), headedWriter);

        headedSystems.CrcCheckpoint(new LogicFrame(7));
        headedWriter.Flush();

        var referenceGame = CreateHost();
        using var referenceTarget = NewDumpTarget();
        var referenceWriter = new DeepCrcWriter(referenceTarget, leaveOpen: true);
        var referenceChecker = HeadedCrcChannels.CreateChecker(referenceGame);

        // MapScenario.CrcCheckpoint, verbatim.
        var message = referenceChecker.ComputeDeepCheckpoint(new LogicFrame(7), referenceWriter);
        referenceWriter.CrcVector(7, message.Combined, message.ChannelCrcs);
        referenceWriter.Flush();

        Assert.Equal(referenceTarget.ToString(), headedTarget.ToString());
        Assert.Equal(message.Combined, headedSystems.FinalCombined);
        Assert.Equal(1, headedSystems.Checkpoints);
    }

    [Fact]
    public void TheDumpOpensWithTheDeepDumpHeaderLine()
    {
        using var target = NewDumpTarget();
        var writer = new DeepCrcWriter(target, leaveOpen: true);
        writer.Flush();

        Assert.Equal(DeepCrcWriter.HeaderLine, Lines(target.ToString())[0]);
    }

    // ------------------------------------------------------- start preconditions

    [Fact]
    public void StartPreconditionsAreVacuousWithTheFlagOff()
    {
        // A flag-off run must never be able to fail these, whatever else is set.
        HeadedCrcChannels.ValidateStartPreconditions(0, dumpPath: null, logicUpdateScaleFactor: 3f);
    }

    [Fact]
    public void StartPreconditionsRefuseAScaledLogicUpdate()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => HeadedCrcChannels.ValidateStartPreconditions(100, "dump.txt", logicUpdateScaleFactor: 2f));

        Assert.Contains("LogicUpdateScaleFactor", exception.Message);
    }

    [Fact]
    public void StartPreconditionsRefuseAMissingDumpPath()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => HeadedCrcChannels.ValidateStartPreconditions(100, dumpPath: null, logicUpdateScaleFactor: 1f));

        Assert.Contains("--headed-crc-out", exception.Message);
    }

    [Fact]
    public void StartPreconditionsAcceptTheSanctionedCombination()
    {
        HeadedCrcChannels.ValidateStartPreconditions(100, "dump.txt", logicUpdateScaleFactor: 1f);
    }

    [Fact]
    public void TheConfiguredIntervalIsClampedTheWayTheRetailIntervalIs()
    {
        // Game hands SyncChecker.EffectiveInterval whatever --headed-crc asked for; this pins
        // that the headed path inherits the same clamp the headless driver uses.
        Assert.Equal(SyncChecker.MaxIntervalInFrames, SyncChecker.EffectiveInterval(1000));
        Assert.Equal(5u, SyncChecker.EffectiveInterval(5));
    }

    // ------------------------------------------------------------------ helpers

    private static string[] Lines(string dump) =>
        dump.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static string LastVectorLine(string dump)
    {
        string last = null;
        foreach (var line in Lines(dump))
        {
            if (line.StartsWith("V ", StringComparison.Ordinal))
            {
                last = line;
            }
        }

        Assert.NotNull(last);
        return last;
    }

    private static uint[] VectorFrames(string dump)
    {
        var frames = new List<uint>();
        foreach (var line in Lines(dump))
        {
            if (line.StartsWith("V ", StringComparison.Ordinal))
            {
                frames.Add(uint.Parse(line.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return frames.ToArray();
    }
}
