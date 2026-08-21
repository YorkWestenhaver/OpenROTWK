// R14 packet 1 gate tests (workbench design-sim-presentation-bridge.md §2 packet 1): the
// headed logic frame now runs through SimCore's frozen phase sequence.
//
// These are render-free by construction: the host is HeadlessSimGame (a real GameLogic and a
// real PartitionCellManager, no renderer, no files), the "residue" hook is a recorder instead
// of Scene3D.LogicTick, and the connection is a fake that records where the logic clock stood
// when the frame's orders were drained. Nothing here touches a GraphicsDevice.
//
// The claim under test is the packet's verifiable claim: the per-frame call sequence is what
// it always was, EXCEPT that the network drain moved from after GameLogic.Update() to before
// it. Test 2 is that one change, stated as an observable fact about the logic clock.

using System;
using System.Collections.Generic;
using OpenSage.Logic.Orders;
using OpenSage.Logic.Sim;
using OpenSage.Network;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Sim;

public class HeadedSimSystemsTests
{
    /// <summary>Records every phase entry the loop announces, in order.</summary>
    private sealed class PhaseRecorder : ISimPhaseObserver
    {
        private readonly ISimPhaseObserver _inner;

        public readonly List<(SimPhase Phase, uint Frame)> Phases = new();

        public PhaseRecorder(ISimPhaseObserver inner)
        {
            _inner = inner;
        }

        public void OnPhase(SimPhase phase, LogicFrame frame)
        {
            Phases.Add((phase, frame.Value));
            _inner.OnPhase(phase, frame);
        }
    }

    /// <summary>
    /// A connection that delivers nothing and records the logic frame each drain saw. The
    /// legacy tick drained AFTER GameLogic.Update(), so it saw N+1; under the frozen sequence
    /// IngestOrders precedes ModuleUpdate, so it must see N.
    /// </summary>
    private sealed class RecordingConnection : IConnection
    {
        private readonly Func<uint> _readLogicFrame;

        public readonly List<uint> LogicFrameAtDrain = new();

        public RecordingConnection(Func<uint> readLogicFrame)
        {
            _readLogicFrame = readLogicFrame;
        }

        public void Send(uint frame, List<Order> orders) => LogicFrameAtDrain.Add(_readLogicFrame());

        public void Receive(uint frame, Action<uint, Order> packetFn)
        {
            // No inbound orders: this test is about WHEN the drain happens, not what it carries.
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// Builds the same wiring Game's constructor builds: HeadedSimSystems as both the phase
    /// bodies and the loop's observer, CRC off, and the unphased residue hook in place of
    /// Scene3D.LogicTick.
    /// </summary>
    private static (SimLoop Loop, PhaseRecorder Recorder) CreateLoop(
        HeadlessSimGame game,
        Action residue = null)
    {
        var systems = new HeadedSimSystems(game, residue);
        var recorder = new PhaseRecorder(systems);
        var loop = new SimLoop(systems, recorder)
        {
            // Game.cs: a headed game runs with the CrcCheckpoint body switched off (packet 5).
            CrcCheckpointIntervalInFrames = 0,
        };
        return (loop, recorder);
    }

    // ------------------------------------------------------------------ frozen sequence

    [Fact]
    public void EveryFrameRunsTheFrozenPhaseSequenceExactlyOnce()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        var (loop, recorder) = CreateLoop(game);

        loop.Advance();
        loop.Advance();

        var expected = new List<(SimPhase Phase, uint Frame)>();
        for (var frame = 0u; frame < 2u; frame++)
        {
            foreach (var phase in SimLoop.PhaseSequence)
            {
                expected.Add((phase, frame));
            }
        }

        Assert.Equal(expected, recorder.Phases);
    }

    [Fact]
    public void TheUnphasedResidueRunsOncePerFrame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        var residueCalls = 0;
        var (loop, _) = CreateLoop(game, () => residueCalls++);

        loop.Advance();
        loop.Advance();
        loop.Advance();

        Assert.Equal(3, residueCalls);
    }

    // ------------------------------------------------- the one intentional behavior change

    [Fact]
    public void OrdersAreIngestedBeforeTheModuleUpdate()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        var connection = new RecordingConnection(() => game.GameLogic.CurrentFrame.Value);
        game.NetworkMessageBuffer = new NetworkMessageBuffer(game, connection);

        var (loop, _) = CreateLoop(game);

        loop.Advance();
        loop.Advance();
        loop.Advance();

        // Frame N's drain sees the logic clock at N, i.e. BEFORE GameLogic.Update() advanced
        // it. Under the legacy order (drain after the module update) this would read 1, 2, 3.
        Assert.Equal(new uint[] { 0, 1, 2 }, connection.LogicFrameAtDrain);
    }

    [Fact]
    public void AFrameWithNoConnectionStillRuns()
    {
        // A headed game sitting in the menu has no NetworkMessageBuffer; IngestOrders must be
        // null-tolerant, exactly as the legacy `NetworkMessageBuffer?.Tick()` was.
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        Assert.Null(game.NetworkMessageBuffer);

        var (loop, _) = CreateLoop(game);

        loop.Advance();

        Assert.Equal(1u, loop.CurrentFrame.Value);
    }

    // ------------------------------------------------------ frame-counter reconciliation

    [Fact]
    public void FrameCountersAgreeAtBoundariesAndDifferInsideAFrame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);

        SimLoop loop = null;
        var observedInsideFrame = new List<(uint Loop, uint Logic)>();

        // The residue hook runs after the ModuleUpdate body and before EndFrame, so it is a
        // window into mid-frame state: the logic clock has advanced, the loop's has not.
        var created = CreateLoop(
            game,
            () => observedInsideFrame.Add((loop.CurrentFrame.Value, game.GameLogic.CurrentFrame.Value)));
        loop = created.Loop;

        // Both clocks start at zero on a freshly constructed host; equality below is only
        // meaningful because nothing resets GameLogic mid-test (Scene3D construction and save
        // loading both re-zero/restore it, which is why HeadedSimSystems asserts lockstep
        // rather than equality, and why giving SimLoop a reset seam is packet 3).
        Assert.Equal(0u, loop.CurrentFrame.Value);
        Assert.Equal(0u, game.GameLogic.CurrentFrame.Value);

        for (var i = 1u; i <= 4u; i++)
        {
            loop.Advance();

            // At the frame boundary the two counters agree.
            Assert.Equal(i, loop.CurrentFrame.Value);
            Assert.Equal(i, game.GameLogic.CurrentFrame.Value);
        }

        // Inside the frame, after ModuleUpdate, the logic clock reads exactly one ahead.
        Assert.Equal(
            new (uint Loop, uint Logic)[] { (0u, 1u), (1u, 2u), (2u, 3u), (3u, 4u) },
            observedInsideFrame);
    }
}
