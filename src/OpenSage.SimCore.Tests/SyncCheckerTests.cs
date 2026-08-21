// Gate tests for scaffolding step 5, part 3: the F8 channel walk, the interval clamp, the
// checkpoint message wire form, per-channel localization, exclude switches, and the
// LogicRandom channel round-trip.

using System;
using System.Collections.Generic;
using System.IO;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.SimCore.Tests;

public class SyncCheckerTests
{
    private sealed class FakeChannel : ICrcChannelSource
    {
        public FakeChannel(CrcChannel channel)
        {
            Channel = channel;
        }

        public CrcChannel Channel { get; }
        public bool IsActive { get; set; } = true;
        public uint Payload { get; set; } = 0x11111111u;

        public void Xfer(IXfer xfer)
        {
            var payload = Payload;
            xfer.XferUInt("Payload", ref payload);
            Payload = payload;
        }
    }

    [Fact]
    public void ChannelEnumIsTheFrozenWalkOrder()
    {
        // F8: Objects -> LogicRandom -> Partition -> TerrainLogic -> Shroud -> Collision ->
        // Taint -> Players -> AI -> LivingWorld. Ordinals are the wire vector index; any
        // reordering is a protocol change and must fail here first.
        Assert.Equal(0, (int)CrcChannel.Objects);
        Assert.Equal(1, (int)CrcChannel.LogicRandom);
        Assert.Equal(2, (int)CrcChannel.Partition);
        Assert.Equal(3, (int)CrcChannel.TerrainLogic);
        Assert.Equal(4, (int)CrcChannel.Shroud);
        Assert.Equal(5, (int)CrcChannel.Collision);
        Assert.Equal(6, (int)CrcChannel.Taint);
        Assert.Equal(7, (int)CrcChannel.Players);
        Assert.Equal(8, (int)CrcChannel.AI);
        Assert.Equal(9, (int)CrcChannel.LivingWorld);
        Assert.Equal(10, CrcChannels.Count);
    }

    [Fact]
    public void IntervalClampReproducesTheBinary()
    {
        // min(configured, 100) - the cmp 0x64/jl pair at 0x77ed5d. Zero is malformed: the
        // original's gate is an unsigned DIV by the interval.
        Assert.Equal(7u, SyncChecker.EffectiveInterval(7));
        Assert.Equal(100u, SyncChecker.EffectiveInterval(100));
        Assert.Equal(100u, SyncChecker.EffectiveInterval(250));
        Assert.Equal(1u, SyncChecker.EffectiveInterval(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => SyncChecker.EffectiveInterval(0));
    }

    [Fact]
    public void CheckpointGateIsUnsignedModulo()
    {
        Assert.True(SyncChecker.IsCheckpointFrame(new LogicFrame(0), 100));
        Assert.False(SyncChecker.IsCheckpointFrame(new LogicFrame(99), 100));
        Assert.True(SyncChecker.IsCheckpointFrame(new LogicFrame(100), 100));
        Assert.True(SyncChecker.IsCheckpointFrame(new LogicFrame(200), 250)); // clamped to 100
        // The counter is unsigned: 0x80000000 % 100 must not go through signed math.
        Assert.True(SyncChecker.IsCheckpointFrame(new LogicFrame(0x8000_0000u + (100 - 0x8000_0000u % 100)), 100));
    }

    [Fact]
    public void RegistrationMustFollowTheFrozenOrder()
    {
        var objects = new FakeChannel(CrcChannel.Objects);
        var players = new FakeChannel(CrcChannel.Players);

        // In order: fine (gaps allowed - unmigrated channels simply do not exist yet).
        _ = new SyncChecker(new ICrcChannelSource[] { objects, players });

        // Out of order and duplicates: malformed.
        Assert.Throws<ArgumentException>(() => new SyncChecker(new ICrcChannelSource[] { players, objects }));
        Assert.Throws<ArgumentException>(() => new SyncChecker(new ICrcChannelSource[] { objects, objects }));
    }

    [Fact]
    public void CheckpointMessageRoundTripsItsWireForm()
    {
        var checker = new SyncChecker(new ICrcChannelSource[]
        {
            new FakeChannel(CrcChannel.Objects) { Payload = 0xAAAAAAAAu },
            new FakeChannel(CrcChannel.Players) { Payload = 0xBBBBBBBBu },
        });

        var message = checker.ComputeCheckpoint(new LogicFrame(100));
        Assert.Equal(100u, message.Frame);
        Assert.Equal(CrcCheckpointMessage.AlgorithmRotlAdd, message.AlgorithmId);
        Assert.Equal(CrcChannels.Count, (int)message.ChannelCount);

        var parsed = CrcCheckpointMessage.Parse(message.ToBytes());
        Assert.Equal(message, parsed);
        Assert.Equal(message.Combined, parsed.Combined);

        // A truncated or padded buffer is malformed.
        var bytes = message.ToBytes();
        Assert.Throws<FormatException>(() => CrcCheckpointMessage.Parse(bytes.AsSpan(0, bytes.Length - 1).ToArray()));
    }

    [Fact]
    public void ChannelCrcIsPositionalAndKnown()
    {
        // A single-uint channel folds its 4 LE bytes as one word from init 0, so the channel
        // CRC is the payload itself - a hand-checkable anchor for the whole walk.
        var checker = new SyncChecker(new ICrcChannelSource[]
        {
            new FakeChannel(CrcChannel.Objects) { Payload = 0x04030201u },
        });
        var message = checker.ComputeCheckpoint(new LogicFrame(0));
        Assert.Equal(0x04030201u, message.ChannelCrcs[(int)CrcChannel.Objects]);
        for (var i = 1; i < CrcChannels.Count; i++)
        {
            Assert.Equal(0u, message.ChannelCrcs[i]);
        }
    }

    [Fact]
    public void DivergenceIsLocalizedToTheDivergingChannel()
    {
        static SyncChecker Build(uint objectsPayload) => new(new ICrcChannelSource[]
        {
            new FakeChannel(CrcChannel.Objects) { Payload = objectsPayload },
            new FakeChannel(CrcChannel.Players) { Payload = 0x5555_5555u },
        });

        var ours = Build(1).ComputeCheckpoint(new LogicFrame(100));
        var theirs = Build(2).ComputeCheckpoint(new LogicFrame(100));

        Assert.NotEqual(ours.Combined, theirs.Combined);
        var diverging = ours.DivergingChannels(theirs);
        Assert.Equal(new[] { CrcChannel.Objects }, diverging);

        var inSync = Build(1).ComputeCheckpoint(new LogicFrame(100));
        Assert.Empty(ours.DivergingChannels(inSync));
        Assert.Equal(ours, inSync);
    }

    [Fact]
    public void ExcludeSwitchDropsExactlyThatChannel()
    {
        static SyncChecker Build() => new(new ICrcChannelSource[]
        {
            new FakeChannel(CrcChannel.Objects) { Payload = 0x0A0B0C0Du },
            new FakeChannel(CrcChannel.Players) { Payload = 0x01020304u },
        });

        var baseline = Build().ComputeCheckpoint(new LogicFrame(0));

        var excluding = Build();
        excluding.SetExcluded(CrcChannel.Objects, true);
        var excluded = excluding.ComputeCheckpoint(new LogicFrame(0));

        Assert.Equal(0u, excluded.ChannelCrcs[(int)CrcChannel.Objects]);
        Assert.Equal(baseline.ChannelCrcs[(int)CrcChannel.Players], excluded.ChannelCrcs[(int)CrcChannel.Players]);
        Assert.NotEqual(baseline.Combined, excluded.Combined);
    }

    [Fact]
    public void InactiveChannelContributesZero()
    {
        var livingWorld = new FakeChannel(CrcChannel.LivingWorld) { IsActive = false };
        var checker = new SyncChecker(new ICrcChannelSource[] { livingWorld });
        var message = checker.ComputeCheckpoint(new LogicFrame(0));
        Assert.Equal(0u, message.ChannelCrcs[(int)CrcChannel.LivingWorld]);
    }

    [Fact]
    public void DeepCheckpointEqualsPlainCheckpoint()
    {
        static SyncChecker Build() => new(new ICrcChannelSource[]
        {
            new FakeChannel(CrcChannel.Objects) { Payload = 0xFEEDF00Du },
            new FakeChannel(CrcChannel.Players) { Payload = 0x0BADF00Du },
        });

        var plain = Build().ComputeCheckpoint(new LogicFrame(100));

        var text = new StringWriter();
        CrcCheckpointMessage deep;
        using (var writer = new DeepCrcWriter(text, leaveOpen: true))
        {
            deep = Build().ComputeDeepCheckpoint(new LogicFrame(100), writer);
        }

        Assert.Equal(plain, deep);

        var dump = text.ToString();
        Assert.StartsWith(DeepCrcWriter.HeaderLine, dump);
        Assert.Contains("F 100\n", dump);
        Assert.Contains("C 0 Objects\n", dump);
        Assert.Contains("C 7 Players\n", dump);
        Assert.Contains("R ", dump);
        Assert.Contains("E 0 ", dump);
    }

    [Fact]
    public void LogicRandomChannelTracksDrawsAndRoundTrips()
    {
        var random = LogicRandom.CreateForSimContext(1234u);
        var source = new LogicRandomChannelSource(random);
        var checker = new SyncChecker(new ICrcChannelSource[] { source });

        var before = checker.ComputeCheckpoint(new LogicFrame(0));
        _ = random.NextUInt32();
        var after = checker.ComputeCheckpoint(new LogicFrame(100));

        // The RNG state is folded into every checkpoint (F5): a draw must change the channel.
        Assert.Equal(new[] { CrcChannel.LogicRandom },
            new CrcCheckpointMessage(0, before.AlgorithmId, CopyCrcs(before), before.Combined)
                .DivergingChannels(new CrcCheckpointMessage(0, after.AlgorithmId, CopyCrcs(after), after.Combined)));

        // Save -> Load restores the exact state: the resumed draw sequence is identical.
        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            source.Xfer(save);
        }

        var expected = new List<uint>();
        for (var i = 0; i < 16; i++)
        {
            expected.Add(random.NextUInt32());
        }

        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            source.Xfer(load);
        }
        for (var i = 0; i < 16; i++)
        {
            Assert.Equal(expected[i], random.NextUInt32());
        }
    }

    private static uint[] CopyCrcs(CrcCheckpointMessage message)
    {
        var crcs = new uint[message.ChannelCount];
        for (var i = 0; i < crcs.Length; i++)
        {
            crcs[i] = message.ChannelCrcs[i];
        }
        return crcs;
    }
}
