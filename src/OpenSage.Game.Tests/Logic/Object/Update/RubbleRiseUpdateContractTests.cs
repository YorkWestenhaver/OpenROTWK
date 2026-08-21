// Mocked-game unit tests for the RubbleRiseUpdate port (api-freeze-v1 §6 fitness item 4): one
// test per behavior branch from the R13 task packet's contract-test plan
// (bfme2-workbench/research/modules-r13/specs/RubbleRiseUpdateModuleData.md §3), plus the
// shadow-copy base test and a mid-behavior save/load round-trip. Clones EmpUpdateContractTests'
// shape: real INI text through the real parser, HeadlessSimGame, [Fact] per branch.
//
// Sleepy-update caveat (spec §3): a freshly spawned module's first Update() runs on the
// SECOND HeadlessSimGame.Step(), not the first. The Initial-phase FX fires from the
// constructor (synchronously, at spawn, zero Step() calls needed); the Burst-phase transition
// fires from Update() (needs at least the first real Update() dispatch, i.e. the second
// Step()).

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class RubbleRiseUpdateContractTests
{
    private static readonly Vector3 OnGround = new(0, 0, 0);

    // 5 Hz logic rate (F6): 1000ms = 5 frames.
    private const string Definitions = @"
GameData
  Gravity = -1.0
End

Object RubbleZeroDelay
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RubbleRiseUpdate ModuleTag_Rubble
    MinRubbleRiseDelay = 0
    MaxRubbleRiseDelay = 0
    RubbleHeight        = 4.0
    MaxShudder          = 0.6
    MinBurstDelay       = 250
    MaxBurstDelay       = 250
    BigBurstFrequency   = 4
    FXList              = INITIAL FX_Initial
    FXList              = BURST FX_Burst
    FXList              = DELAY FX_Delay
  End
End

Object RubbleNonZeroDelay
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RubbleRiseUpdate ModuleTag_Rubble
    MinRubbleRiseDelay = 500
    MaxRubbleRiseDelay = 500
    MinBurstDelay       = 250
    MaxBurstDelay       = 250
    BigBurstFrequency   = 4
    FXList              = INITIAL FX_Initial
  End
End

Object RubbleAlwaysBigBurst
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RubbleRiseUpdate ModuleTag_Rubble
    MinRubbleRiseDelay = 0
    MaxRubbleRiseDelay = 0
    MinBurstDelay       = 250
    MaxBurstDelay       = 250
    BigBurstFrequency   = 1
    FXList              = BURST FX_Burst
  End
End

Object RubbleNeverBigBurst
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RubbleRiseUpdate ModuleTag_Rubble
    MinRubbleRiseDelay = 0
    MaxRubbleRiseDelay = 0
    MinBurstDelay       = 250
    MaxBurstDelay       = 250
    BigBurstFrequency   = 0
    FXList              = BURST FX_Burst
    FXList              = DELAY FX_Delay
  End
End

Object RubbleInitialOnly
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RubbleRiseUpdate ModuleTag_Rubble
    MinRubbleRiseDelay = 0
    MaxRubbleRiseDelay = 0
    MinBurstDelay       = 250
    MaxBurstDelay       = 250
    BigBurstFrequency   = 4
    FXList              = INITIAL FX_Initial
  End
End

Object RubbleWithDamping
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RubbleRiseUpdate ModuleTag_Rubble
    MinRubbleRiseDelay = 0
    MaxRubbleRiseDelay = 0
    RubbleRiseDamping  = 0.5
    RubbleHeight       = 4.0
    MinBurstDelay       = 250
    MaxBurstDelay       = 250
    BigBurstFrequency   = 4
    FXList              = INITIAL FX_Initial
    FXList              = BURST FX_Burst
  End
End

Object RubbleWithoutDamping
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RubbleRiseUpdate ModuleTag_Rubble
    MinRubbleRiseDelay = 0
    MaxRubbleRiseDelay = 0
    MinBurstDelay       = 250
    MaxBurstDelay       = 250
    BigBurstFrequency   = 4
    FXList              = INITIAL FX_Initial
    FXList              = BURST FX_Burst
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x2B012) // "rubble"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static RubbleRiseUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<RubbleRiseUpdate>().Single();

    private static string StateOf(RubbleRiseUpdate module)
    {
        var capture = new FieldCapture();
        module.Xfer(capture);
        return capture.EnumFields["State"];
    }

    /// <summary>
    /// A minimal <see cref="IXfer"/> that records named fields as the walk passes them,
    /// ignoring every other primitive kind. RubbleRiseUpdate's walk only ever calls
    /// XferVersion, XferEnum and XferFrame, so those other members are legitimately inert
    /// here (same idiom as EmpUpdateContractTests' FieldCapture).
    /// </summary>
    private sealed class FieldCapture : IXfer
    {
        public Dictionary<string, string> EnumFields { get; } = new();
        public Dictionary<string, LogicFrame> FrameFields { get; } = new();

        public XferMode Mode => XferMode.Save;
        public void BeginModule(in XferModuleId id) { }
        public void EndModule() { }
        public void XferFix64(string name, ref Fix64 value, Tolerance tol = Tolerance.Exact) { }
        public void XferFixVector3(string name, ref FixVector3 value, Tolerance tol = Tolerance.Exact) { }
        public void XferInt(string name, ref int value, Tolerance tol = Tolerance.Exact) { }
        public void XferUInt(string name, ref uint value, Tolerance tol = Tolerance.Exact) { }
        public void XferBool(string name, ref bool value) { }
        public void XferFrame(string name, ref LogicFrame value, Tolerance tol = Tolerance.Quantum) => FrameFields[name] = value;
        public void XferFrameSpan(string name, ref LogicFrameSpan value, Tolerance tol = Tolerance.Quantum) { }
        public void XferObjectId(string name, ref ObjectId value) { }
        public void XferEnum<T>(string name, ref T value) where T : struct, System.Enum => EnumFields[name] = value.ToString();
        public void XferBitArray(string name, ref BitArray512 value) { }
        public void XferList<T>(string name, List<T> list, XferItem<T> item) { }
        public byte XferVersion(byte currentVersion) => currentVersion;
    }

    // ---- test 1: ctor fires the Initial-phase FX unconditionally, no Step() needed ----

    [Fact]
    public void Construction_FiresInitialPhaseFxImmediately_NoStepNeeded()
    {
        var game = NewGame();
        var events = RecordingSimEvents.InstallOn(game);
        var rubble = game.SpawnObject("RubbleZeroDelay", game.CivilianPlayer, OnGround);

        Assert.Single(events.Events);
        Assert.Equal("FX_Initial", events.Events[0].FXListName);
        Assert.Equal(rubble.Id, events.Events[0].ObjectId);
        Assert.Equal(FXOrientation.PositionOnly, events.Events[0].Orientation);
    }

    // ---- test 2: first real Update() (the SECOND Step()) transitions to Rising, fires Burst ----

    [Fact]
    public void FirstUpdate_ZeroDelay_TransitionsToRising_FiresBurstPhaseFx()
    {
        var game = NewGame();
        var events = RecordingSimEvents.InstallOn(game);
        var rubble = game.SpawnObject("RubbleZeroDelay", game.CivilianPlayer, OnGround);
        var module = ModuleOf(rubble);

        game.Step(); // frame 1: nothing new yet - the module's first real Update() is the 2nd Step()
        Assert.Single(events.Events); // still just the ctor's Initial fire
        Assert.Equal("WaitingForRiseStart", StateOf(module));

        game.Step(); // frame 2: first real Update() dispatch - transitions to Rising, fires Burst
        Assert.Equal(2, events.Events.Count);
        Assert.Equal("FX_Burst", events.Events[1].FXListName);
        Assert.Equal("Rising", StateOf(module));
    }

    // ---- test 3: a nonzero rise delay withholds the Rising transition until the rise frame ----

    [Fact]
    public void NonZeroDelay_NoBurstBeforeRiseFrame()
    {
        // 500ms -> ceil(500*5/1000) = 3 frames.
        var game = NewGame();
        var events = RecordingSimEvents.InstallOn(game);
        var rubble = game.SpawnObject("RubbleNonZeroDelay", game.CivilianPlayer, OnGround);
        var module = ModuleOf(rubble);

        game.Step(); // frame 1
        game.Step(); // frame 2 (one less than the rise frame)
        Assert.Equal("WaitingForRiseStart", StateOf(module));
        Assert.Single(events.Events); // only the ctor's Initial fire

        game.Step(); // frame 3: rise frame reached
        Assert.Equal("Rising", StateOf(module));
        Assert.Equal(2, events.Events.Count);
    }

    // ---- test 4: the burst loop repeats indefinitely, no terminal state (F-RRU-1) ----

    [Fact]
    public void BurstLoop_RepeatsIndefinitely_NoTerminalState()
    {
        // MinBurstDelay = MaxBurstDelay = 250ms -> ceil(250*5/1000) = 2 frames.
        // BigBurstFrequency = 1 forces every roll to "big burst" - deterministic, no
        // RNG-branch flakiness.
        var game = NewGame();
        var events = RecordingSimEvents.InstallOn(game);
        var rubble = game.SpawnObject("RubbleAlwaysBigBurst", game.CivilianPlayer, OnGround);
        var module = ModuleOf(rubble);

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        // The Rising transition itself fires one unconditional Burst, plus every 2-frame
        // cadence tick after that also fires Burst (BigBurstFrequency = 1) - several fires
        // expected over 20 frames, and the module must never stop ticking.
        Assert.True(events.Events.Count(e => e.FXListName == "FX_Burst") >= 5,
            $"expected repeated Burst fires, got {events.Events.Count(e => e.FXListName == "FX_Burst")}");
        Assert.Equal("Rising", StateOf(module));
    }

    // ---- test 5: BigBurstFrequency <= 0 never rolls a big burst, no exception ----

    [Fact]
    public void BigBurstFrequency_ZeroOrNegative_NeverRollsBigBurst_NoException()
    {
        var game = NewGame();
        var events = RecordingSimEvents.InstallOn(game);
        var rubble = game.SpawnObject("RubbleNeverBigBurst", game.CivilianPlayer, OnGround);

        for (var i = 0; i < 20; i++)
        {
            game.Step();
        }

        // Frame 2 fires the unconditional Rising-transition Burst (step 2's fire, not subject
        // to the guard); every burst-loop roll after that must be Delay, never Burst again.
        Assert.Single(events.Events, e => e.FXListName == "FX_Burst");
        Assert.True(events.Events.Count(e => e.FXListName == "FX_Delay") >= 3,
            $"expected repeated Delay fires, got {events.Events.Count(e => e.FXListName == "FX_Delay")}");
    }

    // ---- test 6: a missing phase FX entry is a silent no-op ----

    [Fact]
    public void MissingPhaseFxEntry_IsSilentNoOp()
    {
        var game = NewGame();
        var events = RecordingSimEvents.InstallOn(game);
        var rubble = game.SpawnObject("RubbleInitialOnly", game.CivilianPlayer, OnGround);

        var exception = Record.Exception(() =>
        {
            for (var i = 0; i < 20; i++)
            {
                game.Step();
            }
        });

        Assert.Null(exception);
        // Only the ctor's Initial fire ever recorded - Burst/Delay entries are absent from
        // this object's FXList table and every phase transition through them is a no-op.
        Assert.Single(events.Events);
        Assert.Equal("FX_Initial", events.Events[0].FXListName);
    }

    // ---- test 7: RubbleHeight/RubbleRiseDamping parse but do not affect behavior (F-RRU-1) ----

    [Fact]
    public void RubbleHeightAndDamping_ParsedButUnconsumed()
    {
        var withDamping = NewGame();
        var withDampingEvents = RecordingSimEvents.InstallOn(withDamping);
        withDamping.SpawnObject("RubbleWithDamping", withDamping.CivilianPlayer, OnGround);

        var withoutDamping = NewGame();
        var withoutDampingEvents = RecordingSimEvents.InstallOn(withoutDamping);
        withoutDamping.SpawnObject("RubbleWithoutDamping", withoutDamping.CivilianPlayer, OnGround);

        var dampedData = (RubbleRiseUpdateModuleData)withDamping.AssetStore.ObjectDefinitions
            .GetByName("RubbleWithDamping").Behaviors["ModuleTag_Rubble"].Data;
        Assert.Equal(Fix64.FromDecimalLiteral("4.0"), dampedData.RubbleHeight);
        Assert.Equal(Fix64.FromDecimalLiteral("0.5"), dampedData.RubbleRiseDamping);

        for (var i = 0; i < 10; i++)
        {
            withDamping.Step();
            withoutDamping.Step();
        }

        // Identical FX-event timelines across both configurations over an identical Step()
        // sequence, with the same seed - pins F-RRU-1 as a running assertion.
        Assert.Equal(
            withDampingEvents.Events.Select(e => e.FXListName),
            withoutDampingEvents.Events.Select(e => e.FXListName));
    }

    // ---- shadow-copy + save/load round-trip ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidRisingState()
    {
        var game = NewGame();
        var rubble = game.SpawnObject("RubbleAlwaysBigBurst", game.CivilianPlayer, OnGround);
        var live = ModuleOf(rubble);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("RubbleAlwaysBigBurst", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidRisingState_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static string[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var events = RecordingSimEvents.InstallOn(game);
        var rubble = game.SpawnObject("RubbleAlwaysBigBurst", game.CivilianPlayer, OnGround);
        var module = ModuleOf(rubble);

        var trajectory = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            var before = events.Events.Count;
            game.Step();
            for (var j = before; j < events.Events.Count; j++)
            {
                trajectory.Add(events.Events[j].FXListName);
            }
        }

        return trajectory.ToArray();
    }
}
