// Contract tests for the InactiveBody port (experiment-round-4 §4.1 DoD item 4: one test per
// behavioral branch, plus the shadow-copy base test and a mid-behavior save/load
// continuation). Shape cloned from AutoHealContractTests / EjectPilotDieContractTests.
//
// InactiveBody has no health, so the Die-batch's PortedModuleTestKit.TriggerDeath (which
// asserts health crossed >0 -> <=0) does not apply: damage is driven through AttemptDamage
// directly. The observable effect of the one lethal branch (UNRESISTABLE) is that DieModules
// run - here a CreateObjectDie that spawns a marker object, so "did the module fire OnDie" is
// "did a marker appear in the object list".

using System;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Tests.Logic.Object;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class InactiveBodyContractTests
{
    private const string Marker = "InactiveDieMarker";

    private const string Definitions = @"
Object " + Marker + @"
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1
  End
End

ObjectCreationList OCL_InactiveMarker
  CreateObject
    ObjectNames = " + Marker + @"
    Count = 1
  End
End

; the subject: an inactive body with a Die module whose effect is observable headlessly
Object InactiveTestObject
  KindOf = STRUCTURE IMMOBILE
  Body = InactiveBody ModuleTag_Body
  End
  Behavior = CreateObjectDie ModuleTag_Die
    CreationList = OCL_InactiveMarker
  End
End

; same, but flagged as a prerequisite - the UNRESISTABLE branch asserts against this
Object InactivePrerequisiteObject
  KindOf = STRUCTURE IMMOBILE
  IsPrerequisite = Yes
  Body = InactiveBody ModuleTag_Body
  End
  Behavior = CreateObjectDie ModuleTag_Die
    CreationList = OCL_InactiveMarker
  End
End

Object InactiveTestKiller
  KindOf = INFANTRY
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static readonly Vector3 Origin = new(0, 0, 0);

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0x1AC71Eu);
        game.LoadIniText(Definitions);
        return game;
    }

    private static int MarkerCount(HeadlessSimGame game) =>
        game.GameLogic.Objects.Count(o => o.Definition.Name == Marker);

    private static InactiveBody BodyOf(GameObject gameObject) =>
        Assert.IsType<InactiveBody>(gameObject.BodyModule);

    private static DamageInfoOutput Hit(
        GameObject target,
        DamageType damageType,
        float amount = 10f,
        GameObject source = null) =>
        target.AttemptDamage(new DamageInfoInput(source)
        {
            DamageType = damageType,
            DeathType = DeathType.Normal,
            Amount = amount,
        });

    // ---- branch: bodiless invariants (health/state are pinned constants) ----

    [Fact]
    public void HealthIsAlwaysZeroAndStateAlwaysPristine()
    {
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);
        var body = BodyOf(obj);

        Assert.Equal(0.0f, body.Health);
        Assert.Equal(BodyDamageType.Pristine, body.DamageState);

        // Setting the damage state is a no-op (GPL setDamageState is empty).
        body.DamageState = BodyDamageType.Rubble;
        Assert.Equal(BodyDamageType.Pristine, body.DamageState);

        // InternalChangeHealth cannot move a health that does not exist.
        body.InternalChangeHealth(-999f);
        body.InternalChangeHealth(999f);
        Assert.Equal(0.0f, body.Health);
    }

    [Fact]
    public void ConstructedEffectivelyDead()
    {
        // GPL ctor: setEffectivelyDead(true). The object is dead-on-arrival.
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);
        Assert.True(obj.IsEffectivelyDead);
    }

    // ---- branch: ordinary damage is a pure no-op ----

    [Fact]
    public void NonUnresistableDamage_HasNoEffectAndRunsNoDieModules()
    {
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);

        var output = Hit(obj, DamageType.Explosion, amount: 50f);

        Assert.True(output.NoEffect);
        Assert.Equal(0.0f, output.ActualDamageDealt);
        Assert.Equal(0.0f, output.ActualDamageClipped);
        Assert.Equal(0, MarkerCount(game)); // DieModules did NOT run
    }

    // ---- branch: healing is a pure no-op (and the healing<->damage redirects are safe) ----

    [Fact]
    public void Healing_HasNoEffect()
    {
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);

        var output = Hit(obj, DamageType.Healing, amount: 50f);

        Assert.True(output.NoEffect);
        Assert.Equal(0.0f, output.ActualDamageDealt);
        Assert.Equal(0, MarkerCount(game));
    }

    [Fact]
    public void AttemptHealingWithNonHealingType_RedirectsToDamage()
    {
        // GPL attemptHealing: a non-HEALING type redirects to attemptDamage. An UNRESISTABLE
        // routed through AttemptHealing must therefore still fire the die once.
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);

        var output = obj.BodyModule.AttemptHealing(new DamageInfoInput
        {
            DamageType = DamageType.Unresistable,
            DeathType = DeathType.Normal,
            Amount = 10f,
        });

        Assert.False(output.NoEffect);
        Assert.Equal(1, MarkerCount(game));
    }

    // ---- branch: UNRESISTABLE wipes us out (the one lethal path) ----

    [Fact]
    public void UnresistableDamage_ClearsNoEffectAndRunsDieModules()
    {
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);

        var output = Hit(obj, DamageType.Unresistable);

        Assert.False(output.NoEffect);
        // Still no health accounting - dealt/clipped stay zero (GPL leaves them 0).
        Assert.Equal(0.0f, output.ActualDamageDealt);
        Assert.Equal(0.0f, output.ActualDamageClipped);
        Assert.Equal(1, MarkerCount(game)); // DieModules ran exactly once
    }

    [Fact]
    public void UnresistableDamage_ResolvesTheDamageDealer()
    {
        var game = NewGame();
        var killer = game.SpawnObject("InactiveTestKiller", game.CivilianPlayer, new Vector3(30, 0, 0));
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);

        var output = Hit(obj, DamageType.Unresistable, source: killer);

        Assert.False(output.NoEffect);
        Assert.Equal(1, MarkerCount(game));
    }

    // ---- branch: the m_dieCalled latch - DieModules fire at most once ----

    [Fact]
    public void RepeatedUnresistableDamage_FiresDieModulesOnlyOnce()
    {
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);

        var first = Hit(obj, DamageType.Unresistable);
        var second = Hit(obj, DamageType.Unresistable);

        // Both report the wipe-out (noEffect cleared) ...
        Assert.False(first.NoEffect);
        Assert.False(second.NoEffect);

        // ... but the die dispatch is latched: exactly one marker was created.
        Assert.Equal(1, MarkerCount(game));
    }

    // ---- branch: EstimateDamage (0 except UNRESISTABLE returns the raw request) ----

    [Fact]
    public void EstimateDamage_ZeroExceptUnresistable()
    {
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);
        var body = BodyOf(obj);

        Assert.Equal(0.0f, body.EstimateDamage(new DamageInfoInput
        {
            DamageType = DamageType.Explosion,
            Amount = 42f,
        }));

        Assert.Equal(42f, body.EstimateDamage(new DamageInfoInput
        {
            DamageType = DamageType.Unresistable,
            Amount = 42f,
        }));
    }

    // ---- finding F-1: DebugUtility.AssertCrash throws in ALL builds (GPL's DEBUG_ASSERTCRASH
    // is debug-only). A prerequisite carrying an InactiveBody therefore hard-faults on the
    // UNRESISTABLE branch instead of silently continuing. Documented, not silently changed. ----

    [Fact]
    public void PrerequisiteWithInactiveBody_AssertsOnUnresistable()
    {
        var game = NewGame();
        var obj = game.SpawnObject("InactivePrerequisiteObject", game.CivilianPlayer, Origin);

        Assert.Throws<Exception>(() => Hit(obj, DamageType.Unresistable));
    }

    // ---- item 3: the shadow-copy base test, taken MID-BEHAVIOR (after the die latched) ----

    [Fact]
    public void ShadowCopyCrcMatches_MidBehavior()
    {
        var game = NewGame();
        var live = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);
        var shadow = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, new Vector3(40, 0, 0));

        // Move the live module into its post-death state (_dieCalled = true) and give it a
        // non-default damage scalar so the base-walked Fix64 field is exercised too.
        Hit(live, DamageType.Unresistable);
        live.BodyModule.ApplyDamageScalar(0.5f);

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    [Fact]
    public void XferIsDeclaredToTheWalk()
    {
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);
        var body = BodyOf(obj);

        Assert.True(body.HasSimXfer);
        // The walk is stable across repeated visits.
        Assert.Equal(PortedModuleTestKit.LiveCrc(body), PortedModuleTestKit.LiveCrc(body));
    }

    [Fact]
    public void DieCalledLatchIsInTheWalk()
    {
        // The CRC must differ before and after the latch flips - proof the field is walked
        // (an omitted _dieCalled would leave the two CRCs equal).
        var game = NewGame();
        var obj = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);
        var body = BodyOf(obj);

        var before = PortedModuleTestKit.LiveCrc(body);
        Hit(obj, DamageType.Unresistable);
        var after = PortedModuleTestKit.LiveCrc(body);

        Assert.NotEqual(before, after);
    }

    // ---- item 4: mid-behavior save/load continuation - the persistence decision (D-1) ----

    [Fact]
    public void SaveLoadMidBehavior_PersistsTheDieCalledLatch()
    {
        // Object A dies (latch set), and its state is saved. Loading that state into a fresh
        // object B must carry the latch, so B refuses to re-fire its DieModules - whereas an
        // un-loaded control object C fires normally. This is exactly the continuation the GPL
        // xfer would lose (it omits m_dieCalled); our walk preserves it.
        var game = NewGame();

        var a = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);
        Hit(a, DamageType.Unresistable);
        Assert.Equal(1, MarkerCount(game)); // A's die fired
        var savedLatched = PortedModuleTestKit.Save(BodyOf(a));

        // B loads A's latched state, then takes an UNRESISTABLE hit: no new marker.
        var b = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, new Vector3(20, 0, 0));
        PortedModuleTestKit.Load(BodyOf(b), savedLatched);
        Hit(b, DamageType.Unresistable);
        Assert.Equal(1, MarkerCount(game)); // unchanged: B's latch was loaded

        // C is the control: no load, so its die fires and the marker count advances.
        var c = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, new Vector3(40, 0, 0));
        Hit(c, DamageType.Unresistable);
        Assert.Equal(2, MarkerCount(game));

        // And the loaded B now CRC-matches a live module that reached the latch by playing.
        Assert.Equal(PortedModuleTestKit.LiveCrc(BodyOf(c)), PortedModuleTestKit.LiveCrc(BodyOf(b)));
    }

    [Fact]
    public void SaveLoad_RoundTripsDamageScalar()
    {
        // The base BodyModule Fix64 damage scalar rides the same walk (XferBodyBase). A
        // non-default value must survive save -> load byte-identically.
        var game = NewGame();
        var live = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, Origin);
        var shadow = game.SpawnObject("InactiveTestObject", game.CivilianPlayer, new Vector3(40, 0, 0));

        live.BodyModule.ApplyDamageScalar(0.25f);
        PortedModuleTestKit.Load(BodyOf(shadow), PortedModuleTestKit.Save(BodyOf(live)));

        Assert.Equal(live.BodyModule.DamageScalar, shadow.BodyModule.DamageScalar, precision: 5);
        Assert.Equal(PortedModuleTestKit.LiveCrc(BodyOf(live)), PortedModuleTestKit.LiveCrc(BodyOf(shadow)));
    }
}
