// S1 system tests (build-roadmap Tier-1 weapon-damage-armor): the firing -> damage ->
// armor -> health chain, exercised on HeadlessSimGame with real parsed INI so the
// quantizing parse path (ParseFix64 / ParseFix64Percentage / RangeDuration) is on the
// tested path. One test per core formula/branch, plus Xfer round-trips (SimWeapon via
// the SimCore visitors, ActiveBody via the shadow-copy base test) and a mid-state
// save/load continuation.

using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Combat;

public class WeaponDamageArmorSystemTests
{
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Armor TestArmor
  Armor = DEFAULT 100%
  Armor = SLASH 50%
  Armor = PIERCE 25%
  Armor = FLAME 200%
  Armor = CRUSH 0%
End

Weapon TestGun
  ClipSize = 2
  AutoReloadsClip = Yes
  DelayBetweenShots = 600
  ClipReloadTime = 1000
End

Weapon TestGunNoReload
  ClipSize = 1
  AutoReloadsClip = No
  DelayBetweenShots = 600
  ClipReloadTime = 1000
End

Weapon TestGunRangedDelay
  ClipSize = 0
  AutoReloadsClip = Yes
  DelayBetweenShots = Min:200 Max:1000
  ClipReloadTime = 0
End

Weapon TestNuggetGun
  AttackRange = 100
  DamageNugget
    Damage = 37.5
    Radius = 0.0
    DamageType = SLASH
    DeathType = NORMAL
  End
End

Object PlainVictim
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End

Object ArmoredVictim
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  ArmorSet
    Conditions = None
    Armor = TestArmor
  End
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition, float x = 0, float y = 0)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(x, y, 0));

    private static ActiveBody BodyOf(GameObject gameObject)
        => Assert.IsType<ActiveBody>(gameObject.BodyModule, exactMatch: false);

    private static Fix64 Fix(int value) => new(value);

    private static CombatDamageInput Damage(
        int amount,
        DamageType type = DamageType.Slash,
        GameObject source = null,
        bool kill = false)
        => new()
        {
            SourceId = source?.Id ?? ObjectId.Invalid,
            DamageType = type,
            Amount = Fix(amount),
            Kill = kill,
        };

    // ================================================================
    // Armor: the damage-type multiplier table (GPL Armor.cpp)
    // ================================================================

    [Fact]
    public void Armor_CoefficientsQuantizeExactly()
    {
        var game = NewGame();
        var armor = game.AssetStore.ArmorTemplates.GetByName("TestArmor");

        Assert.Equal(Fix64.Half, armor.Values[(int)DamageType.Slash]);
        Assert.Equal(Fix64.One, armor.Values[(int)DamageType.Magic]);   // DEFAULT fill
        Assert.Equal(Fix64.Two, armor.Values[(int)DamageType.Flame]);
        Assert.Equal(Fix64.Zero, armor.Values[(int)DamageType.Crush]);
    }

    [Fact]
    public void Armor_AdjustsBySlashCoefficient()
    {
        var game = NewGame();
        var victim = Spawn(game, "ArmoredVictim");

        var output = victim.AttemptCombatDamage(Damage(40, DamageType.Slash));

        Assert.Equal(Fix(20), output.ActualDamageDealt);
        Assert.Equal(Fix(80), BodyOf(victim).DamageCore.CurrentHealth);
    }

    [Fact]
    public void Armor_ZeroCoefficientBlocksAllDamage()
    {
        var game = NewGame();
        var victim = Spawn(game, "ArmoredVictim");

        var output = victim.AttemptCombatDamage(Damage(500, DamageType.Crush));

        Assert.Equal(Fix64.Zero, output.ActualDamageDealt);
        Assert.Equal(Fix(100), BodyOf(victim).DamageCore.CurrentHealth);
    }

    [Fact]
    public void Armor_UnresistableBypassesArmor()
    {
        var game = NewGame();
        var victim = Spawn(game, "ArmoredVictim");

        // Crush is 0% - but UNRESISTABLE ignores the table entirely (GPL adjustDamage).
        var output = victim.AttemptCombatDamage(Damage(30, DamageType.Unresistable));

        Assert.Equal(Fix(30), output.ActualDamageDealt);
        Assert.Equal(Fix(70), BodyOf(victim).DamageCore.CurrentHealth);
    }

    // ================================================================
    // Damage scalar + clipping + kill + healing (GPL ActiveBody)
    // ================================================================

    [Fact]
    public void DamageScalar_MultipliesAfterArmor()
    {
        var game = NewGame();
        var victim = Spawn(game, "ArmoredVictim");
        BodyOf(victim).ApplyDamageScalar(Fix64.Half);

        // 40 slash -> 20 after armor -> 10 after scalar.
        var output = victim.AttemptCombatDamage(Damage(40, DamageType.Slash));

        Assert.Equal(Fix(10), output.ActualDamageDealt);
        Assert.Equal(Fix(90), BodyOf(victim).DamageCore.CurrentHealth);
    }

    [Fact]
    public void DamageScalar_DoesNotTouchUnresistable()
    {
        var game = NewGame();
        var victim = Spawn(game, "PlainVictim");
        BodyOf(victim).ApplyDamageScalar(Fix64.Half);

        var output = victim.AttemptCombatDamage(Damage(30, DamageType.Unresistable));

        Assert.Equal(Fix(30), output.ActualDamageDealt);
    }

    [Fact]
    public void Overkill_ClipsToRemainingHealth_AndKills()
    {
        var game = NewGame();
        var victim = Spawn(game, "PlainVictim");

        var output = victim.AttemptCombatDamage(Damage(250, DamageType.Unresistable));

        Assert.Equal(Fix(250), output.ActualDamageDealt);
        Assert.Equal(Fix(100), output.ActualDamageClipped);
        Assert.True(victim.IsEffectivelyDead);
    }

    [Fact]
    public void KillFlag_KillsThroughAnyArmor()
    {
        var game = NewGame();
        var victim = Spawn(game, "ArmoredVictim");

        // Crush is 0%-resisted, but Kill replaces the amount with remaining health.
        var output = victim.AttemptCombatDamage(Damage(0, DamageType.Crush, kill: true));

        Assert.Equal(Fix(100), output.ActualDamageDealt);
        Assert.True(victim.IsEffectivelyDead);
    }

    [Fact]
    public void Healing_AddsHealthAndClampsAtMax()
    {
        var game = NewGame();
        var victim = Spawn(game, "PlainVictim");
        victim.AttemptCombatDamage(Damage(60, DamageType.Unresistable));

        victim.AttemptHealing(Fix(30), victim);
        Assert.Equal(Fix(70), BodyOf(victim).DamageCore.CurrentHealth);

        var second = victim.AttemptHealing(Fix(500), victim);
        Assert.Equal(Fix(100), BodyOf(victim).DamageCore.CurrentHealth);
        // GPL clipped = prevHealth - currentHealth: NEGATIVE of the health restored.
        Assert.Equal(-30.0f, second.ActualDamageClipped); // legacy float view of Fix64 result
    }

    [Fact]
    public void DamageStates_FollowThresholds()
    {
        var game = NewGame();
        var victim = Spawn(game, "PlainVictim");
        var body = BodyOf(victim);

        Assert.Equal(BodyDamageType.Pristine, body.DamageState);

        // Thresholds from the GameData block: Damaged at 50%, ReallyDamaged at 10%
        // (division-free predicate: health > max * threshold).
        victim.AttemptCombatDamage(Damage(40, DamageType.Unresistable));
        Assert.Equal(BodyDamageType.Pristine, body.DamageState);        // 60% > 50%

        victim.AttemptCombatDamage(Damage(20, DamageType.Unresistable));
        Assert.Equal(BodyDamageType.Damaged, body.DamageState);         // 40%

        victim.AttemptCombatDamage(Damage(35, DamageType.Unresistable));
        Assert.Equal(BodyDamageType.ReallyDamaged, body.DamageState);   // 5%

        victim.AttemptCombatDamage(Damage(5, DamageType.Unresistable));
        Assert.Equal(BodyDamageType.Rubble, body.DamageState);          // 0
    }

    [Fact]
    public void NegativeAmount_DealsNothing()
    {
        var game = NewGame();
        var victim = Spawn(game, "PlainVictim");

        var output = victim.AttemptCombatDamage(Damage(-50, DamageType.Unresistable));

        Assert.Equal(Fix64.Zero, output.ActualDamageDealt);
        Assert.Equal(Fix(100), BodyOf(victim).DamageCore.CurrentHealth);
    }

    // ================================================================
    // The nugget chain: parse-time quantization of weapon damage
    // ================================================================

    [Fact]
    public void DamageNugget_QuantizesExactlyAtParse()
    {
        var game = NewGame();
        var weapon = game.AssetStore.WeaponTemplates.GetByName("TestNuggetGun");

        DamageNugget nugget = null;
        foreach (var candidate in weapon.Nuggets)
        {
            nugget = candidate as DamageNugget;
            if (nugget != null)
            {
                break;
            }
        }

        Assert.NotNull(nugget);
        // 37.5 is exact in Q31.32: raw = 37 * 2^32 + 2^31.
        Assert.Equal(Fix64.FromRaw((37L << 32) + (1L << 31)), nugget.Damage);
        Assert.Equal(DamageType.Slash, nugget.DamageType);
    }

    // ================================================================
    // SimWeapon: fire timing in logic frames (GPL Weapon.cpp)
    // ================================================================

    [Fact]
    public void SimWeapon_StartsEmpty_FirstReloadFree()
    {
        var game = NewGame();
        var weapon = new SimWeapon(game.AssetStore.WeaponTemplates.GetByName("TestGun"));
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var now = new LogicFrame(10);

        Assert.Equal(SimWeaponStatus.OutOfAmmo, weapon.GetStatus(now));

        weapon.Reload(now, random, Fix64.One, loadInstantly: true);
        Assert.Equal(2, weapon.AmmoInClip);
        Assert.Equal(SimWeaponStatus.ReadyToFire, weapon.GetStatus(now));
    }

    [Fact]
    public void SimWeapon_DelayBetweenShots_InFrames()
    {
        var game = NewGame();
        var weapon = new SimWeapon(game.AssetStore.WeaponTemplates.GetByName("TestGun"));
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var now = new LogicFrame(10);
        weapon.Reload(now, random, Fix64.One, loadInstantly: true);

        var reloaded = weapon.FireShot(now, random, Fix64.One);

        Assert.False(reloaded);
        Assert.Equal(1, weapon.AmmoInClip);
        // 600 ms at 5 Hz = 3 frames (ceil at parse).
        Assert.Equal(new LogicFrame(13), weapon.WhenWeCanFireAgain);
        Assert.Equal(SimWeaponStatus.BetweenFiringShots, weapon.GetStatus(new LogicFrame(12)));
        Assert.Equal(SimWeaponStatus.ReadyToFire, weapon.GetStatus(new LogicFrame(13)));
    }

    [Fact]
    public void SimWeapon_EmptyClip_AutoReloads()
    {
        var game = NewGame();
        var weapon = new SimWeapon(game.AssetStore.WeaponTemplates.GetByName("TestGun"));
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var now = new LogicFrame(10);
        weapon.Reload(now, random, Fix64.One, loadInstantly: true);
        weapon.FireShot(now, random, Fix64.One);

        var reloaded = weapon.FireShot(new LogicFrame(13), random, Fix64.One);

        Assert.True(reloaded);
        Assert.Equal(2, weapon.AmmoInClip);  // clip refilled, held by the reload delay
        // 1000 ms at 5 Hz = 5 frames from the fire frame.
        Assert.Equal(new LogicFrame(18), weapon.WhenWeCanFireAgain);
        Assert.Equal(SimWeaponStatus.ReloadingClip, weapon.GetStatus(new LogicFrame(17)));
        Assert.Equal(SimWeaponStatus.ReadyToFire, weapon.GetStatus(new LogicFrame(18)));
    }

    [Fact]
    public void SimWeapon_NoAutoReload_StaysOutOfAmmoForever()
    {
        var game = NewGame();
        var weapon = new SimWeapon(game.AssetStore.WeaponTemplates.GetByName("TestGunNoReload"));
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var now = new LogicFrame(10);
        weapon.Reload(now, random, Fix64.One, loadInstantly: true);

        weapon.FireShot(now, random, Fix64.One);

        Assert.Equal(0, weapon.AmmoInClip);
        Assert.Equal(SimWeaponStatus.OutOfAmmo, weapon.GetStatus(new LogicFrame(100000)));
    }

    [Fact]
    public void SimWeapon_RateOfFireBonus_DividesAndFloors()
    {
        var game = NewGame();
        var weapon = new SimWeapon(game.AssetStore.WeaponTemplates.GetByName("TestGun"));
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var now = new LogicFrame(10);
        weapon.Reload(now, random, Fix64.One, loadInstantly: true);

        // 3 frames / 2.0 = 1.5 -> floor -> 1 frame (GPL REAL_TO_INT_FLOOR).
        weapon.FireShot(now, random, Fix64.Two);

        Assert.Equal(new LogicFrame(11), weapon.WhenWeCanFireAgain);
    }

    [Fact]
    public void SimWeapon_FixedDelay_DrawsNoRandomness()
    {
        var game = NewGame();
        var weapon = new SimWeapon(game.AssetStore.WeaponTemplates.GetByName("TestGun"));
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var now = new LogicFrame(10);
        weapon.Reload(now, random, Fix64.One, loadInstantly: true);

        var drawsBefore = random.DrawCount;
        weapon.FireShot(now, random, Fix64.One);

        // min == max: the GPL guard skips the RNG entirely (draw-count conformance).
        Assert.Equal(drawsBefore, random.DrawCount);
    }

    [Fact]
    public void SimWeapon_RangedDelay_DrawsOncePerShot_AndIsSeedDeterministic()
    {
        LogicFrame FireOnce(HeadlessSimGame game)
        {
            var weapon = new SimWeapon(game.AssetStore.WeaponTemplates.GetByName("TestGunRangedDelay"));
            var random = game.GameEngine.SimContext.GameLogicRandom;
            var now = new LogicFrame(10);
            weapon.Reload(now, random, Fix64.One, loadInstantly: true);

            var drawsBefore = random.DrawCount;
            weapon.FireShot(now, random, Fix64.One);
            Assert.Equal(drawsBefore + 1, random.DrawCount);
            return weapon.WhenWeCanFireAgain;
        }

        var a = FireOnce(NewGame(seed: 0xAAAA));
        var b = FireOnce(NewGame(seed: 0xAAAA));
        var frames = a.Value - 10;

        Assert.Equal(a, b);                       // same seed, same draw, same schedule
        Assert.InRange(frames, 1u, 5u);           // Min:200 Max:1000 ms = 1..5 frames
    }

    [Fact]
    public void SimWeapon_PreAttack_HoldsUntilWindowEnds()
    {
        var game = NewGame();
        var weapon = new SimWeapon(game.AssetStore.WeaponTemplates.GetByName("TestGun"));
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var now = new LogicFrame(10);
        weapon.Reload(now, random, Fix64.One, loadInstantly: true);

        weapon.StartPreAttack(now, new LogicFrameSpan(4));

        Assert.Equal(SimWeaponStatus.PreAttack, weapon.GetStatus(new LogicFrame(13)));
        Assert.Equal(SimWeaponStatus.ReadyToFire, weapon.GetStatus(new LogicFrame(14)));
    }

    [Fact]
    public void SimWeapon_PercentReady_IsExactRatio()
    {
        var game = NewGame();
        var weapon = new SimWeapon(game.AssetStore.WeaponTemplates.GetByName("TestGun"));
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var now = new LogicFrame(10);
        weapon.Reload(now, random, Fix64.One, loadInstantly: true);
        weapon.FireShot(now, random, Fix64.One);   // 3-frame delay: ready at 13

        Assert.Equal(Fix64.Zero, weapon.GetPercentReadyToFire(new LogicFrame(10)));
        Assert.Equal(Fix64.One / new Fix64(3), weapon.GetPercentReadyToFire(new LogicFrame(11)));
        Assert.Equal(Fix64.One, weapon.GetPercentReadyToFire(new LogicFrame(13)));
    }

    // ================================================================
    // Xfer: mid-state save/load
    // ================================================================

    [Fact]
    public void SimWeapon_Xfer_RoundTripsMidCycle()
    {
        var game = NewGame();
        var template = game.AssetStore.WeaponTemplates.GetByName("TestGun");
        var random = game.GameEngine.SimContext.GameLogicRandom;
        var live = new SimWeapon(template);
        var now = new LogicFrame(10);
        live.Reload(now, random, Fix64.One, loadInstantly: true);
        live.FireShot(now, random, Fix64.One);     // mid-cycle: 1 round, between shots

        // Save the live weapon.
        var stream = new System.IO.MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            live.Xfer(save);
        }

        // Load into a differently-stated shadow.
        var shadow = new SimWeapon(template);
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            shadow.Xfer(load);
        }

        // CRC equality (the same walk the Objects channel folds).
        var liveCrc = new XferCrcVisitor();
        live.Xfer(liveCrc);
        var shadowCrc = new XferCrcVisitor();
        shadow.Xfer(shadowCrc);
        Assert.Equal(liveCrc.Value, shadowCrc.Value);

        // And the continuation is identical: both become ready on the same frame and
        // report the same ammo.
        Assert.Equal(live.AmmoInClip, shadow.AmmoInClip);
        Assert.Equal(live.WhenWeCanFireAgain, shadow.WhenWeCanFireAgain);
        Assert.Equal(
            live.GetStatus(new LogicFrame(13)),
            shadow.GetStatus(new LogicFrame(13)));
    }

    [Fact]
    public void ActiveBody_ShadowCopy_CrcEqualsLiveCrc_MidBehavior()
    {
        var game = NewGame();
        var live = Spawn(game, "ArmoredVictim");
        var shadow = Spawn(game, "ArmoredVictim");

        // Put the live body mid-behavior: damaged, scalared, subdual-free.
        BodyOf(live).ApplyDamageScalar(Fix64.Half);
        live.AttemptCombatDamage(Damage(40, DamageType.Slash));
        shadow.AttemptCombatDamage(Damage(80, DamageType.Pierce));  // differently-stated

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(BodyOf(live), BodyOf(shadow));
    }

    [Fact]
    public void ActiveBody_SaveLoad_ContinuationMatches()
    {
        var game = NewGame();
        var live = Spawn(game, "PlainVictim");
        live.AttemptCombatDamage(Damage(30, DamageType.Unresistable));

        var state = PortedModuleTestKit.Save(BodyOf(live));
        var restoredHost = Spawn(game, "PlainVictim");
        PortedModuleTestKit.Load(BodyOf(restoredHost), state);

        // Same follow-up damage produces the same Fix64 health on both.
        live.AttemptCombatDamage(Damage(25, DamageType.Unresistable));
        restoredHost.AttemptCombatDamage(Damage(25, DamageType.Unresistable));

        Assert.Equal(
            BodyOf(live).DamageCore.CurrentHealth,
            BodyOf(restoredHost).DamageCore.CurrentHealth);
        Assert.Equal(Fix(45), BodyOf(live).DamageCore.CurrentHealth);
    }

    // ================================================================
    // DamagePipeline: delivery + area filtering (GPL dealDamageInternal)
    // ================================================================

    [Fact]
    public void Pipeline_DirectDamage_HitsVictim()
    {
        var game = NewGame();
        var source = Spawn(game, "PlainVictim");
        var victim = Spawn(game, "ArmoredVictim", 10, 0);

        var output = DamagePipeline.DealDirectDamage(victim, Damage(40, DamageType.Slash, source));

        Assert.Equal(Fix(20), output.ActualDamageDealt);
    }

    [Fact]
    public void Pipeline_AreaDamage_PrimaryVictimIgnoresAffectsFlags()
    {
        var game = NewGame();
        var source = Spawn(game, "PlainVictim");
        var victim = Spawn(game, "PlainVictim", 10, 0);

        // Affects NOTHING - but the primary victim bypasses every check (GPL).
        DamagePipeline.DealAreaDamage(
            game.GameEngine.SimContext, source, victim, Fix(30),
            WeaponAffectsTypes.None, Damage(25, DamageType.Unresistable, source));

        Assert.Equal(Fix(75), BodyOf(victim).DamageCore.CurrentHealth);
        // The source stood inside the radius and was correctly skipped.
        Assert.Equal(Fix(100), BodyOf(source).DamageCore.CurrentHealth);
    }

    [Fact]
    public void Pipeline_AreaDamage_AlliesFlagGatesSplash()
    {
        var game = NewGame();
        var source = Spawn(game, "PlainVictim");
        var victim = Spawn(game, "PlainVictim", 10, 0);
        var bystander = Spawn(game, "PlainVictim", 15, 0);   // same owner = ally

        // Without the Allies flag the bystander is skipped...
        DamagePipeline.DealAreaDamage(
            game.GameEngine.SimContext, source, victim, Fix(30),
            WeaponAffectsTypes.None, Damage(25, DamageType.Unresistable, source));
        Assert.Equal(Fix(100), BodyOf(bystander).DamageCore.CurrentHealth);

        // ... with it, splash lands.
        DamagePipeline.DealAreaDamage(
            game.GameEngine.SimContext, source, victim, Fix(30),
            WeaponAffectsTypes.Allies, Damage(25, DamageType.Unresistable, source));
        Assert.Equal(Fix(75), BodyOf(bystander).DamageCore.CurrentHealth);
    }

    [Fact]
    public void Pipeline_AreaDamage_SuicideFlagKillsSource()
    {
        var game = NewGame();
        var source = Spawn(game, "PlainVictim");
        var victim = Spawn(game, "PlainVictim", 5, 0);

        DamagePipeline.DealAreaDamage(
            game.GameEngine.SimContext, source, victim, Fix(30),
            WeaponAffectsTypes.Suicide, Damage(25, DamageType.Unresistable, source));

        // The victim took the shot; the source took HUGE_DAMAGE_AMOUNT and died.
        Assert.Equal(Fix(75), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.True(source.IsEffectivelyDead);
    }
}
