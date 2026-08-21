// Mocked-game unit tests for the EmpUpdate port (api-freeze-v1 §6 fitness item 4): one test
// per behavior branch from the R12 task packet, [create -> tick -> observable effect], plus
// the shadow-copy base test and a mid-behavior save/load round-trip.
//
// The observables are: the module's tracked scale (Fix64 sim state, F-EMP-5 - never pushed to
// a renderer, but its convergence is real translated behavior), the DisabledType.Emp flag and
// its expiry frame, whether a candidate got killed/destroyed, and the recorded
// FireParticleSystemAtObject events (ISimEvents, F-EMP-1).
//
// F-EMP-3 (filed in EmpUpdate.cs): GameObject.IsFactionStructure is a standing `=> false`
// stub, so the STRUCTURE branch of doDisableAttack never currently passes. Structure coverage
// here is therefore limited to "never disabled, never throws" rather than "disabled like a
// vehicle" - ported faithfully to the existing engine behavior, not invented around.

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

public class EmpUpdateContractTests
{
    private static readonly Vector3 OnGround = new(0, 0, 0);
    private static readonly Vector3 HighUp = new(0, 0, 500);

    // 5 Hz logic rate (F6): 1000ms = 5 frames.
    private const string Definitions = @"
GameData
  Gravity = -1.0
End

FXParticleSystem PS_Disable
End

Object EmpBomb
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EMPUpdate ModuleTag_Emp
    Lifetime = 2000
    StartFadeTime = 1000
    DisabledDuration = 1000
    StartScale = 0.5
    TargetScaleMin = 2.0
    TargetScaleMax = 2.0
    EffectRadius = 300
    DisableFXParticleSystem = PS_Disable
  End
End

Object EmpBombFiltered
  KindOf = IMMOBILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = EMPUpdate ModuleTag_Emp
    Lifetime = 2000
    StartFadeTime = 1000
    DisabledDuration = 1000
    StartScale = 1.0
    TargetScaleMin = 1.0
    TargetScaleMax = 1.0
    EffectRadius = 300
    DoesNotAffect = ALLIES
    DoesNotAffectMyOwnBuildings = Yes
  End
End

Object EmpProducer
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = AIUpdateInterface ModuleTag_AI
  End
End

Object EmpTestTank
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object EmpTestTurret
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object EmpTestGrunt
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object EmpTestChopper
  KindOf = AIRCRAFT
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object EmpTestHardenedChopper
  KindOf = AIRCRAFT EMP_HARDENED
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object EmpTestCargoPlane
  KindOf = AIRCRAFT TRANSPORT
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xE117) // "empup"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static EmpUpdate ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<EmpUpdate>().Single();

    // ---- test case 1: scale interpolation reaches (close to) target by lifetime end ----

    [Fact]
    public void ScaleInterpolation_MonotonicallyConvergesTowardRandomizedTarget()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var module = ModuleOf(emp);

        // StartScale 0.5, TargetScaleMin == TargetScaleMax == 2.0 (deterministic draw). The
        // GPL blend (5% of the remaining gap per frame) is asymptotic: over the object's short
        // 10-frame Lifetime it will not reach 2.0, but it must move monotonically closer to it
        // every single tick and never overshoot.
        var start = Fix64.FromDecimalLiteral("0.5");
        var target = Fix64.FromDecimalLiteral("2.0");
        Assert.Equal(start, ScaleOf(module));

        // The very first Step() only advances the frame counter from 0 to 1 without running a
        // tick yet (the module's first wake frame is 1, matching UpdateSleepTime.None's 1-frame
        // delay), so checkpoints are sampled a few steps apart rather than on every consecutive
        // pair - each gap below is guaranteed to contain at least one real tick.
        game.Step();
        game.Step();
        var afterTwo = ScaleOf(module);
        Assert.True(afterTwo > start, $"expected progress toward target after two steps, got {afterTwo}");
        Assert.True(afterTwo < target);

        for (var i = 0; i < 7; i++)
        {
            game.Step();
        }
        var afterNine = ScaleOf(module);
        Assert.True(afterNine > afterTwo, $"expected continued convergence, got {afterTwo} -> {afterNine}");
        Assert.True(afterNine < target, "the asymptotic blend must never reach or overshoot the target");
    }

    private static Fix64 ScaleOf(EmpUpdate module)
    {
        // The tracked scale is private sim state (F-EMP-5); read it back by name through the
        // module's own Xfer walk, the same one the CRC/save-load tests exercise, rather than
        // reflection or hand-parsed byte offsets.
        var capture = new FieldCapture();
        module.Xfer(capture);
        return capture.Fix64Fields["CurrentScale"];
    }

    /// <summary>
    /// A minimal <see cref="IXfer"/> that records named Fix64 fields as the walk passes them,
    /// ignoring every other primitive kind. EmpUpdate's walk only ever calls XferVersion,
    /// XferFrame and XferFix64, so those other members are legitimately inert here.
    /// </summary>
    private sealed class FieldCapture : IXfer
    {
        public Dictionary<string, Fix64> Fix64Fields { get; } = new();

        public XferMode Mode => XferMode.Save;
        public void BeginModule(in XferModuleId id) { }
        public void EndModule() { }
        public void XferFix64(string name, ref Fix64 value, Tolerance tol = Tolerance.Exact) => Fix64Fields[name] = value;
        public void XferFixVector3(string name, ref FixVector3 value, Tolerance tol = Tolerance.Exact) { }
        public void XferInt(string name, ref int value, Tolerance tol = Tolerance.Exact) { }
        public void XferUInt(string name, ref uint value, Tolerance tol = Tolerance.Exact) { }
        public void XferBool(string name, ref bool value) { }
        public void XferFrame(string name, ref LogicFrame value, Tolerance tol = Tolerance.Quantum) { }
        public void XferFrameSpan(string name, ref LogicFrameSpan value, Tolerance tol = Tolerance.Quantum) { }
        public void XferObjectId(string name, ref ObjectId value) { }
        public void XferEnum<T>(string name, ref T value) where T : struct, System.Enum { }
        public void XferBitArray(string name, ref BitArray512 value) { }
        public void XferList<T>(string name, List<T> list, XferItem<T> item) { }
        public byte XferVersion(byte currentVersion) => currentVersion;
    }

    // ---- test case 2: disabling attack fires exactly once, at StartFadeTime ----

    [Fact]
    public void DisablingAttack_FiresExactlyOnce_AtStartFadeTime()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var victim = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        var events = RecordingSimEvents.InstallOn(game);

        // A tick with CurrentFrame == F only runs once F-1 prior Step() calls have already run
        // (the module's first wake frame is CurrentFrame 1, matching UpdateSleepTime.None's
        // 1-frame delay), so the tick that sees StartFadeTime's frame 5 runs on the 6th Step().
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }
        Assert.False(victim.IsDisabledByType(DisabledType.Emp), "must not fire before StartFadeTime");
        Assert.Empty(events.ParticleSystems);

        game.Step(); // the 6th step: tick sees CurrentFrame == 5 == StartFadeTime, fires
        Assert.True(victim.IsDisabledByType(DisabledType.Emp), "must fire exactly at StartFadeTime");
        Assert.Single(events.ParticleSystems);

        // "Exactly once" is precise here in a way IsDisabledByType alone cannot show (it just
        // stays true): each DoDisableAttack() call requests one particle event per victim, so
        // a second firing would double the count. Frame equality against a monotonically
        // increasing counter can only ever hold once, but this pins the observable anyway.
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }
        Assert.True(victim.IsDisabledByType(DisabledType.Emp));
        Assert.Single(events.ParticleSystems);
    }

    // ---- test case 3: vehicles within EffectRadius disabled, for DisabledDuration ----

    [Fact]
    public void VehicleWithinRadius_GetsDisabled_ThenAutoClearsAfterDisabledDuration()
    {
        // F-EMP-6 - CLOSED (A0-prime): GameObject.CheckDisabledStates (the sweep that
        // auto-clears DisabledType.Emp once its recorded expiry frame passes) is now called
        // from GameObject.Update(), which GameLogic.Update() wires in once per object per
        // frame. This module's own Disable() call records
        // Context.CurrentFrame(=5, StartFadeTime) + DisabledDuration(5) = expiry frame 10;
        // GameLogic.Update() runs the auto-expiry sweep AFTER incrementing its frame counter
        // (T-frame window clears at T+1), so the tank stays disabled through CurrentFrame 10
        // and clears once CurrentFrame reaches 11.
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var tank = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        for (var i = 0; i < 6; i++) // tick sees CurrentFrame == 5 == StartFadeTime on the 6th step
        {
            game.Step();
        }
        Assert.True(tank.IsDisabledByType(DisabledType.Emp));

        for (var i = 0; i < 4; i++) // CurrentFrame 7..10: still within the recorded window
        {
            game.Step();
            Assert.True(tank.IsDisabledByType(DisabledType.Emp));
        }

        game.Step(); // CurrentFrame 11: auto-expiry sweep clears it
        Assert.False(tank.IsDisabledByType(DisabledType.Emp));
    }

    [Fact]
    public void StructureWithinRadius_NeverDisabled_KnownEngineLimitation()
    {
        // F-EMP-3: GameObject.IsFactionStructure is a standing `=> false` stub, so the
        // STRUCTURE branch of doDisableAttack can never currently pass. This test pins the
        // CURRENT (incomplete) engine behavior rather than the full GPL contract, and will
        // need updating once IsFactionStructure is implemented for real.
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var turret = game.SpawnObject("EmpTestTurret", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(turret.IsDisabledByType(DisabledType.Emp));
    }

    [Fact]
    public void Infantry_NeverDisabled()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var grunt = game.SpawnObject("EmpTestGrunt", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(grunt.IsDisabledByType(DisabledType.Emp));
        Assert.False(grunt.IsDestroyed);
    }

    // ---- test case 4: airborne aircraft killed; allied transports spared ----

    [Fact]
    public void AirborneEnemyAircraft_IsKilled()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var chopper = game.SpawnObject("EmpTestChopper", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, HighUp.Z));
        Assert.True(chopper.IsSignificantlyAboveTerrain);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.True(chopper.IsDestroyed);
    }

    [Fact]
    public void GroundedAircraft_IsNotKilled_ButIsDisabledInstead()
    {
        // Not "significantly above terrain" -> falls through to the ordinary disable path
        // rather than the airborne-kill branch (GPL only kills a currently-airborne aircraft).
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var chopper = game.SpawnObject("EmpTestChopper", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, 0));
        Assert.False(chopper.IsSignificantlyAboveTerrain);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(chopper.IsDestroyed);
        Assert.True(chopper.IsDisabledByType(DisabledType.Emp));
    }

    [Fact]
    public void AirborneEmpHardenedAircraft_IsSpared()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var hardened = game.SpawnObject("EmpTestHardenedChopper", game.PlayerManager.NeutralPlayer, new Vector3(50, 0, HighUp.Z));

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(hardened.IsDestroyed);
    }

    // NOTE: HeadlessSimGame only ever seeds two players (NeutralPlayer, CivilianPlayer), and
    // GameObject.GetRelationship is Team/Player-relationship-based (Player.GetRelationship),
    // NOT the separate Player.Allies/Enemies hash sets some other modules read directly - by
    // design every relationship defaults to Neutral until a test calls Player.SetRelationship
    // explicitly (see that method's doc comment). Each of the two cases below therefore gets
    // its own game so the one relationship override in play is unambiguous.

    [Fact]
    public void AirborneAlliedTransport_IsSpared()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var alliedTransport = game.SpawnObject("EmpTestCargoPlane", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, HighUp.Z));

        // candidate.GetRelationship(self) resolves through the CANDIDATE's owner, so the
        // override belongs on the transport's player, pointed at the EMP's player.
        game.PlayerManager.NeutralPlayer.SetRelationship(game.CivilianPlayer, RelationshipType.Allies);

        // GetRelationship also short-circuits to Neutral whenever either object's Team is
        // null - and HeadlessSimGame.SpawnObject never assigns one - so give both a
        // singleton team (same construction SabotageSupplyCenterCrateCollideContractTests
        // uses) or the SetRelationship override above is never actually observed.
        emp.Team = new Team(new TeamTemplate(game.TeamFactory, 901, "EmpTeam", game.CivilianPlayer, isSingleton: true), 901);
        alliedTransport.Team = new Team(new TeamTemplate(game.TeamFactory, 902, "TransportTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 902);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(alliedTransport.IsDestroyed, "GPL: DONT DISABLE YOUR OWN TRANSPORT PLANES");
    }

    [Fact]
    public void AirborneEnemyTransport_IsKilled()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var enemyTransport = game.SpawnObject("EmpTestCargoPlane", game.PlayerManager.NeutralPlayer, new Vector3(-30, 0, HighUp.Z));

        game.PlayerManager.NeutralPlayer.SetRelationship(game.CivilianPlayer, RelationshipType.Enemies);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.True(enemyTransport.IsDestroyed, "a non-allied transport gets no exemption");
    }

    // ---- test case 5: DoesNotAffect / DoesNotAffectMyOwnBuildings exemptions ----

    // F-EMP-2 (R13 fix): GPL's DoesNotAffect is a WeaponAffectsTypes (TheWeaponAffectsMaskNames)
    // reject mask, and its only live use in EMPUpdate.cpp is an `else if` sibling of the
    // STRUCTURE branch that exempts an ALLIED vehicle/SPAWNS_ARE_THE_WEAPONS/grounded-aircraft
    // candidate specifically when WEAPON_AFFECTS_ALLIES is rejected
    // (EMPUpdate.cpp:281) - NOT an ObjectFilter KindOf match. The two tests below assert that
    // GPL-correct semantics: an ALLIED vehicle is exempted, a non-allied one is not, mirroring
    // the AirborneAlliedTransport_IsSpared / AirborneEnemyTransport_IsKilled pair above.

    [Fact]
    public void DoesNotAffectAllies_ExemptsAlliedVehicle()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBombFiltered", game.CivilianPlayer, OnGround); // DoesNotAffect = ALLIES
        var alliedTank = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));

        // candidate.GetRelationship(self) resolves through the CANDIDATE's owner, so the
        // override belongs on the tank's player, pointed at the EMP's player - same shape as
        // AirborneAlliedTransport_IsSpared above.
        game.PlayerManager.NeutralPlayer.SetRelationship(game.CivilianPlayer, RelationshipType.Allies);
        emp.Team = new Team(new TeamTemplate(game.TeamFactory, 905, "EmpTeam3", game.CivilianPlayer, isSingleton: true), 905);
        alliedTank.Team = new Team(new TeamTemplate(game.TeamFactory, 906, "AlliedTankTeam", game.PlayerManager.NeutralPlayer, isSingleton: true), 906);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(alliedTank.IsDisabledByType(DisabledType.Emp), "WEAPON_AFFECTS_ALLIES is rejected by DoesNotAffect = ALLIES");
    }

    [Fact]
    public void DoesNotAffectAllies_DoesNotExemptNonAlliedVehicle()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBombFiltered", game.CivilianPlayer, OnGround); // DoesNotAffect = ALLIES
        var neutralTank = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.True(neutralTank.IsDisabledByType(DisabledType.Emp), "the reject mask only exempts ALLIES, not neutral/enemy candidates");
    }

    [Fact]
    public void DoesNotAffectMyOwnBuildings_ExemptsOwnStructure_DoesNotThrow()
    {
        // F-EMP-3: structures never disable regardless (IsFactionStructure stub), so this
        // confirms the DoesNotAffectMyOwnBuildings guard evaluates cleanly for an own-owned
        // structure without altering the (already-negative) outcome or throwing.
        var game = NewGame();
        var emp = game.SpawnObject("EmpBombFiltered", game.CivilianPlayer, OnGround);
        var ownTurret = game.SpawnObject("EmpTestTurret", game.CivilianPlayer, new Vector3(30, 0, 0));

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(ownTurret.IsDisabledByType(DisabledType.Emp));
        Assert.False(ownTurret.IsDestroyed);
    }

    // ---- test case 5b: onlyEffectAirborne (F-EMP-7, R13 fix) ----

    [Fact]
    public void ProducerIntendedVictimIsAirborne_RestrictsScanToAirborneOnly_GroundVehicleSpared()
    {
        // GPL EMPUpdate.cpp:186-199: when the EMP's producer's AI-intended victim is airborne,
        // the whole radius scan is restricted to airborne-only candidates - ground vehicles in
        // the blast radius are NOT disabled.
        var game = NewGame();
        var producer = game.SpawnObject("EmpProducer", game.CivilianPlayer, OnGround);
        var intendedVictim = game.SpawnObject("EmpTestChopper", game.PlayerManager.NeutralPlayer, new Vector3(80, 0, HighUp.Z));
        producer.AIUpdate.SetCurrentVictim(intendedVictim.Id);

        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        emp.CreatedByObjectID = producer.Id;
        var groundTank = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.False(groundTank.IsDisabledByType(DisabledType.Emp),
            "a ground vehicle must be spared when the EMP's producer intended an airborne target");
        // The airborne-aircraft branch itself is unaffected by the restriction: an airborne
        // candidate found by the (now airborne-only) scan is still killed as normal.
        Assert.True(intendedVictim.IsDestroyed);
    }

    [Fact]
    public void ProducerIntendedVictimIsGrounded_DoesNotRestrictScan()
    {
        // Control case: when the producer's AI-intended victim is NOT airborne (or there is no
        // producer/AI/intended victim at all), the scan is unrestricted - matching every other
        // test in this file, none of which sets up a producer.
        var game = NewGame();
        var producer = game.SpawnObject("EmpProducer", game.CivilianPlayer, OnGround);
        var intendedVictim = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(80, 0, 0));
        producer.AIUpdate.SetCurrentVictim(intendedVictim.Id);

        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        emp.CreatedByObjectID = producer.Id;
        var groundTank = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.True(groundTank.IsDisabledByType(DisabledType.Emp));
    }

    // ---- test case 6: disable-FX particle system requested per disabled victim ----

    [Fact]
    public void DisableFxParticleSystem_RequestedOncePerDisabledVictim()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var tankA = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        var tankB = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(-30, 0, 0));
        var events = RecordingSimEvents.InstallOn(game);

        for (var i = 0; i < 6; i++)
        {
            game.Step();
        }

        Assert.Equal(2, events.ParticleSystems.Count);
        Assert.All(events.ParticleSystems, ps => Assert.Equal("PS_Disable", ps.ParticleSystemName));
        Assert.Contains(events.ParticleSystems, ps => ps.ObjectId == tankA.Id);
        Assert.Contains(events.ParticleSystems, ps => ps.ObjectId == tankB.Id);
    }

    // ---- test case 7: EMP object killed exactly at Lifetime, no persistence beyond ----

    [Fact]
    public void EmpObject_KilledExactlyAtLifetime()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);

        // Lifetime = 2000ms = 10 frames; the tick that sees CurrentFrame == 10 runs on the
        // 11th Step() (the module's first wake frame is CurrentFrame 1, not 0).
        for (var i = 0; i < 10; i++)
        {
            game.Step();
            Assert.False(emp.IsDestroyed, $"must not die before Lifetime elapses (step {i})");
        }

        game.Step(); // the 11th step: tick sees CurrentFrame == 10 == Lifetime, dies
        Assert.True(emp.IsDestroyed);
    }

    // ---- shadow-copy + save/load round-trip ----

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var live = ModuleOf(emp);

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("EmpBomb", game.CivilianPlayer, new Vector3(300, 0, 0));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 4);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xF00D);
        var emp = game.SpawnObject("EmpBomb", game.CivilianPlayer, OnGround);
        var tank = game.SpawnObject("EmpTestTank", game.PlayerManager.NeutralPlayer, new Vector3(30, 0, 0));
        var module = ModuleOf(emp);

        var trajectory = new bool[9];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk;
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = tank.IsDisabledByType(DisabledType.Emp);
        }

        return trajectory;
    }
}
