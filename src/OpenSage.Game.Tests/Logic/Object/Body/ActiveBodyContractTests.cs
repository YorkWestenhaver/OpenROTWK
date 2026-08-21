// R7 ActiveBody contract tests: the Body ModuleData audit (Fix64 health/subdual) and the
// completed Objects-CRC fold (armor-set condition flags now ride the contract Xfer). Exercised
// on HeadlessSimGame with real parsed INI so the ParseFix64 quantizing path is on the tested
// path. Complements the S1 WeaponDamageArmorSystemTests (which own the armor->health chain,
// shadow-copy CRC, and the plain save/load continuation).

using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Body;

public class ActiveBodyContractTests
{
    // ArmoredHero carries a Veteran-conditioned armor set so flipping the veterancy armor
    // flag actually changes the resolved armor coefficient - the flag is real sim state.
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
  HealthBonus_Veteran = 150%
End

Armor BaseArmor
  Armor = DEFAULT 100%
End

Armor VeteranArmor
  Armor = DEFAULT 50%
End

Object ArmoredHero
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = BaseArmor
  End
  ArmorSet
    Conditions = VETERAN
    Armor = VeteranArmor
  End
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 200
    InitialHealth = 200
    SubdualDamageCap = 80
  End
End

Object Fractional
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 37.5
  End
End

; no Contain module - exercises ActiveBody's non-RiderChangeContain KillPilot branch
; (GPL's ""else"" arm: unmanned + neutral team).
Object KillPilotVehicle
  KindOf = VEHICLE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xB0D_1E5u)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(0, 0, 0));

    private static ActiveBody BodyOf(GameObject gameObject)
        => Assert.IsType<ActiveBody>(gameObject.BodyModule, exactMatch: false);

    private static CombatDamageInput Damage(int amount, DamageType type = DamageType.Slash)
        => new() { DamageType = type, Amount = new Fix64(amount) };

    // ---- item 1: the audited ModuleData quantizes at parse ----

    [Fact]
    public void MaxHealth_ParsedAsFix64_IntegerLiteralIsExact()
    {
        var game = NewGame();
        var body = BodyOf(Spawn(game, "ArmoredHero"));

        Assert.Equal(new Fix64(200), body.DamageCore.MaxHealth);
        Assert.Equal(new Fix64(200), body.DamageCore.InitialHealth);
        Assert.Equal(new Fix64(200), body.DamageCore.CurrentHealth);
    }

    [Fact]
    public void MaxHealth_ParsedAsFix64_FractionalLiteralIsExactHalf()
    {
        var game = NewGame();
        var body = BodyOf(Spawn(game, "Fractional"));

        // 37.5 = 37*2^32 + 2^31, the exact Q31.32 representation (no float round-trip).
        Assert.Equal(Fix64.FromRaw((37L << 32) + (1L << 31)), body.DamageCore.MaxHealth);
    }

    // ---- item 4: subdual branch drives off the audited Fix64 cap ----

    [Fact]
    public void SubdualDamage_AccumulatesToCap_AndSubdues()
    {
        var game = NewGame();
        var hero = Spawn(game, "ArmoredHero");
        var body = BodyOf(hero);

        // Cap is 80; two 50-point subdual hits clamp at 80 (the audited Fix64 cap).
        hero.AttemptCombatDamage(Damage(50, DamageType.SubdualMissile));
        Assert.True(body.HasAnySubdualDamage);
        hero.AttemptCombatDamage(Damage(50, DamageType.SubdualMissile));

        Assert.Equal(new Fix64(80), body.DamageCore.CurrentSubdualDamage);
    }

    // ---- item 3/4: the completed CRC fold - armor-set flags round-trip the contract Xfer ----

    [Fact]
    public void ArmorSetFlags_RoundTripThroughContractXfer()
    {
        var game = NewGame();
        var live = BodyOf(Spawn(game, "ArmoredHero"));
        var shadow = BodyOf(Spawn(game, "ArmoredHero"));

        live.SetArmorSetFlag(ArmorSetCondition.Veteran);
        Assert.True(live.TestArmorSetFlag(ArmorSetCondition.Veteran));
        Assert.False(shadow.TestArmorSetFlag(ArmorSetCondition.Veteran));

        // Save/load must carry the flag AND keep the CRC equal (the flag is in the walk now).
        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(live, shadow);
        Assert.True(shadow.TestArmorSetFlag(ArmorSetCondition.Veteran));
    }

    [Fact]
    public void ArmorSetFlags_SelectDifferentArmor_SurviveSaveLoad_Continuation()
    {
        var game = NewGame();
        var live = Spawn(game, "ArmoredHero");
        var liveBody = BodyOf(live);

        // Promote to veteran: the VETERAN armor set (50% DEFAULT) is now active.
        liveBody.SetArmorSetFlag(ArmorSetCondition.Veteran);

        // Save mid-behavior, load into a fresh (base-armor) host.
        var state = PortedModuleTestKit.Save(liveBody);
        var restored = Spawn(game, "ArmoredHero");
        PortedModuleTestKit.Load(BodyOf(restored), state);

        // The restored body must resolve the veteran armor too: identical 40 -> 20 damage.
        live.AttemptCombatDamage(Damage(40, DamageType.Slash));
        restored.AttemptCombatDamage(Damage(40, DamageType.Slash));

        Assert.Equal(new Fix64(20), liveBody.DamageCore.MaxHealth - liveBody.DamageCore.CurrentHealth);
        Assert.Equal(
            liveBody.DamageCore.CurrentHealth,
            BodyOf(restored).DamageCore.CurrentHealth);
        Assert.True(BodyOf(restored).TestArmorSetFlag(ArmorSetCondition.Veteran));
    }

    // ---- subdual recovery: the heal side of internalAddSubdualDamage, driven the same way
    // GPL's SubdualDamageHelper drives it - a negative-amount subdual hit through the same
    // AttemptCombatDamage entry point (SubdualDamageHelper itself is a separate, unported
    // module; this exercises the recovery arithmetic ActiveBody already owns). ----

    [Fact]
    public void SubdualDamage_HealTickBelowCap_ReEnablesTheUnit()
    {
        var game = NewGame();
        var hero = Spawn(game, "ArmoredHero");
        var body = BodyOf(hero);

        // Cap is 80: push past it so the unit is subdued and disabled.
        hero.AttemptCombatDamage(Damage(90, DamageType.SubdualMissile));
        Assert.True(body.DamageCore.IsSubdued);
        Assert.True(hero.IsDisabledByType(DisabledType.Subdued));

        // A heal tick removes subdual damage (GPL: attemptDamage with a negative amount of
        // the same subdual type). Enough to drop back under the cap.
        hero.AttemptCombatDamage(Damage(-30, DamageType.SubdualMissile));

        Assert.False(body.DamageCore.IsSubdued);
        Assert.False(hero.IsDisabledByType(DisabledType.Subdued));
    }

    // ---- veterancy bonus: OnVeterancyLevelChanged scales MaxHealth by GameData.HealthBonus
    // and PreserveRatio carries CurrentHealth along proportionally (BodyDamageCore.SetMaxHealth). ----

    [Fact]
    public void VeterancyPromotion_ScalesMaxHealthAndCurrentHealthProportionally()
    {
        var game = NewGame();
        var hero = Spawn(game, "ArmoredHero");
        var body = BodyOf(hero);

        // Damage first so CurrentHealth < MaxHealth, to prove the ratio (not just the max)
        // carries through the promotion.
        hero.AttemptCombatDamage(Damage(100, DamageType.Unresistable));
        Assert.Equal(100.0f, body.Health);

        // provideFeedback:false - the headless host has no AudioSystem, matching the promotion
        // sound's null-unsafe call (recorded the same way EjectPilotDieContractTests does).
        hero.ExperienceTracker.SetVeterancyLevel(VeterancyLevel.Veteran, provideFeedback: false);

        // Regular's HealthBonus defaults to 1.0; Veteran is set to 150% above, so
        // OnVeterancyLevelChanged's mult = 1.5 is exactly the 50% max-health bonus.
        Assert.Equal(300.0f, body.MaxHealth);
        // PreserveRatio: 100/200 = 50% carries onto the new max (150/300).
        Assert.Equal(150.0f, body.Health);
    }

    // ---- KillPilot: the non-RiderChangeContain branch (no Contain module ported for this
    // test's vehicle) - the unit is made Unmanned and reassigned to the neutral team without
    // being destroyed. The RiderChangeContain bike branch is not exercised here: that Contain
    // implementation has not been ported yet (out of this packet's scope). ----

    [Fact]
    public void KillPilot_OnUnmannedVehicle_DisablesAndNeutralizesWithoutDestroying()
    {
        var game = NewGame();
        var vehicle = game.SpawnObject("KillPilotVehicle", game.CivilianPlayer, new Vector3(0, 0, 0));

        Assert.False(vehicle.IsEffectivelyDead);

        vehicle.AttemptCombatDamage(new CombatDamageInput
        {
            DamageType = DamageType.KillPilot,
            Amount = Fix64.Zero,
        });

        Assert.True(vehicle.IsDisabledByType(DisabledType.Unmanned));
        Assert.Equal(game.PlayerManager.NeutralPlayer.DefaultTeam, vehicle.Team);
        Assert.False(vehicle.IsEffectivelyDead);
    }
}
