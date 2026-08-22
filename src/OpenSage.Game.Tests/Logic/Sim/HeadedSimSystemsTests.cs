// R14 packet 1 gate tests (workbench design-sim-presentation-bridge.md §2 packet 1): the
// headed logic frame now runs through SimCore's frozen phase sequence.
//
// R15 packet 2 (br-p2-scene3d-split) extends them: the unphased residue hook is gone. What
// used to be Scene3D.LogicTick is now three things in three pinned slots -
//   * the player tick, inside GameLogic.Update (GPL's AI::update slot, beside the pathfind
//     queue), i.e. in the ModuleUpdate phase and on the PRE-increment logic frame;
//   * IScene3D.SimObjectTick, at the HEAD of PartitionUpdate (unchanged position: the object
//     loop dirties what the partition tick then re-anchors);
//   * IScene3D.ReapDestroyed, at the TAIL of PartitionUpdate, AFTER
//     PartitionCellManager.Update - packet 2's one claimed behavior change, matching GPL,
//     which reaps its pending-delete list once ThePartitionManager has already run.
//
// R15 packet 3 ("one clock") extends them again:
//   * the scripting tick is inside the frame now, at the HEAD of ModuleUpdate, instead of on
//     a second wall-clock accumulator in Game.Update;
//   * the EndFrame reconciliation is plain equality against the loop's own counter, which is
//     only assertable because SimLoop.ResetTo exists to re-seat the loop after a host-side
//     clock jump (a loaded save).
//
// These are render-free by construction: the host is HeadlessSimGame (a real GameLogic, a real
// PlayerManager and a real PartitionCellManager, no renderer, no files), the scene is a
// recording decorator over HeadlessSimGame's own null-object scene, and the connection is a
// fake that records where the logic clock stood when the frame's orders were drained. Nothing
// here touches a GraphicsDevice.

using System;
using System.Collections.Generic;
using OpenSage.Audio;
using OpenSage.Content.Loaders;
using OpenSage.Data.Map;
using OpenSage.DataStructures;
using OpenSage.Graphics.Cameras;
using OpenSage.Graphics.ParticleSystems;
using OpenSage.Graphics.Rendering;
using OpenSage.Graphics.Rendering.Shadows;
using OpenSage.Graphics.Rendering.Water;
using OpenSage.Gui;
using OpenSage.Gui.DebugUI;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Orders;
using OpenSage.Logic.Sim;
using OpenSage.Network;
using OpenSage.Rendering;
using OpenSage.Scripting;
using OpenSage.Settings;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Ticking;
using OpenSage.Terrain;
using OpenSage.Terrain.Roads;
using Xunit;
using Player = OpenSage.Logic.Player;

namespace OpenSage.Tests.Logic.Sim;

[Collection(GameTraceCollection.Name)]
public class HeadedSimSystemsTests
{
    /// <summary>
    /// Stands in for the game's <c>ScriptingSystem</c> (a headless host has none): records the
    /// logic frame the tick saw, so the test can prove the call lands BEFORE
    /// <c>GameLogic.Update()</c> advanced the clock.
    /// </summary>
    private sealed class RecordingScriptingTick : IScriptingTick
    {
        private readonly Func<uint> _readLogicFrame;
        private readonly List<string> _log;

        public readonly List<uint> LogicFrameAtTick = new();

        public RecordingScriptingTick(Func<uint> readLogicFrame, List<string> log = null)
        {
            _readLogicFrame = readLogicFrame;
            _log = log;
        }

        public void ScriptingTick()
        {
            LogicFrameAtTick.Add(_readLogicFrame());
            _log?.Add(nameof(ScriptingTick));
        }
    }

    /// <summary>
    /// The smallest possible <see cref="ISimSystems"/>: does nothing at all. Used where the
    /// test is about the loop's own counter rather than about what a frame does.
    /// </summary>
    private sealed class InertSystems : ISimSystems
    {
        public void IngestOrders(LogicFrame frame) { }
        public void DispatchOrder(in ScheduledOrder order) { }
        public void ModuleUpdate(LogicFrame frame) { }
        public void PartitionUpdate(LogicFrame frame) { }
        public void CrcCheckpoint(LogicFrame frame) { }
    }

    /// <summary>Records every phase entry the loop announces, in order.</summary>
    private sealed class PhaseRecorder : ISimPhaseObserver
    {
        private readonly ISimPhaseObserver _inner;
        private readonly List<string> _log;

        public readonly List<(SimPhase Phase, uint Frame)> Phases = new();

        public PhaseRecorder(ISimPhaseObserver inner, List<string> log = null)
        {
            _inner = inner;
            _log = log;
        }

        public void OnPhase(SimPhase phase, LogicFrame frame)
        {
            Phases.Add((phase, frame.Value));
            _log?.Add($"phase:{phase}");
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
    /// A pass-through <see cref="IScene3D"/> that records when the two sim entry points packet
    /// 2 introduced are called. Everything else forwards to the host's own scene, so the game
    /// behaves exactly as it does without the decorator.
    /// </summary>
    private sealed class RecordingScene3D : IScene3D
    {
        private readonly IScene3D _inner;
        private readonly List<string> _log;
        private readonly Action _onSimObjectTick;

        public RecordingScene3D(IScene3D inner, List<string> log, Action onSimObjectTick = null)
        {
            _inner = inner;
            _log = log;
            _onSimObjectTick = onSimObjectTick;
        }

        public void SimObjectTick(in TimeInterval time)
        {
            _log.Add(nameof(SimObjectTick));
            _onSimObjectTick?.Invoke();
            _inner.SimObjectTick(time);
        }

        public void ReapDestroyed()
        {
            _log.Add(nameof(ReapDestroyed));
            _inner.ReapDestroyed();
        }

        public IEditorCameraController EditorCameraController => _inner.EditorCameraController;
        public IGameEngine GameEngine => _inner.GameEngine;
        public SelectionGui SelectionGui => _inner.SelectionGui;
        public DebugOverlay DebugOverlay => _inner.DebugOverlay;
        ParticleSystemManager IScene3D.ParticleSystemManager => _inner.ParticleSystemManager;
        public Camera Camera => _inner.Camera;
        public TacticalView TacticalView => _inner.TacticalView;
        public MapFile MapFile => _inner.MapFile;
        public OpenSage.Terrain.Terrain Terrain => _inner.Terrain;
        public IQuadtree<GameObject> Quadtree => _inner.Quadtree;
        public bool ShowTerrain { get; set; }
        public WaterAreaCollection WaterAreas => _inner.WaterAreas;
        public bool ShowWater { get; set; }
        public RoadCollection Roads => _inner.Roads;
        public bool ShowRoads { get; set; }
        public Bridge[] Bridges => _inner.Bridges;
        public bool ShowBridges { get; set; }
        public bool FrustumCulling { get; set; }
        public PlayerScriptsList PlayerScripts => _inner.PlayerScripts;
        public IGameObjectCollection GameObjects => _inner.GameObjects;
        public bool ShowObjects { get; set; }
        public CameraCollection Cameras => _inner.Cameras;
        public WaypointCollection Waypoints => _inner.Waypoints;
        public WorldLighting Lighting => _inner.Lighting;
        public ShadowSettings Shadows => _inner.Shadows;
        public WaterSettings Waters => _inner.Waters;
        public IReadOnlyList<Player> Players => _inner.Players;
        public Player LocalPlayer => _inner.LocalPlayer;
        public OpenSage.Navigation.Navigation Navigation => _inner.Navigation;
        public AudioSystem Audio => _inner.Audio;
        AssetLoadContext IScene3D.AssetLoadContext => _inner.AssetLoadContext;
        public Radar Radar => _inner.Radar;
        public IGame Game => _inner.Game;
        public GameObject BuildPreviewObject { get; set; }
        public RenderScene RenderScene => _inner.RenderScene;
        public RadarDrawUtil RadarDrawUtil => _inner.RadarDrawUtil;
        public int GetPlayerIndex(Player player) => _inner.GetPlayerIndex(player);
        public void LocalLogicTick(in TimeInterval gameTime, float tickT) => _inner.LocalLogicTick(gameTime, tickT);
        public void BuildRenderList(RenderList renderList, Camera camera, in TimeInterval gameTime) { }
        public void Render(DrawingContext2D drawingContext) { }
        public GameObject CreateSkirmishPlayerStartingBuilding(in PlayerSetting playerSetting, Player player) =>
            throw new NotSupportedException();
        public void Dispose() { }
    }

    /// <summary>
    /// Builds the same wiring Game's constructor builds: HeadedSimSystems as both the phase
    /// bodies and the loop's observer, CRC off. Since packet 2 there is no residue hook - the
    /// sim work the hook used to carry now reaches the loop through IScene3D.
    /// </summary>
    private static (SimLoop Loop, PhaseRecorder Recorder) CreateLoop(
        HeadlessSimGame game,
        List<string> log = null,
        IScriptingTick scripting = null)
    {
        var systems = new HeadedSimSystems(game, scripting);
        var recorder = new PhaseRecorder(systems, log);
        var loop = new SimLoop(systems, recorder)
        {
            // Game.cs: a headed game runs with the CrcCheckpoint body switched off (packet 5).
            CrcCheckpointIntervalInFrames = 0,
        };
        return (loop, recorder);
    }

    /// <summary>
    /// Swaps in a recording scene and returns the shared call log. The decorator forwards to
    /// the host's own null-object scene, so nothing else about the host changes.
    /// </summary>
    private static List<string> RecordScene(
        HeadlessSimGame game,
        Action onSimObjectTick = null)
    {
        var log = new List<string>();
        game.Scene3D = new RecordingScene3D(game.Scene3D, log, onSimObjectTick);
        return log;
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

    // ---------------------------------------------------- packet 2: the Scene3D.LogicTick split

    [Fact]
    public void TheSceneSimTickAndTheReapEachRunOncePerFrame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        var log = RecordScene(game);
        var (loop, _) = CreateLoop(game);

        loop.Advance();
        loop.Advance();
        loop.Advance();

        // Exactly one of each per frame, in that order - the split kept the object loop ahead
        // of the reap, as the single LogicTick body had it.
        Assert.Equal(
            new[]
            {
                "SimObjectTick", "ReapDestroyed",
                "SimObjectTick", "ReapDestroyed",
                "SimObjectTick", "ReapDestroyed",
            },
            log);
    }

    [Fact]
    public void BothSceneSimCallsHappenInsideThePartitionUpdatePhase()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);

        // One shared log: phase entries and scene calls interleaved in real order.
        var log = new List<string>();
        game.Scene3D = new RecordingScene3D(game.Scene3D, log);
        var (loop, _) = CreateLoop(game, log);

        loop.Advance();

        var partitionIndex = log.IndexOf($"phase:{SimPhase.PartitionUpdate}");
        var simObjectTickIndex = log.IndexOf("SimObjectTick");
        var reapIndex = log.IndexOf("ReapDestroyed");

        Assert.True(partitionIndex >= 0, string.Join(" -> ", log));
        Assert.True(simObjectTickIndex > partitionIndex, string.Join(" -> ", log));
        Assert.True(reapIndex > simObjectTickIndex, string.Join(" -> ", log));

        // Nothing else in the frozen sequence starts between the phase opening and the reap:
        // both calls belong to PartitionUpdate, with PartitionCellManager.Update between them
        // (HeadedSimSystems.PartitionUpdate). PartitionCellManager is a sealed concrete class
        // with no observable per-frame state, so its position between the two is pinned by the
        // phase body and this test's bracket, not by a third log entry.
        for (var i = partitionIndex + 1; i < reapIndex; i++)
        {
            Assert.False(log[i].StartsWith("phase:", StringComparison.Ordinal), log[i]);
        }
    }

    [Fact]
    public void ThePlayerTickRunsInsideTheModuleUpdateOnThePreIncrementFrame()
    {
        // The one runtime-observable consequence of moving PlayerManager.LogicTick into
        // GameLogic.Update: a power brownout clears without anyone calling PlayerManager or
        // Scene3D, and it clears on the frame the logic clock READS during ModuleUpdate (the
        // pre-increment value), not the frame it has already advanced to.
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        var player = game.PlayerManager.NeutralPlayer;

        player.SetPowerSabotagedTillFrame(new LogicFrame(2));
        Assert.True(player.HasInsufficientPower);

        var (loop, _) = CreateLoop(game);

        // Frame N's ModuleUpdate ticks the player with the logic clock still reading N.
        loop.Advance();  // player ticked at logic frame 0
        Assert.True(player.HasInsufficientPower);

        loop.Advance();  // player ticked at logic frame 1
        Assert.True(player.HasInsufficientPower);

        loop.Advance();  // player ticked at logic frame 2 -> 2 >= 2, brownout lifts
        Assert.False(player.HasInsufficientPower);
    }

    [Fact]
    public void AFrameWithNoSceneStillRuns()
    {
        // A headed game between maps has no Scene3D at all; the partition phase must tolerate
        // that exactly as the old `Scene3D?.LogicTick(...)` did.
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E)
        {
            Scene3D = null,
        };

        var (loop, _) = CreateLoop(game);

        loop.Advance();

        Assert.Equal(1u, loop.CurrentFrame.Value);
        Assert.Equal(1u, game.GameLogic.CurrentFrame.Value);
    }

    // -------------------------------------------------------- packet 3: one clock

    [Fact]
    public void TheScriptingTickRunsOncePerFrameAtTheHeadOfModuleUpdate()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);

        var log = new List<string>();
        var scripting = new RecordingScriptingTick(() => game.GameLogic.CurrentFrame.Value, log);
        var (loop, _) = CreateLoop(game, log, scripting);

        loop.Advance();
        loop.Advance();
        loop.Advance();

        // Once per frame, reading the PRE-increment logic clock: the tick precedes
        // GameLogic.Update() within the phase. (Under the deleted second accumulator this
        // count was whatever the wall clock happened to produce.)
        Assert.Equal(new uint[] { 0, 1, 2 }, scripting.LogicFrameAtTick);

        // ...and it is inside ModuleUpdate, not a phase of its own: the sequence between the
        // ModuleUpdate entry and the next phase entry contains the tick and nothing else.
        var moduleIndex = log.IndexOf($"phase:{SimPhase.ModuleUpdate}");
        Assert.True(moduleIndex >= 0, string.Join(" -> ", log));
        Assert.Equal("ScriptingTick", log[moduleIndex + 1]);
        Assert.Equal($"phase:{SimPhase.PartitionUpdate}", log[moduleIndex + 2]);

        // The frozen sequence is untouched - no scripting phase was added (F6).
        Assert.Equal(6, SimLoop.PhaseSequence.Length);
    }

    [Fact]
    public void AFrameWithNoScriptingSystemStillRuns()
    {
        // A headless host has no ScriptingSystem at all, and a headed game has none before
        // its content loads; the ModuleUpdate head must tolerate that.
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        Assert.Null(game.Scripting);

        var (loop, _) = CreateLoop(game);

        loop.Advance();

        Assert.Equal(1u, loop.CurrentFrame.Value);
        Assert.Equal(1u, game.GameLogic.CurrentFrame.Value);
    }

    [Fact]
    public void ResetToReSeatsTheLoopsCounter()
    {
        // The seam itself, against an inert host: the loop resumes counting from wherever it
        // is put, which is what lets a save restore an arbitrary frame number.
        var loop = new SimLoop(new InertSystems());

        loop.Advance();
        loop.Advance();
        Assert.Equal(2u, loop.CurrentFrame.Value);

        loop.ResetTo(new LogicFrame(4711));
        Assert.Equal(4711u, loop.CurrentFrame.Value);

        loop.Advance();
        Assert.Equal(4712u, loop.CurrentFrame.Value);
    }

    [Fact]
    public void TheEndFrameAssertRejectsALoopThatHasDriftedOffTheLogicClock()
    {
        // The strengthened assert. A loop sitting on frame 7 while the logic clock sits on 0
        // is exactly the state a save load produces if nobody re-seats the loop; packet 1's
        // delta assert accepted it (the logic clock still advanced by one), packet 3's
        // equality assert does not.
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        var (loop, _) = CreateLoop(game);

        loop.ResetTo(new LogicFrame(7));

        var exception = Assert.Throws<Exception>(() => loop.Advance());
        Assert.Contains("EndFrame of loop frame 7", exception.Message);
    }

    [Fact]
    public void ResetToPutsTheLoopBackOnTheLogicClockAfterAJump()
    {
        // ...and the recovery Game performs at StartGame/LoadSaveFile: re-seat the loop onto
        // the logic clock and frames run again.
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x81D6E);
        var (loop, _) = CreateLoop(game);

        loop.Advance();
        loop.ResetTo(new LogicFrame(500));
        Assert.Throws<Exception>(() => loop.Advance());

        // The failed Advance still ran GameLogic.Update(), so the logic clock is where it is;
        // this is precisely what Game does after SaveFile.Load.
        loop.ResetTo(game.GameLogic.CurrentFrame);

        loop.Advance();

        Assert.Equal(game.GameLogic.CurrentFrame.Value, loop.CurrentFrame.Value);
    }

    // ------------------------------------------------- the R14 intentional behavior change

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

        // SimObjectTick runs after the ModuleUpdate body and before EndFrame, so it is a
        // window into mid-frame state: the logic clock has advanced, the loop's has not.
        // (Before packet 2 this window was the unphased residue hook, in the same slot.)
        RecordScene(
            game,
            () => observedInsideFrame.Add((loop.CurrentFrame.Value, game.GameLogic.CurrentFrame.Value)));

        var created = CreateLoop(game);
        loop = created.Loop;

        // Both clocks start at zero on a freshly constructed host. Since packet 3 this is the
        // asserted invariant, not an incidental one: HeadedSimSystems.OnPhase crashes the game
        // if the two ever name different frames, and SimLoop.ResetTo is what a host uses to
        // restore the pairing after loading a save.
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
