// RespawnUpdate contract tests - the R14 respawn seam end to end on HeadlessSimGame with real
// parsed INI, so the audited parse path, the S1 Fix64 kill resolution, GameObject.OnDie's claim
// dispatch, GameLogic's real sleepy-update queue and the order-side applicator are all on the
// tested path.
//
// Behavioral reference: BFME2-only, ABSENT from generals-gpl. Facts under test come from the
// written seam design (bfme2-workbench/research/design-respawn-seam.md) as amended by the
// wave-2a adversarial review and ratified by dr-0033 - never from the retail binary.
//
// FRAME ARITHMETIC. Durations are milliseconds quantized by ceil(ms * 5 / 1000) at the frozen
// 5 Hz logic rate, so 200 ms is exactly 1 logic frame and every value below is a round
// multiple of 200. GameLogic.Update() increments its frame counter at the END of the tick, so
// the Nth game.Step() runs update modules at frame N-1; and a window opened before the step
// loop (the kill here is applied at frame 0) with end frame T therefore lapses on step T+1,
// not step T. Every frame-exact assertion below is written against that T+1 convention.

using System.Numerics;
using OpenSage.Logic;
using OpenSage.Logic.Object;
using OpenSage.Logic.Orders;
using OpenSage.Logic.Sim;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Update;

public class RespawnUpdateContractTests
{
    // DeathAnimationTime 1000 ms = 5 frames; RespawnRules Time 2000 ms = 10 frames;
    // RespawnAnimationTime 400 ms = 2 frames.
    private const string Definitions = @"
GameData
  UnitDamagedThreshold = 0.5
  UnitReallyDamagedThreshold = 0.1
End

Object ReviveHero
  KindOf = INFANTRY HERO SELECTABLE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
    PermanentlyKilledByFilter = NONE +STRUCTURE
  End
  Behavior = RespawnUpdate ModuleTag_Respawn
    DeathAnim = DYING
    DeathAnimationTime = 1000
    RespawnAnim = LEVELED
    RespawnAnimationTime = 400
    RespawnRules = AutoSpawn:No Cost:500 Time:2000 Health:100%
  End
End

Object AutoReviveHero
  KindOf = INFANTRY HERO SELECTABLE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = RespawnBody ModuleTag_Body
    MaxHealth = 100
    InitialHealth = 100
  End
  Behavior = RespawnUpdate ModuleTag_Respawn
    DeathAnimationTime = 1000
    RespawnAnimationTime = 0
    RespawnRules = AutoSpawn:Yes Cost:0 Time:2000 Health:100%
  End
End

; A RespawnUpdate on a plain ActiveBody - the data-error shape ClaimDeath must refuse.
Object BodylessReviveHero
  KindOf = INFANTRY HERO SELECTABLE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
  Behavior = RespawnUpdate ModuleTag_Respawn
    DeathAnimationTime = 1000
    RespawnRules = AutoSpawn:No Cost:500 Time:2000 Health:100%
  End
End

Object ReviveFortress
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 20
  GeometryHeight = 20
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
  Behavior = ProductionUpdate ModuleTag_Prod
    ProductionModifier
      CostMultiplier = 0.80
      TimeMultiplier = 0.50
      HeroRevive = Yes
      ModifierFilter = NONE +HERO
    End
  End
End

; Same building, no HeroRevive modifier: the base price must survive untouched.
Object PlainFortress
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 20
  GeometryHeight = 20
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 1000
  End
End

Object StructureKiller
  KindOf = STRUCTURE
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
End

Object InfantryKiller
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
End
";

    private static HeadlessSimGame NewGame(uint seed = 0xC0FFEEu)
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: seed);
        game.LoadIniText(Definitions);
        game.CivilianPlayer.BankAccount.Money = 100000;
        return game;
    }

    private static GameObject Spawn(HeadlessSimGame game, string definition, float x = 0)
        => game.SpawnObject(definition, game.CivilianPlayer, new Vector3(x, 0, 0));

    private static RespawnUpdate ReviveOf(GameObject gameObject)
        => Assert.IsType<RespawnUpdate>(gameObject.FindBehavior<RespawnUpdate>());

    private static void Kill(GameObject target, GameObject source = null)
        => target.AttemptCombatDamage(new CombatDamageInput
        {
            SourceId = source?.Id ?? ObjectId.Invalid,
            DamageType = DamageType.Magic,
            Amount = new SimCore.Numerics.Fix64(9999),
            Kill = false,
        });

    private static void Step(HeadlessSimGame game, int times)
    {
        for (var i = 0; i < times; i++)
        {
            game.Step();
        }
    }

    // ================================================================
    // ModuleData audit
    // ================================================================

    [Fact]
    public void ModuleData_QuantizesDurationsAndHealthPercent()
    {
        var game = NewGame();
        var data = Assert.IsType<RespawnUpdateModuleData>(
            game.AssetStore.ObjectDefinitions.GetByName("ReviveHero")
                .Behaviors["ModuleTag_Respawn"].Data);

        Assert.Equal(5u, data.DeathAnimationTime.Value);   // 1000 ms at 5 Hz
        Assert.Equal(2u, data.RespawnAnimationTime.Value); // 400 ms at 5 Hz
        Assert.NotNull(data.RespawnRules);
        Assert.False(data.RespawnRules.AutoSpawn);
        Assert.Equal(500, data.RespawnRules.Cost);
        Assert.Equal(10u, data.RespawnRules.Time.Value);   // 2000 ms at 5 Hz
        // "Health:100%" reaches the sim as an INTEGER percent, not a float-backed Percentage.
        Assert.Equal(100, data.RespawnRules.HealthPercent);
    }

    // ================================================================
    // Part A - the claim, and the reap suppression it buys
    // ================================================================

    [Fact]
    public void NonPermanentDeath_IsClaimed_AndTheObjectIsNotReaped()
    {
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var killer = Spawn(game, "InfantryKiller", 50);

        Kill(hero, killer);

        Assert.True(hero.IsEffectivelyDead);
        Assert.Equal(RespawnPhase.DeathAnimation, ReviveOf(hero).Phase);
        Assert.False(hero.IsDestroyed);

        // The whole point: several frames later the object is still in the world, still
        // ticking. Nothing reached GameLogic's destroy list, so DeleteDestroyed never ran on it.
        Step(game, 20);
        Assert.False(hero.IsDestroyed);
        Assert.NotNull(game.GameLogic.GetObjectById(hero.Id));
    }

    [Fact]
    public void PermanentDeath_IsNotClaimed_AndTakesTheOrdinaryPath()
    {
        // H1 IS THE POINT OF THIS TEST. ActiveBody calls OnDie from INSIDE base.AttemptDamage,
        // so a ClaimDeath that read RespawnBody.IsPermanentlyKilled instead of resolving from
        // the killing blow would see false here and claim - and strand - a permanent death.
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var killer = Spawn(game, "StructureKiller", 50);

        Kill(hero, killer);

        var body = Assert.IsType<RespawnBody>(hero.BodyModule);
        Assert.True(body.IsPermanentlyKilled);
        Assert.Equal(RespawnPhase.PermanentlyDead, ReviveOf(hero).Phase);

        // No die module on this template, so OnDie's no-die-module fallback destroyed it.
        Assert.True(hero.IsDestroyed);
    }

    [Fact]
    public void RespawnUpdateOnANonRespawnBody_RefusesTheClaim()
    {
        var game = NewGame();
        var hero = Spawn(game, "BodylessReviveHero");
        var killer = Spawn(game, "InfantryKiller", 50);

        Kill(hero, killer);

        // Conservative default: claiming a death we have no body-side way to undo would strand
        // the object forever, so the object dies normally instead.
        Assert.Equal(RespawnPhase.PermanentlyDead, ReviveOf(hero).Phase);
        Assert.True(hero.IsDestroyed);
    }

    [Fact]
    public void ClaimedDeath_HidesTheObject_WhenTheDeathAnimationLapses()
    {
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        Kill(hero, Spawn(game, "InfantryKiller", 50));

        var revive = ReviveOf(hero);

        // The window opened at frame 0 with DeathAnimationTime = 5 frames, so it lapses on
        // step 6, not step 5 (T+1).
        Step(game, 5);
        Assert.Equal(RespawnPhase.DeathAnimation, revive.Phase);
        Assert.False(hero.Hidden);

        Step(game, 1);
        Assert.Equal(RespawnPhase.AwaitingRevive, revive.Phase);
        Assert.True(hero.Hidden);
        Assert.False(hero.IsSelectable);
    }

    // ================================================================
    // Part B/C - purchase routing, pricing and the affordability guard
    // ================================================================

    [Fact]
    public void Revive_IsRefused_WhileTheDeathAnimationIsStillRunning()
    {
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "ReviveFortress", 100);
        Kill(hero, Spawn(game, "InfantryKiller", 50));

        var before = game.CivilianPlayer.BankAccount.Money;
        var result = ReviveApplicator.Apply(game, game.CivilianPlayer, hero.Id, anchor.Id);

        Assert.Equal(ReviveOrderResult.NotRevivable, result);
        Assert.Equal(before, game.CivilianPlayer.BankAccount.Money); // a refusal mutates nothing
    }

    [Fact]
    public void Revive_AppliesTheAnchorsHeroReviveModifier_ToCostAndTime()
    {
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "ReviveFortress", 100);
        Kill(hero, Spawn(game, "InfantryKiller", 50));
        Step(game, 6); // lapse the death animation

        var before = game.CivilianPlayer.BankAccount.Money;
        Assert.Equal(ReviveOrderResult.Started, ReviveApplicator.Apply(game, game.CivilianPlayer, hero.Id, anchor.Id));

        // 500 * 0.80 = 400, through ProductionMath's one-multiply-truncate-toward-zero.
        Assert.Equal(before - 400u, game.CivilianPlayer.BankAccount.Money);
        Assert.Equal(400, ReviveOf(hero).PaidReviveCost);
        Assert.Equal(anchor.Id, ReviveOf(hero).ReviveAnchorId);
        Assert.Equal(RespawnPhase.Reviving, ReviveOf(hero).Phase);

        // TimeMultiplier 0.50 halves the 10-frame countdown to 5. BeginRevive ran at frame 6
        // (six steps have completed), so the end frame is 11 and it lapses on the 6th step
        // from here, not the 5th (T+1).
        Step(game, 5);
        Assert.Equal(RespawnPhase.Reviving, ReviveOf(hero).Phase);
        Step(game, 1);
        Assert.Equal(RespawnPhase.RespawnAnimation, ReviveOf(hero).Phase);
    }

    [Fact]
    public void Revive_WithoutAHeroReviveModifier_ChargesTheBasePrice()
    {
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "PlainFortress", 100);
        Kill(hero, Spawn(game, "InfantryKiller", 50));
        Step(game, 6);

        var before = game.CivilianPlayer.BankAccount.Money;
        Assert.Equal(ReviveOrderResult.Started, ReviveApplicator.Apply(game, game.CivilianPlayer, hero.Id, anchor.Id));
        Assert.Equal(before - 500u, game.CivilianPlayer.BankAccount.Money);
    }

    [Fact]
    public void Revive_IsRefusedDeterministically_WhenTheMoneyIsGone()
    {
        // BankAccount.Withdraw CLAMPS to the balance rather than failing, so without the
        // CanAfford guard this would silently be a free revive on every peer that happened to
        // process a competing spend first - a desync, not just a balance bug.
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "PlainFortress", 100);
        Kill(hero, Spawn(game, "InfantryKiller", 50));
        Step(game, 6);

        game.CivilianPlayer.BankAccount.Money = 499;
        Assert.Equal(ReviveOrderResult.Unaffordable, ReviveApplicator.Apply(game, game.CivilianPlayer, hero.Id, anchor.Id));

        Assert.Equal(499u, game.CivilianPlayer.BankAccount.Money);
        Assert.Equal(RespawnPhase.AwaitingRevive, ReviveOf(hero).Phase);
    }

    [Fact]
    public void Revive_ThroughTheOrderProcessorVocabulary_CarriesHeroAnchorAndSlot()
    {
        var order = Order.CreateRevive(3, new ObjectId(17), new ObjectId(42), 5);

        // OQ-3/dr-0033: the value is the recovered BFME2 MSG_REVIVE number, carried into the
        // otherwise ZH-numbered live enum so the two vocabularies agree before they unify.
        Assert.Equal(1114, (int)order.OrderType);
        Assert.Equal(OrderType.Revive, order.OrderType);
        Assert.Equal(new ObjectId(17), order.Arguments[0].Value.ObjectId);
        Assert.Equal(new ObjectId(42), order.Arguments[1].Value.ObjectId);
        Assert.Equal(5, order.Arguments[2].Value.Integer);
    }

    [Fact]
    public void CancelRevive_RefundsExactlyWhatWasPaid_AndReturnsToAwaiting()
    {
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "ReviveFortress", 100);
        Kill(hero, Spawn(game, "InfantryKiller", 50));
        Step(game, 6);
        ReviveApplicator.Apply(game, game.CivilianPlayer, hero.Id, anchor.Id);

        var revive = ReviveOf(hero);
        Assert.True(revive.CancelRevive(out var refund));

        Assert.Equal(400, refund); // what was actually withdrawn, not a recomputed price
        Assert.Equal(RespawnPhase.AwaitingRevive, revive.Phase);
        Assert.Equal(0, revive.PaidReviveCost);
        Assert.False(revive.CancelRevive(out _)); // idempotent: nothing in flight now
    }

    // ================================================================
    // H4 - the exit from the dead state, through the Body
    // ================================================================

    [Fact]
    public void CompletedRevive_ClearsIsEffectivelyDead_ThroughTheBodysHealthRestore()
    {
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "PlainFortress", 100);
        Kill(hero, Spawn(game, "InfantryKiller", 50));
        Step(game, 6);
        ReviveApplicator.Apply(game, game.CivilianPlayer, hero.Id, anchor.Id);

        var revive = ReviveOf(hero);
        var body = Assert.IsType<RespawnBody>(hero.BodyModule);

        // RespawnRules Time = 10 frames and BeginRevive ran at frame 6 (six steps have
        // completed), so the countdown ends at frame 16 and lapses on the 11th step from here,
        // not the 10th (T+1).
        Step(game, 10);
        Assert.Equal(RespawnPhase.Reviving, revive.Phase);
        Assert.True(hero.IsEffectivelyDead);

        Step(game, 1);
        Assert.False(hero.IsEffectivelyDead);
        Assert.False(hero.Hidden);
        Assert.True(hero.IsSelectable);
        // Health:100% of InitialHealth, applied by the body's exact Fix64 mul-div.
        Assert.Equal(new SimCore.Numerics.Fix64(100), body.DamageCore.CurrentHealth);
    }

    [Fact]
    public void RevivedHero_ReturnsToAlive_AfterTheRespawnAnimation()
    {
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "PlainFortress", 100);
        Kill(hero, Spawn(game, "InfantryKiller", 50));
        Step(game, 6);
        ReviveApplicator.Apply(game, game.CivilianPlayer, hero.Id, anchor.Id);

        var revive = ReviveOf(hero);
        Step(game, 11); // revive completes at frame 16; RespawnAnimationTime = 2 frames begins
        Assert.Equal(RespawnPhase.RespawnAnimation, revive.Phase);

        Step(game, 2);
        Assert.Equal(RespawnPhase.Alive, revive.Phase);
    }

    [Fact]
    public void SecondDeathAfterARevive_ResolvesItsOwnPermanence()
    {
        // The second-death latch the review asked for. Revive() re-arms the body's permanence
        // resolver; without that, the stale "already resolved" latch would skip the filter test
        // on the second death and a structure kill would be treated as revivable.
        var game = NewGame();
        var hero = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "PlainFortress", 100);
        var infantry = Spawn(game, "InfantryKiller", 50);
        var structure = Spawn(game, "StructureKiller", 150);

        Kill(hero, infantry);
        Step(game, 6);
        ReviveApplicator.Apply(game, game.CivilianPlayer, hero.Id, anchor.Id);
        Step(game, 13); // 11 steps to complete the revive at frame 16, then 2 for the anim

        var revive = ReviveOf(hero);
        var body = Assert.IsType<RespawnBody>(hero.BodyModule);
        Assert.Equal(RespawnPhase.Alive, revive.Phase);
        Assert.False(body.IsPermanentlyKilled);
        Assert.False(body.IsPermanenceResolved);

        Kill(hero, structure);

        Assert.True(body.IsPermanentlyKilled);
        Assert.Equal(RespawnPhase.PermanentlyDead, revive.Phase);
        Assert.True(hero.IsDestroyed);
    }

    [Fact]
    public void AutoSpawnHero_RevivesWithNoOrderAndNoMoney()
    {
        var game = NewGame();
        var hero = Spawn(game, "AutoReviveHero");
        Kill(hero, Spawn(game, "InfantryKiller", 50));

        var revive = ReviveOf(hero);
        var before = game.CivilianPlayer.BankAccount.Money;

        Step(game, 6); // death animation lapses straight into the countdown
        Assert.Equal(RespawnPhase.Reviving, revive.Phase);

        Step(game, 10);
        Assert.Equal(RespawnPhase.Alive, revive.Phase); // RespawnAnimationTime = 0, no anim phase
        Assert.False(hero.IsEffectivelyDead);
        Assert.Equal(before, game.CivilianPlayer.BankAccount.Money);
    }

    // ================================================================
    // Xfer: H2 - the awaiting-revive state is MODULE state
    // ================================================================

    [Fact]
    public void RevivePhase_ParticipatesInCrc()
    {
        var game = NewGame();
        var alive = Spawn(game, "ReviveHero");
        var dead = Spawn(game, "ReviveHero", 200);
        Kill(dead, Spawn(game, "InfantryKiller", 50));

        // A module that forgot to walk its phase would fold identically here.
        Assert.NotEqual(
            PortedModuleTestKit.LiveCrc(ReviveOf(alive)),
            PortedModuleTestKit.LiveCrc(ReviveOf(dead)));
    }

    [Fact]
    public void ShadowCopy_CrcEqualsLiveCrc_MidLifecycle()
    {
        var game = NewGame();
        var live = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "ReviveFortress", 100);
        Kill(live, Spawn(game, "InfantryKiller", 50));
        Step(game, 6);
        ReviveApplicator.Apply(game, game.CivilianPlayer, live.Id, anchor.Id);

        var shadow = Spawn(game, "ReviveHero", 300); // differently-stated: still alive

        PortedModuleTestKit.AssertShadowCopyCrcEqualsLiveCrc(ReviveOf(live), ReviveOf(shadow));
    }

    [Fact]
    public void SaveLoad_MidRevive_Continues()
    {
        var game = NewGame();
        var live = Spawn(game, "ReviveHero");
        var anchor = Spawn(game, "ReviveFortress", 100);
        Kill(live, Spawn(game, "InfantryKiller", 50));
        Step(game, 6);
        ReviveApplicator.Apply(game, game.CivilianPlayer, live.Id, anchor.Id);

        var state = PortedModuleTestKit.Save(ReviveOf(live));
        var restoredHost = Spawn(game, "ReviveHero", 300);
        Assert.Equal(RespawnPhase.Alive, ReviveOf(restoredHost).Phase);

        PortedModuleTestKit.Load(ReviveOf(restoredHost), state);

        Assert.Equal(RespawnPhase.Reviving, ReviveOf(restoredHost).Phase);
        Assert.Equal(anchor.Id, ReviveOf(restoredHost).ReviveAnchorId);
        Assert.Equal(400, ReviveOf(restoredHost).PaidReviveCost);
        Assert.Equal(
            PortedModuleTestKit.LiveCrc(ReviveOf(live)),
            PortedModuleTestKit.LiveCrc(ReviveOf(restoredHost)));
    }
}
