// The step-5 run-twice gate (api-freeze-v1 §6, Target A): the same scripted scenario, run
// twice in one process with GC pressure between runs (and again by the CI determinism job
// under different GC/tiering configurations - see .github/workflows/ci.yml), must produce a
// byte-identical checkpoint CRC stream AND a byte-identical deep dump. Any divergence is a
// determinism bug by definition (F14), and the deep dumps localize it to a field.

using System;
using System.Collections.Generic;
using System.IO;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.SimCore.Tests;

[Trait("Category", "Determinism")]
public class RunTwiceDeterminismTests
{
    private const uint MatchSeed = 0xC0FFEEu;
    private const uint CheckpointInterval = 50;
    private const int FramesToRun = 201; // checkpoints at 0, 50, 100, 150, 200

    private sealed class FakeObject
    {
        public uint Id;
        public FixVector3 Position;
        public Fix64 Health;
        public LogicFrame NextWake;

        public void Xfer(IXfer xfer, XferModuleId id)
        {
            xfer.BeginModule(id);
            xfer.XferFixVector3("Position", ref Position, Tolerance.Band);
            xfer.XferFix64("Health", ref Health, Tolerance.Quantum);
            xfer.XferFrame("NextWake", ref NextWake);
            xfer.EndModule();
        }
    }

    /// <summary>The Objects channel: every object in ascending ObjectId order.</summary>
    private sealed class ObjectsChannel : ICrcChannelSource
    {
        public readonly List<FakeObject> Objects = new();

        public CrcChannel Channel => CrcChannel.Objects;
        public bool IsActive => true;

        public void Xfer(IXfer xfer)
        {
            foreach (var obj in Objects)
            {
                obj.Xfer(xfer, new XferModuleId(obj.Id, 0, "ModuleTag_01", "FakeObject"));
            }
        }
    }

    private sealed class Scenario : ISimSystems
    {
        private readonly LogicRandom _random = LogicRandom.CreateForSimContext(MatchSeed);
        private readonly ObjectsChannel _objects = new();
        private readonly SyncChecker _checker;

        public readonly List<byte[]> CheckpointStream = new();
        public readonly StringWriter DeepDump = new();
        private readonly DeepCrcWriter _deepWriter;

        public Scenario()
        {
            _checker = new SyncChecker(new ICrcChannelSource[]
            {
                _objects,
                new LogicRandomChannelSource(_random),
            });
            _deepWriter = new DeepCrcWriter(DeepDump, leaveOpen: true);

            for (var i = 0; i < 8; i++)
            {
                _objects.Objects.Add(new FakeObject
                {
                    Id = (uint)(i + 1),
                    Position = new FixVector3(
                        Fix64.FromRaw((long)(i + 1) << 32),
                        Fix64.Zero,
                        Fix64.Zero),
                    Health = Fix64.FromRaw(100L << 32),
                    NextWake = LogicFrame.Zero,
                });
            }
        }

        public void IngestOrders(LogicFrame frame)
        {
        }

        public void DispatchOrder(in ScheduledOrder order)
        {
        }

        public void ModuleUpdate(LogicFrame frame)
        {
            // Every object consults the one logic RNG stream and mutates Fix64 state -
            // integer-only sim work with allocation churn thrown in so the GC has something
            // to move between the two runs.
            foreach (var obj in _objects.Objects)
            {
                var step = _random.Next(-3, 4);
                var delta = new FixVector3(
                    Fix64.FromRaw((long)step << 28),
                    Fix64.FromRaw((long)-step << 27),
                    Fix64.Zero);
                obj.Position += delta;
                obj.Health -= Fix64.FromRaw((long)_random.Next(0, 2) << 30);
                obj.NextWake = new LogicFrame(frame.Value + (uint)_random.Next(1, 5));
                _ = new byte[64 + (int)(frame.Value % 7) * 16]; // garbage, deliberately
            }
        }

        public void PartitionUpdate(LogicFrame frame)
        {
        }

        public void CrcCheckpoint(LogicFrame frame)
        {
            var message = _checker.ComputeDeepCheckpoint(frame, _deepWriter);
            CheckpointStream.Add(message.ToBytes());

            // The deep walk folds the identical bytes, so recomputing plainly must agree -
            // asserted inline at every checkpoint of every run.
            Assert.Equal(message, _checker.ComputeCheckpoint(frame));
        }
    }

    private static (List<byte[]> Stream, string DeepDump) RunOnce()
    {
        var scenario = new Scenario();
        var loop = new SimLoop(scenario)
        {
            CrcCheckpointIntervalInFrames = SyncChecker.EffectiveInterval(CheckpointInterval),
        };
        for (var i = 0; i < FramesToRun; i++)
        {
            loop.Advance();
        }
        return (scenario.CheckpointStream, scenario.DeepDump.ToString());
    }

    [Fact]
    public void SameScenarioTwiceYieldsIdenticalCrcStreamAndDeepDump()
    {
        var first = RunOnce();

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        var second = RunOnce();

        Assert.Equal(5, first.Stream.Count); // frames 0, 50, 100, 150, 200
        Assert.Equal(first.Stream.Count, second.Stream.Count);
        for (var i = 0; i < first.Stream.Count; i++)
        {
            Assert.Equal(first.Stream[i], second.Stream[i]);
        }

        Assert.Equal(first.DeepDump, second.DeepDump);

        // The stream is not trivially constant: state evolved between checkpoints.
        Assert.NotEqual(first.Stream[0], first.Stream[1]);
    }

    [Fact]
    public void CheckpointStreamSurvivesTheWireRoundTrip()
    {
        var (stream, _) = RunOnce();
        foreach (var bytes in stream)
        {
            var parsed = CrcCheckpointMessage.Parse(bytes);
            Assert.Equal(bytes, parsed.ToBytes());
        }
    }
}
