// Mocked-game unit tests for the CountermeasuresBehavior R12 port (api-freeze-v1 §6 fitness
// item 4): one test per task-packet behavior, plus the mid-behavior save/load round-trip and
// the shadow-copy base test. Object/Locomotor definitions are parsed from INI text through the
// real parser, so the quantizing S5 parses (DelayBetweenVolleys/ReloadTime/
// ReactionLaunchLatency -> LogicFrameSpan, VolleyArcAngle -> Fix64 radians, EvasionRate ->
// Fix64 percentage) are on the tested path.
//
// EvasionRate is tested at its 100%/0% extremes (AlwaysEvades/NeverEvades) rather than at a
// fractional draw: the observable effect under test is reportMissileForCountermeasures'
// CONTROL FLOW (arm-the-reaction-timer-on-success vs. do-nothing-on-failure), which the
// extremes exercise deterministically without coupling the test to a specific RNG draw value.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class CountermeasuresBehaviorContractTests
{
    // 5 Hz (F6): ReactionLaunchLatency 200ms -> 1 frame, MissileDecoyDelay 400ms -> 2 frames,
    // DelayBetweenVolleys 1000ms -> 5 frames, ReloadTime 2000ms -> 10 frames.
    private const string Definitions = @"
Upgrade Upgrade_Flares
  Type = PLAYER
End

Locomotor AirLoco
  Surfaces = AIR
  Speed = 60
  TurnRate = 90
  Acceleration = 100
  Braking = 100
  Appearance = TWO_LEGS
  ZAxisBehavior = NO_Z_MOTIVE_FORCE
End

Object Flare
  KindOf = PROJECTILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
    Gravity = 0
  End
  Locomotor = SET_NORMAL AirLoco
End

Object Missile
  KindOf = PROJECTILE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End

Object AlwaysEvadesJet
  KindOf = VEHICLE AIRCRAFT
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
    Gravity = 0
  End
  Behavior = CountermeasuresBehavior ModuleTag_CM
    TriggeredBy = Upgrade_Flares
    FlareTemplateName = Flare
    VolleySize = 3
    VolleyArcAngle = 45
    VolleyVelocityFactor = 2.0
    DelayBetweenVolleys = 1000
    NumberOfVolleys = 2
    ReloadTime = 2000
    EvasionRate = 100%
    ReactionLaunchLatency = 200
    MissileDecoyDelay = 400
  End
  Locomotor = SET_NORMAL AirLoco
End

Object NeverEvadesJet
  KindOf = VEHICLE AIRCRAFT
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
    Gravity = 0
  End
  Behavior = CountermeasuresBehavior ModuleTag_CM
    TriggeredBy = Upgrade_Flares
    FlareTemplateName = Flare
    VolleySize = 3
    VolleyArcAngle = 45
    VolleyVelocityFactor = 2.0
    DelayBetweenVolleys = 1000
    NumberOfVolleys = 2
    ReloadTime = 2000
    EvasionRate = 0%
    ReactionLaunchLatency = 200
    MissileDecoyDelay = 400
  End
  Locomotor = SET_NORMAL AirLoco
End

Object NoReloadJet
  KindOf = VEHICLE AIRCRAFT
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 500
  End
  Behavior = SimLocomotorUpdate ModuleTag_Loco
    Gravity = 0
  End
  Behavior = CountermeasuresBehavior ModuleTag_CM
    TriggeredBy = Upgrade_Flares
    FlareTemplateName = Flare
    VolleySize = 1
    VolleyArcAngle = 0
    VolleyVelocityFactor = 1.0
    DelayBetweenVolleys = 200
    NumberOfVolleys = 1
    ReloadTime = 0
    EvasionRate = 100%
    ReactionLaunchLatency = 200
    MissileDecoyDelay = 400
  End
  Locomotor = SET_NORMAL AirLoco
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEE) =>
        NewGameFrom(Definitions, seed);

    private static HeadlessSimGame NewGameFrom(string definitions, uint seed)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(definitions);
        return game;
    }

    private static CountermeasuresBehavior ModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<CountermeasuresBehavior>().Single();

    private static void ActivateUpgrade(HeadlessSimGame game, CountermeasuresBehavior module)
    {
        var upgrades = new UpgradeSet { game.AssetStore.Upgrades.GetByName("Upgrade_Flares") };
        module.TryUpgrade(upgrades);
    }

    // ------------------------------------------------------------------ missile report -> reaction -> volley

    [Fact]
    public void MissileReport_EvasionSucceeds_ArmsReactionThenLaunchesVolleyWithArcSpread()
    {
        var game = NewGame();
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);
        Assert.True(module.IsActive);

        module.ReportMissileForCountermeasures(missile);

        Assert.Equal(1u, module.IncomingMissiles);
        Assert.Equal(1u, module.DivertedMissiles);          // EvasionRate 100% -> always evades
        Assert.NotEqual(LogicFrame.Zero, module.ReactionFrame); // reaction timer armed
        Assert.Empty(module.CounterMeasures);                // not launched yet - waiting on the timer

        // ReactionLaunchLatency = 200ms = 1 frame; step past it.
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.Equal(LogicFrame.Zero, module.ReactionFrame); // consumed once the volley fired
        Assert.Equal(3, module.CounterMeasures.Count);        // VolleySize
        Assert.Equal(3u, module.ActiveCountermeasures);
        Assert.Equal(3u, module.AvailableCountermeasures);    // 2*3 - 3 launched
    }

    [Fact]
    public void MissileReport_EvasionFails_NeverArmsReaction()
    {
        var game = NewGame();
        var jet = game.SpawnObject("NeverEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);
        module.ReportMissileForCountermeasures(missile);

        Assert.Equal(1u, module.IncomingMissiles);
        Assert.Equal(0u, module.DivertedMissiles);           // EvasionRate 0% -> never evades
        Assert.Equal(LogicFrame.Zero, module.ReactionFrame); // never armed

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        Assert.Empty(module.CounterMeasures);                // nothing ever launched
    }

    // ------------------------------------------------------------------ volley ratio/angle/velocity formula

    [Fact]
    public void VolleySize3_ComputesExtremeAndCenterRatiosWithArcAngleAndVelocityFactor()
    {
        var game = NewGame();
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);
        module.ReportMissileForCountermeasures(missile);

        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var launches = module.LastVolleyLaunches;
        Assert.Equal(3, launches.Count);

        // ratio = i / (volleySize - 1) * 2 - 1 for i in [0, volleySize)
        Assert.Equal(Fix64.FromDecimalLiteral("-1"), launches[0].Ratio);
        Assert.Equal(Fix64.Zero, launches[1].Ratio);
        Assert.Equal(Fix64.One, launches[2].Ratio);

        // angle = ratio * VolleyArcAngle (45 degrees in radians).
        var arcAngleRadians = Fix64.Pi / Fix64.FromDecimalLiteral("4");
        AssertApproximately(-arcAngleRadians, launches[0].Angle);
        Assert.Equal(Fix64.Zero, launches[1].Angle);
        AssertApproximately(arcAngleRadians, launches[2].Angle);

        // Aircraft never moved (VelocityMagnitude < 1) so GPL's fallback speed of -10 applies,
        // scaled by VolleyVelocityFactor (2.0): kick magnitude is |-10 * 2.0| = 20 for every
        // flare, direction varying per the arc angle.
        foreach (var launch in launches)
        {
            var magnitudeSquared = launch.KickVelocity.X * launch.KickVelocity.X + launch.KickVelocity.Y * launch.KickVelocity.Y;
            // Tolerance is looser here than the angle checks below: squaring a LUT-approximated
            // unit vector (FixTrig's measured error ~3.2e-5, api-freeze-v1 F2) amplifies the
            // relative error, so this is a formula sanity check, not a bit-exact assertion.
            AssertApproximately(Fix64.FromDecimalLiteral("400"), magnitudeSquared, Fix64.FromDecimalLiteral("0.5")); // 20^2
        }
    }

    private static void AssertApproximately(Fix64 expected, Fix64 actual, Fix64? toleranceOverride = null)
    {
        var tolerance = toleranceOverride ?? Fix64.FromDecimalLiteral("0.01");
        var delta = expected - actual;
        if (delta < Fix64.Zero)
        {
            delta = -delta;
        }
        Assert.True(delta < tolerance, $"expected {expected}, got {actual}");
    }

    // ------------------------------------------------------------------ volley cadence / supply / reload

    [Fact]
    public void SuccessiveVolleysFireAtDelayIntervalThenAutoReloadsAfterDepletion()
    {
        var game = NewGame();
        var jet = game.SpawnObject("NoReloadJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);
        // NumberOfVolleys=1 * VolleySize=1 -> total available = 1.
        Assert.Equal(1u, module.AvailableCountermeasures);

        module.ReportMissileForCountermeasures(missile);

        // ReactionLaunchLatency = 1 frame; DelayBetweenVolleys = 1 frame; ReloadTime = 0 (no
        // auto-reload) - after the single available shot fires, supply stays at zero forever.
        for (var i = 0; i < 4; i++)
        {
            game.Step();
        }

        Assert.Equal(1, module.CounterMeasures.Count);
        Assert.Equal(0u, module.AvailableCountermeasures);
        Assert.Equal(LogicFrame.Zero, module.ReloadFrame); // ReloadTime=0 -> auto-reload never arms
    }

    [Fact]
    public void ReloadTimeAutoReloadsSupplyAfterDepletion()
    {
        var game = NewGame();
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missileA = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var missileB = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(-100, 0, 50));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);
        Assert.Equal(6u, module.AvailableCountermeasures); // NumberOfVolleys(2) * VolleySize(3)

        module.ReportMissileForCountermeasures(missileA);
        // Reaction (1 frame) then two volleys of 3, DelayBetweenVolleys apart (5 frames): drain
        // the full 6-flare supply well within 15 frames.
        for (var i = 0; i < 15; i++)
        {
            game.Step();
        }

        Assert.Equal(0u, module.AvailableCountermeasures);
        Assert.NotEqual(LogicFrame.Zero, module.ReloadFrame); // reload timer armed once depleted

        // ReloadTime = 2000ms = 10 frames from the frame it armed; keep stepping well past it.
        for (var i = 0; i < 12; i++)
        {
            game.Step();
        }

        Assert.Equal(6u, module.AvailableCountermeasures); // reloaded to full
        Assert.Equal(LogicFrame.Zero, module.ReloadFrame);

        // Confirm the module is actually usable again post-reload.
        module.ReportMissileForCountermeasures(missileB);
        Assert.Equal(2u, module.IncomingMissiles);
    }

    // ------------------------------------------------------------------ calculateCountermeasureToDivertTo

    [Fact]
    public void CalculateCountermeasureToDivertTo_ReturnsClosestActiveFlareWithinVolleyRange()
    {
        var game = NewGame();
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);
        module.ReportMissileForCountermeasures(missile);
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.Equal(3, module.CounterMeasures.Count);

        // The most-recently-launched (last) flare, if resolvable, is what the GPL scan returns
        // (file header note: the found-branch never advances the iterator).
        var lastFlareId = module.CounterMeasures[^1];
        var result = module.CalculateCountermeasureToDivertTo(missile);
        Assert.Equal(lastFlareId, result);
    }

    [Fact]
    public void CalculateCountermeasureToDivertTo_EmptyList_ReturnsInvalid()
    {
        var game = NewGame();
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);

        Assert.Equal(ObjectId.Invalid, module.CalculateCountermeasureToDivertTo(missile));
    }

    // ------------------------------------------------------------------ flare cleanup

    [Fact]
    public void DeadFlare_IsErasedFromListAndActiveCountDecrements()
    {
        var game = NewGame();
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);
        module.ReportMissileForCountermeasures(missile);
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        Assert.Equal(3, module.CounterMeasures.Count);
        Assert.Equal(3u, module.ActiveCountermeasures);

        var flareId = module.CounterMeasures[0];
        var flare = game.GameLogic.GetObjectById(flareId);
        flare.Kill();
        // One Step() reaps the destroyed object (DeleteDestroyed, at the tail of that same
        // Step() call, after this module's own Update() already ran); a second Step() is what
        // actually lets this module's flare-validation loop observe GetObjectById returning
        // null and prune it.
        game.Step();
        game.Step();

        Assert.DoesNotContain(flareId, module.CounterMeasures);
        Assert.Equal(2u, module.ActiveCountermeasures);
    }

    // ------------------------------------------------------------------ upgrade / airborne gating

    [Fact]
    public void UpgradeInactive_SleepsForever_NeverLaunches()
    {
        var game = NewGame();
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var module = ModuleOf(jet);

        Assert.False(module.IsActive);

        module.ReportMissileForCountermeasures(missile);
        // reportMissileForCountermeasures itself has no upgrade gate (GPL-exact); the reaction
        // timer still arms...
        Assert.NotEqual(LogicFrame.Zero, module.ReactionFrame);

        // ...but Update() gates on the upgrade and sleeps forever, so nothing ever launches even
        // after the reaction frame has long since passed.
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        Assert.Empty(module.CounterMeasures);
    }

    [Fact]
    public void NotAirborne_ReactionWindowIsMissedWhileGrounded_NeverLaunches()
    {
        var game = NewGame();
        // Ground-level: HeightAboveTerrain ~0, IsAirborne() false.
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 0));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 0));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);
        module.ReportMissileForCountermeasures(missile);
        Assert.NotEqual(LogicFrame.Zero, module.ReactionFrame); // armed regardless of airborne state

        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        Assert.Empty(module.CounterMeasures); // grounded through the reaction frame: never fires

        // GPL's launch checks are frame-EXACT (`m_reactionFrame == now`), gated behind the
        // isAirborneTarget() branch entirely: once "now" has moved past the armed reaction
        // frame while grounded, taking off afterward does NOT retroactively fire it - this is
        // GPL's own behavior, translated faithfully rather than smoothed into a `<=` check.
        jet.UpdateTransform(new Vector3(0, 0, 50));
        jet.UpdateColliders();
        for (var i = 0; i < 5; i++)
        {
            game.Step();
        }

        Assert.Empty(module.CounterMeasures);
    }

    // ------------------------------------------------------------------ base contract tests

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var live = ModuleOf(jet);

        ActivateUpgrade(game, live);
        live.ReportMissileForCountermeasures(missile);
        for (var i = 0; i < 3; i++)
        {
            game.Step();
        }

        var shadowHost = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(400, 0, 50));
        var shadow = ModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidBehavior_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 2);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    private static List<int> RunScenario(int roundTripAtFrame)
    {
        var game = NewGame(seed: 0xFEED);
        var jet = game.SpawnObject("AlwaysEvadesJet", game.CivilianPlayer, new Vector3(0, 0, 50));
        var missile = game.SpawnObject("Missile", game.PlayerManager.NeutralPlayer, new Vector3(100, 0, 50));
        var module = ModuleOf(jet);

        ActivateUpgrade(game, module);
        module.ReportMissileForCountermeasures(missile);

        var trajectory = new List<int>();
        for (var i = 0; i < 8; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk; // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory.Add(module.CounterMeasures.Count);
        }

        return trajectory;
    }
}
