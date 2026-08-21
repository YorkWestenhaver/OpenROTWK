// Mocked-game unit tests for the MobMemberSlavedUpdate port (api-freeze-v1 §6 fitness item 4):
// one test per landed-reachable behavior branch, [create -> tick -> observable effect], plus the
// mid-behavior save/load round-trip and the shadow-copy base test. Object definitions and the
// locomotor templates are parsed from INI text through the real parser, so the quantizing S5
// radius/count parses are on the tested path.
//
// The observables are the S2 movement seam the module drives: the member's locomotor SET type
// (snapped to SET_PANIC when catching up), its move MODE (MoveToPosition once a catch-up move is
// issued), its sim POSITION (closing on the master), and its life (self-destruct when orphaned or
// isolated too long). The un-landed self-task / mood / path-lead-lag refinements are findings
// (F-MMS-*), not tested here.

using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class MobMemberSlavedUpdateContractTests
{
    // Speed 30/s -> 6/frame at the frozen 5 Hz; MustCatchUpRadius 50 -> crisis radius 150.
    // CatchUpCrisisBailTime 1: two consecutive critically-far heavy ticks self-destruct.
    private const string Definitions = @"
Locomotor MobLoco
  Surfaces = GROUND
  Speed = 30
  TurnRate = 360
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object MobNexus
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Locomotor = SET_NORMAL MobLoco
End

Object MobMember
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
  End
  Behavior = MobMemberSlavedUpdate ModuleTag_Mob
    MustCatchUpRadius = 50
    NoNeedToCatchUpRadius = 25
    CatchUpCrisisBailTime = 1
  End
  Locomotor = SET_NORMAL MobLoco
  Locomotor = SET_PANIC MobLoco
  Locomotor = SET_WANDER MobLoco
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB0B)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static MobMemberSlavedUpdate MobOf(GameObject obj) =>
        obj.BehaviorModules.OfType<MobMemberSlavedUpdate>().Single();

    private static SimLocomotorUpdate LocoOf(GameObject obj) =>
        obj.BehaviorModules.OfType<SimLocomotorUpdate>().First();

    private static Fix64 F(string s) => Fix64.FromDecimalLiteral(s);

    // The ctor stagger biases the first heavy tick into [1, 16]; the throttle then fires every
    // 16 frames. Stepping this many frames guarantees at least two heavy ticks for any stagger.
    private static void Step(HeadlessSimGame game, int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            game.Step();
        }
    }

    // ---------------------------------------------------------------- orphan self-destruct

    [Fact]
    public void NoMaster_MemberSelfDestructs()
    {
        // Not enslaved: the master lookup (every frame, before the throttle) finds nothing and
        // the member kills itself immediately - GPL's orphaned-member behavior and the first
        // half of the mob-invincibility failsafe.
        var game = NewGame();
        var member = game.SpawnObject("MobMember", game.CivilianPlayer, new Vector3(10, 0, 0));

        Assert.False(member.IsEffectivelyDead);
        // The module's first wake is one frame after spawn (SetWakeFrame(None) = now+1); the
        // master lookup then finds nothing (id Invalid -> reserved null slot) and kills self.
        Step(game, 4);
        Assert.True(member.IsEffectivelyDead);
    }

    // ---------------------------------------------------------------- too far -> catch up

    [Fact]
    public void TooFarFromStillMaster_SnapsToPanicAndMovesToMaster()
    {
        // Member 100 units from an idle master: beyond MustCatchUpRadius (50) but inside the
        // crisis radius (150). It must snap to SET_PANIC and head for the master.
        var game = NewGame();
        var master = game.SpawnObject("MobNexus", game.CivilianPlayer, Vector3.Zero);
        var member = game.SpawnObject("MobMember", game.CivilianPlayer, new Vector3(100, 0, 0));
        MobOf(member).SetSlaver(master);
        var loco = LocoOf(member);

        var sawPanic = false;
        for (var i = 0; i < 20; i++)
        {
            game.Step();
            sawPanic |= loco.CurrentSetType == LocomotorSetType.Panic;
        }

        Assert.True(sawPanic, "member never snapped to SET_PANIC to catch up");
        Assert.False(member.IsEffectivelyDead);              // not critical -> no bail
        Assert.True(loco.Physics.Position.X < F("100"),      // moved toward the master at origin
            $"member did not close on the master; x = {loco.Physics.Position.X}");
    }

    // ---------------------------------------------------------------- critically far -> bail

    [Fact]
    public void CriticallyFarTooLong_MemberBailsAndSelfDestructs()
    {
        // Member 400 units out: beyond 3*MustCatchUpRadius (150). With CatchUpCrisisBailTime 1,
        // the crisis counter passes the threshold on the second critically-far heavy tick and
        // the member self-destructs (the failsafe's teardown half).
        var game = NewGame();
        var master = game.SpawnObject("MobNexus", game.CivilianPlayer, Vector3.Zero);
        var member = game.SpawnObject("MobMember", game.CivilianPlayer, new Vector3(400, 0, 0));
        MobOf(member).SetSlaver(master);

        var died = false;
        for (var i = 0; i < 60 && !died; i++)
        {
            game.Step();
            died = member.IsEffectivelyDead;
        }

        Assert.True(died, "critically-far member never bailed");
    }

    // ---------------------------------------------------------------- close -> stay put

    [Fact]
    public void CloseToIdleMaster_DoesNotCatchUpAndStops()
    {
        // Member 20 units from an idle master: inside MustCatchUpRadius. It must NOT snap to
        // panic or run a catch-up move; with the master idle it stops and holds position.
        var game = NewGame();
        var master = game.SpawnObject("MobNexus", game.CivilianPlayer, Vector3.Zero);
        var member = game.SpawnObject("MobMember", game.CivilianPlayer, new Vector3(20, 0, 0));
        MobOf(member).SetSlaver(master);
        var loco = LocoOf(member);

        Step(game, 20);

        Assert.False(member.IsEffectivelyDead);
        Assert.NotEqual(LocomotorSetType.Panic, loco.CurrentSetType);   // never had to catch up
        // Held station near the spawn (no catch-up move dragged it toward the master).
        Assert.True(Fix64.Abs(loco.Physics.Position.X - F("20")) < F("1"),
            $"member drifted from station; x = {loco.Physics.Position.X}");
    }

    // ---------------------------------------------------------------- the walk

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var master = game.SpawnObject("MobNexus", game.CivilianPlayer, Vector3.Zero);
        var member = game.SpawnObject("MobMember", game.CivilianPlayer, new Vector3(100, 0, 0));
        MobOf(member).SetSlaver(master);

        // Drive real state into the module: the stagger counter advances and a catch-up runs.
        Step(game, 20);
        var live = MobOf(member);

        // Shadow: the same class over the same data on a second member, differently stated.
        var shadowHost = game.SpawnObject("MobMember", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = MobOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        // Two identical games, identical script. Game B round-trips the module state (and the
        // engine-owned wake frame, S6) through Save->Load mid-behavior; if the load path lost or
        // misread the slaver id, the stagger counter, or the crisis counter, B's position
        // trajectory diverges from A's.
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 9);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static long[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var master = game.SpawnObject("MobNexus", game.CivilianPlayer, Vector3.Zero);
        var member = game.SpawnObject("MobMember", game.CivilianPlayer, new Vector3(120, 0, 0));
        var module = MobOf(member);
        module.SetSlaver(master);
        var loco = LocoOf(member);

        var trajectory = new long[24];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;     // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = loco.Physics.Position.X.RawValue;
        }

        return trajectory;
    }
}
