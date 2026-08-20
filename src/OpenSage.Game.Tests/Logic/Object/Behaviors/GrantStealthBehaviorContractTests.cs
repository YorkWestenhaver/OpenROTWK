// Mocked-game unit tests for the GrantStealthBehavior port (R12, api-freeze-v1 §6 fitness
// item 4): one test per INI-configurable behavior branch, [create -> tick -> observable
// effect], plus the shadow-copy base test and a mid-state save/load round-trip. Object
// definitions are parsed from INI text through the real parser, so the S5 quantizing parse
// of StartRadius/FinalRadius/RadiusGrowRate (Fix64) is on the tested path.

using System.Linq;
using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Behaviors;

public class GrantStealthBehaviorContractTests
{
    // 5 Hz (F6), one scan per real tick (plus one Step() of scheduling warm-up before the
    // module's first tick - see RadiusGrowsLinearly...). StartRadius 0, RadiusGrowRate 20,
    // FinalRadius 40 => first scan radius 20 (mid scan), second scan radius 40 (final scan,
    // destroys the host).
    private const string Definitions = @"
FXParticleSystem PS_Grant
End

Object Granter
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GrantStealthBehavior ModuleTag_Grant
    StartRadius = 0
    FinalRadius = 40
    RadiusGrowRate = 20
    KindOf = INFANTRY
    RadiusParticleSystemName = PS_Grant
  End
End

Object UnfilteredGranter
  KindOf = STRUCTURE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = GrantStealthBehavior ModuleTag_Grant
    StartRadius = 0
    FinalRadius = 40
    RadiusGrowRate = 20
  End
End

Object Sneakable
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = StealthUpdate ModuleTag_Stealth
    StealthDelay = 1000
    GrantedBySpecialPower = Yes
  End
End

Object NonStealthy
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
End

Object Vehicle
  KindOf = VEHICLE
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 50
  End
  Behavior = StealthUpdate ModuleTag_Stealth
    StealthDelay = 1000
    GrantedBySpecialPower = Yes
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0x57EA17) // "STEALTh"
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GrantStealthBehavior GranterModuleOf(GameObject obj) =>
        obj.BehaviorModules.OfType<GrantStealthBehavior>().Single();

    private static bool IsStealthed(GameObject obj) => obj.TestStatus(ObjectStatus.Stealthed);

    [Fact]
    public void RadiusGrowsLinearly_GrantsAlliesAsTheyEnterRange_ThenDestroysHostOnFinalScan()
    {
        var game = NewGame();
        var granter = game.SpawnObject("Granter", game.CivilianPlayer, Vector3.Zero);

        // In range only once the radius reaches 20 (the module's first real scan), still
        // short of 40 (the second, final scan).
        var midRange = game.SpawnObject("Sneakable", game.CivilianPlayer, new Vector3(25, 0, 0));
        // In range from the module's very first scan.
        var nearRange = game.SpawnObject("Sneakable", game.CivilianPlayer, new Vector3(5, 0, 0));

        // Sleepy-module scheduling (GameLogic.Update: modules due strictly at-or-before the
        // PRE-increment frame counter run, then the counter advances) means a module whose
        // ctor calls SetWakeFrame(None) is due on frame 1 and does not run on the Step() that
        // carries the counter from 0 to 1 - only on the next one. This first Step() is that
        // warm-up: nothing has scanned yet.
        game.Step();
        Assert.False(IsStealthed(midRange));
        Assert.False(IsStealthed(nearRange));
        Assert.False(granter.IsDestroyed);

        // First real scan: radius grows 0 -> 20. nearRange (5) is in; midRange (25) is not yet.
        game.Step();
        Assert.True(IsStealthed(nearRange));
        Assert.False(IsStealthed(midRange));
        Assert.False(granter.IsDestroyed);

        // Second (final) scan: radius grows 20 -> 40 (clamped to FinalRadius). midRange (25)
        // is now in range and granted; the host is destroyed afterwards.
        game.Step();
        Assert.True(IsStealthed(midRange));
        Assert.True(granter.IsDestroyed);
    }

    [Fact]
    public void MatchingKindOf_ReceivesGrant_NonMatchingDoesNot()
    {
        var game = NewGame();
        game.SpawnObject("Granter", game.CivilianPlayer, Vector3.Zero); // KindOf = INFANTRY
        var infantry = game.SpawnObject("Sneakable", game.CivilianPlayer, new Vector3(5, 0, 0));
        var vehicle = game.SpawnObject("Vehicle", game.CivilianPlayer, new Vector3(5, 0, 0));

        game.Step(); // warm-up (see RadiusGrowsLinearly... for why)
        game.Step(); // first real scan, radius 20

        Assert.True(IsStealthed(infantry));
        Assert.False(IsStealthed(vehicle));   // KindOf filter excludes VEHICLE
    }

    [Fact]
    public void UnsetKindOf_MatchesEveryKind()
    {
        var game = NewGame();
        game.SpawnObject("UnfilteredGranter", game.CivilianPlayer, Vector3.Zero);
        var vehicle = game.SpawnObject("Vehicle", game.CivilianPlayer, new Vector3(5, 0, 0));

        game.Step(); // warm-up
        game.Step(); // first real scan, radius 20

        Assert.True(IsStealthed(vehicle));    // GPL default: all KindOf bits set
    }

    [Fact]
    public void OnlyAllies_ReceiveTheGrant_NotEnemiesOrNeutrals()
    {
        var game = NewGame();
        var granter = game.SpawnObject("Granter", game.CivilianPlayer, Vector3.Zero);
        var allyPlayer = game.PlayerManager.Players[0];
        var enemyPlayer = game.PlayerManager.Players[1];
        granter.Owner.AddAlly(allyPlayer);

        var ally = game.SpawnObject("Sneakable", allyPlayer, new Vector3(5, 0, 0));
        var enemy = game.SpawnObject("Sneakable", enemyPlayer, new Vector3(5, 0, 0));

        game.Step(); // warm-up
        game.Step(); // first real scan, radius 20

        Assert.True(IsStealthed(ally));
        Assert.False(IsStealthed(enemy));     // neither owned nor allied - no grant
    }

    [Fact]
    public void NoStealthUpdateModule_IsSkippedWithoutError()
    {
        var game = NewGame();
        game.SpawnObject("Granter", game.CivilianPlayer, Vector3.Zero);
        var plain = game.SpawnObject("NonStealthy", game.CivilianPlayer, new Vector3(5, 0, 0));

        // KindOf = INFANTRY matches, but NonStealthy carries no StealthUpdate module; the
        // grant is simply a no-op (FindBehavior<StealthUpdate>() null-check).
        game.Step(); // warm-up
        game.Step(); // first real scan, radius 20

        Assert.False(plain.TestStatus(ObjectStatus.Stealthed));
    }

    [Fact]
    public void RadiusParticleSystem_FiresOnceAtConstruction()
    {
        var game = NewGame();
        var events = RecordingSimEvents.InstallOn(game);

        var granter = game.SpawnObject("Granter", game.CivilianPlayer, Vector3.Zero);

        var ps = Assert.Single(events.ParticleSystems);
        Assert.Equal("PS_Grant", ps.ParticleSystemName);
        Assert.Equal(granter.Id, ps.ObjectId);

        // The request is fire-and-forget at construction; further ticks do not repeat it.
        game.Step();
        game.Step();
        Assert.Single(events.ParticleSystems);
    }

    [Fact]
    public void NoRadiusParticleSystemConfigured_FiresNoEvent()
    {
        var game = NewGame();
        var events = RecordingSimEvents.InstallOn(game);

        game.SpawnObject("UnfilteredGranter", game.CivilianPlayer, Vector3.Zero);

        Assert.Empty(events.ParticleSystems);
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var granter = game.SpawnObject("Granter", game.CivilianPlayer, Vector3.Zero);
        var live = GranterModuleOf(granter);

        game.Step(); // warm-up
        game.Step(); // radius 0 -> 20, not yet final

        // The shadow is the same class over the same data on a second object in a different
        // (fresh) state; Load must overwrite everything the walk carries.
        var shadowHost = game.SpawnObject("Granter", game.CivilianPlayer, new Vector3(200, 0, 0));
        var shadow = GranterModuleOf(shadowHost);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
    }

    [Fact]
    public void MidState_SaveLoadRoundTrip_ContinuesIdentically()
    {
        var trajectoryA = RunScenario(roundTripAtFrame: -1);
        var trajectoryB = RunScenario(roundTripAtFrame: 2);
        Assert.Equal(trajectoryA, trajectoryB);
    }

    // Trajectory over 3 Step()s: [warm-up: nothing yet, first scan (radius 20): target
    // (distance 25) still out of range, final scan (radius 40): target granted and the host
    // destroyed] = [false, false, true].
    private static bool[] RunScenario(int roundTripAtFrame)
    {
        var game = NewGame();
        var granter = game.SpawnObject("Granter", game.CivilianPlayer, Vector3.Zero);
        var module = GranterModuleOf(granter);
        var target = game.SpawnObject("Sneakable", game.CivilianPlayer, new Vector3(25, 0, 0));

        var trajectory = new bool[3];
        for (var i = 0; i < trajectory.Length; i++)
        {
            if (i == roundTripAtFrame)
            {
                var state = PortedModuleTestKit.Save(module);
                var wake = module.NextWakeFrameForWalk; // engine-owned, walk-carried (S6)
                PortedModuleTestKit.Load(module, state);
                module.NextWakeFrameForWalk = wake;
            }

            game.Step();
            trajectory[i] = IsStealthed(target);
        }

        return trajectory;
    }
}
