// S4 system tests (build-roadmap pillar economy-production): resource accounting,
// production queue/build timers, cost/time formulas, and experience->veterancy
// progression - one test per core formula/branch, plus Xfer round-trips through the
// SimCore visitors (save -> load -> CRC == live CRC) and mid-state save/load
// continuations. The compiler test runs on HeadlessSimGame with real parsed
// ExperienceLevel INI (the BFME2/AotR experiencelevels.ini shape) so the real parse
// path feeds the table.

using System.Collections.Generic;
using System.IO;
using OpenSage.Logic.Economy;
using OpenSage.Logic.Sim;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Economy;

public class EconomyProductionSystemTests
{
    private static Fix64 Fix(int value) => new(value);

    /// <summary>
    /// value/100 as a Fix64 THROUGH THE BLESSED PARSE PATH (F4: FromDecimalLiteral,
    /// round-half-up) - the same quantization INI percent fields get, so boundary
    /// products (800 * 0.8) land on the same side as parsed data.
    /// </summary>
    private static Fix64 Percent(int value)
    {
        var negative = value < 0;
        var abs = negative ? -value : value;
        var text = (negative ? "-" : "") + (abs / 100) + "." + (abs % 100).ToString("00");
        return Fix64.FromDecimalLiteral(text);
    }

    // ================================================================
    // ResourceBank: GPL Money withdraw/deposit semantics
    // ================================================================

    [Fact]
    public void Money_WithdrawClampsToBalanceAndReturnsActual()
    {
        var bank = new ResourceBank(100);

        Assert.Equal(60u, bank.Withdraw(60));
        Assert.Equal(40u, bank.Money);

        // Over-withdraw takes what's there (GPL Money::withdraw clamp).
        Assert.Equal(40u, bank.Withdraw(500));
        Assert.Equal(0u, bank.Money);

        Assert.Equal(0u, bank.Withdraw(10));
    }

    [Fact]
    public void Money_DepositAccumulates()
    {
        var bank = new ResourceBank();
        bank.Deposit(0);      // no-op branch
        bank.Deposit(250);
        bank.Deposit(750);
        Assert.Equal(1000u, bank.Money);
        Assert.True(bank.CanAfford(1000));
        Assert.False(bank.CanAfford(1001));
    }

    [Fact]
    public void Money_XferRoundTripsAndCrcMatches()
    {
        var live = new ResourceBank(12345);

        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            live.Xfer(save);
        }

        var shadow = new ResourceBank(999);
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            shadow.Xfer(load);
        }

        Assert.Equal(12345u, shadow.Money);

        var liveCrc = new XferCrcVisitor();
        live.Xfer(liveCrc);
        var shadowCrc = new XferCrcVisitor();
        shadow.Xfer(shadowCrc);
        Assert.Equal(liveCrc.Value, shadowCrc.Value);
    }

    // ================================================================
    // CommandPointsBank: the BFME2 population pool
    // ================================================================

    [Fact]
    public void CommandPoints_UseReleaseAndAfford()
    {
        var pool = new CommandPointsBank(limit: 300);

        Assert.True(pool.CanAfford(300));
        pool.Use(120);
        Assert.Equal(120, pool.Used);
        Assert.Equal(180, pool.Available);

        Assert.True(pool.CanAfford(180));
        Assert.False(pool.CanAfford(181));

        // Zero-cost entries always fit (structures, heroes with CommandPoints = 0).
        pool.Use(180);
        Assert.True(pool.CanAfford(0));

        pool.Release(120);
        Assert.Equal(180, pool.Used);

        // Release clamps at zero.
        pool.Release(10000);
        Assert.Equal(0, pool.Used);

        // Limit can move mid-game (fortress upgrades / WotR bonuses).
        pool.SetLimit(600);
        Assert.Equal(600, pool.Available);
    }

    // ================================================================
    // ProductionMath: GPL calcCostToBuild / calcTimeToBuild
    // ================================================================

    [Fact]
    public void Math_TruncateTowardZero()
    {
        Assert.Equal(1, ProductionMath.TruncateTowardZero(new Fix64(3) / new Fix64(2)));
        Assert.Equal(-1, ProductionMath.TruncateTowardZero(new Fix64(-3) / new Fix64(2)));
        Assert.Equal(0, ProductionMath.TruncateTowardZero(Fix64.Zero));
        Assert.Equal(5, ProductionMath.TruncateTowardZero(Fix64.Zero + new Fix64(5)));
    }

    [Fact]
    public void Math_CostToBuild_PercentChange()
    {
        // GPL comment: "-.2 equals 20% cheaper".
        Assert.Equal(640, ProductionMath.CalcCostToBuild(800, Percent(-20), Fix64.One, Fix64.One));
        // No modifiers: identity.
        Assert.Equal(800, ProductionMath.CalcCostToBuild(800, Fix64.Zero, Fix64.One, Fix64.One));
        // +50% dearer.
        Assert.Equal(1200, ProductionMath.CalcCostToBuild(800, Percent(50), Fix64.One, Fix64.One));
    }

    [Fact]
    public void Math_CostToBuild_KindOfChangesStackMultiplicatively()
    {
        // GPL getProductionCostChangeBasedOnKindOf: start 1; each match *= (1 + pct).
        var factor = Fix64.One;
        factor = ProductionMath.StackKindOfCostChange(factor, Percent(10));   // * 1.1
        factor = ProductionMath.StackKindOfCostChange(factor, Percent(-50));  // * 0.5
        // 800 * 1.1 * 0.5 = 440
        Assert.Equal(440, ProductionMath.CalcCostToBuild(800, Fix64.Zero, factor, Fix64.One));
    }

    [Fact]
    public void Math_CostToBuild_HandicapAndTruncation()
    {
        // 100 * 1 * 1 * 0.75 = 75; 33 * 0.5 = 16.5 -> truncates to 16.
        Assert.Equal(75, ProductionMath.CalcCostToBuild(100, Fix64.Zero, Fix64.One, Percent(75)));
        Assert.Equal(16, ProductionMath.CalcCostToBuild(33, Fix64.Zero, Fix64.One, Percent(50)));
    }

    [Fact]
    public void Math_TimeToBuild_StepSequence()
    {
        var baseTime = new LogicFrameSpan(100);

        // No modifiers: identity.
        Assert.Equal(100u, ProductionMath.CalcTimeToBuildFrames(
            baseTime, Fix64.One, Fix64.Zero, Fix64.One).Value);

        // Handicap 1.5 then -50% change: trunc(100*1.5)=150; trunc(150*0.5)=75.
        Assert.Equal(75u, ProductionMath.CalcTimeToBuildFrames(
            baseTime, Fix64.One + Fix64.Half, Percent(-50), Fix64.One).Value);

        // Energy penalty rate 0.5 doubles the time: trunc(100/0.5)=200.
        Assert.Equal(200u, ProductionMath.CalcTimeToBuildFrames(
            baseTime, Fix64.One, Fix64.Zero, Fix64.Half).Value);

        // Per-step truncation is observable: 33 frames * 0.5 truncates to 16 BEFORE
        // the next step (not carried as 16.5).
        Assert.Equal(8u, ProductionMath.CalcTimeToBuildFrames(
            new LogicFrameSpan(33), Fix64.Half, Percent(-50), Fix64.One).Value);
    }

    [Fact]
    public void Math_TimeToBuild_MultipleFactoryDiscount()
    {
        // GPL: per EXTRA factory, buildTime *= factoryMult (trunc each step).
        // 100 * 0.75 = 75; 75 * 0.75 = 56.25 -> 56.
        Assert.Equal(56u, ProductionMath.CalcTimeToBuildFrames(
            new LogicFrameSpan(100), Fix64.One, Fix64.Zero, Fix64.One,
            extraFactoryCount: 2, multipleFactoryMultiplier: Percent(75)).Value);

        // Zero/negative multiplier disables the loop (GPL `if (factoryMult > 0)`).
        Assert.Equal(100u, ProductionMath.CalcTimeToBuildFrames(
            new LogicFrameSpan(100), Fix64.One, Fix64.Zero, Fix64.One,
            extraFactoryCount: 3, multipleFactoryMultiplier: Fix64.Zero).Value);
    }

    [Fact]
    public void Math_EnergyPenaltyRate_Branches()
    {
        // 80% energy, 40% penalty modifier: short = 0.2*0.4 = 0.08 -> rate 0.92,
        // then the underpowered cap pulls it to MaxLowEnergyProductionSpeed 0.8.
        Assert.Equal(Percent(80), ProductionMath.CalcEnergyPenaltyRate(
            Percent(80), Percent(40), Percent(10), Percent(80)));

        // Same but cap above: rate stays 0.92.
        var rate = ProductionMath.CalcEnergyPenaltyRate(
            Percent(80), Percent(40), Percent(10), Percent(95));
        // 1 - (1-0.8)*0.4 = 0.92 up to Fix64 quantization of the inputs.
        var delta = rate.RawValue - Percent(92).RawValue;
        Assert.InRange(delta < 0 ? -delta : delta, 0, 8);

        // Zero energy, full modifier: rate 0 -> min clamp 0.1.
        Assert.Equal(Percent(10), ProductionMath.CalcEnergyPenaltyRate(
            Fix64.Zero, Fix64.One, Percent(10), Percent(80)));

        // Full energy (and over): no penalty, no cap.
        Assert.Equal(Fix64.One, ProductionMath.CalcEnergyPenaltyRate(
            Fix64.One + Fix64.One, Fix64.One, Percent(10), Percent(80)));

        // Design floor: min 0 would dead-stop, floored at 0.01.
        Assert.Equal(Fix64.One / new Fix64(100), ProductionMath.CalcEnergyPenaltyRate(
            Fix64.Zero, Fix64.One, Fix64.Zero, Fix64.One));
    }

    [Fact]
    public void Math_ProductionModifierTruncates()
    {
        // BFME2 ProductionModifier CostMultiplier (pinned trunc-toward-zero).
        Assert.Equal(450, ProductionMath.ApplyProductionMultiplier(600, Percent(75)));
        Assert.Equal(16, ProductionMath.ApplyProductionMultiplier(33, Fix64.Half));
    }

    // ================================================================
    // ProductionQueueCore: queue / timers / cost / refund
    // ================================================================

    [Fact]
    public void Queue_QueueUnitWithdrawsCost()
    {
        var bank = new ResourceBank(1000);
        var queue = new ProductionQueueCore();

        Assert.True(queue.QueueUnit(templateKey: 7, productionId: queue.AllocateProductionId(), cost: 300, quantity: 1, bank));
        Assert.Equal(700u, bank.Money);
        Assert.True(queue.IsProducing);
        Assert.Equal(ProductionKind.Unit, queue.Front.Kind);
        Assert.Equal(7u, queue.Front.TemplateKey);
        Assert.Equal(1, queue.Front.ProductionId);
        Assert.Equal(1, queue.CountUnitTypeInQueue(7));
    }

    [Fact]
    public void Queue_WithdrawClampsWhenPoor()
    {
        // GPL: affordability is checked by BuildAssistant BEFORE queueing; the withdraw
        // itself clamps, so a forced queue on a poor player empties the purse.
        var bank = new ResourceBank(100);
        var queue = new ProductionQueueCore();

        Assert.True(queue.QueueUnit(1, queue.AllocateProductionId(), cost: 300, quantity: 1, bank));
        Assert.Equal(0u, bank.Money);
    }

    [Fact]
    public void Queue_MaxEntriesEnforced()
    {
        var bank = new ResourceBank(10000);
        var queue = new ProductionQueueCore(maxQueueEntries: 2);

        Assert.True(queue.QueueUnit(1, queue.AllocateProductionId(), 10, 1, bank));
        Assert.True(queue.QueueUnit(1, queue.AllocateProductionId(), 10, 1, bank));
        Assert.False(queue.CanQueue);
        Assert.False(queue.QueueUnit(1, queue.AllocateProductionId(), 10, 1, bank));
        Assert.Equal(9980u, bank.Money);   // third withdraw never happened
        Assert.Equal(2, queue.Count);
    }

    [Fact]
    public void Queue_DuplicateUpgradeRejected()
    {
        var bank = new ResourceBank(1000);
        var queue = new ProductionQueueCore();

        Assert.True(queue.QueueUpgrade(upgradeKey: 42, cost: 200, bank));
        Assert.True(queue.IsUpgradeInQueue(42));
        // GPL: "you cannot queue the production of an upgrade twice in this queue".
        Assert.False(queue.QueueUpgrade(42, 200, bank));
        Assert.Equal(800u, bank.Money);
    }

    [Fact]
    public void Queue_CancelRefunds()
    {
        var bank = new ResourceBank(1000);
        var queue = new ProductionQueueCore();
        var id = queue.AllocateProductionId();
        queue.QueueUnit(7, id, cost: 300, quantity: 1, bank);
        queue.QueueUpgrade(42, cost: 200, bank);
        Assert.Equal(500u, bank.Money);

        // GPL recomputes calcCostToBuild at cancel: the refund is the CALLER's number,
        // legally different from the amount withdrawn if modifiers changed in between.
        Assert.True(queue.CancelUnit(id, refund: 240, bank));
        Assert.Equal(740u, bank.Money);
        Assert.Equal(1, queue.Count);

        Assert.True(queue.CancelUpgrade(42, refund: 200, bank));
        Assert.Equal(940u, bank.Money);
        Assert.False(queue.IsProducing);

        // Unknown id: no refund, no change.
        Assert.False(queue.CancelUnit(999, 100, bank));
        Assert.Equal(940u, bank.Money);
    }

    [Fact]
    public void Queue_AdvanceCompletesExactlyAtTotalFrames()
    {
        var bank = new ResourceBank(1000);
        var queue = new ProductionQueueCore();
        queue.QueueUnit(7, queue.AllocateProductionId(), 100, 1, bank);

        // GPL: frames++ then percent = frames/total*100, complete at >= 100 - exactly
        // frames >= total.
        for (var frame = 1; frame < 10; frame++)
        {
            Assert.Equal(ProductionAdvanceResult.InProgress, queue.AdvanceFront(totalProductionFrames: 10));
        }
        Assert.Equal(ProductionAdvanceResult.Complete, queue.AdvanceFront(10));
        Assert.Equal(10, queue.Front.FramesUnderConstruction);

        queue.MarkFrontUnitProduced();
        Assert.Equal(0, queue.Front.QuantityRemaining);
        queue.RemoveFront();
        Assert.Equal(ProductionAdvanceResult.Idle, queue.AdvanceFront(10));
    }

    [Fact]
    public void Queue_ProgressIsExactRatio()
    {
        var bank = new ResourceBank(1000);
        var queue = new ProductionQueueCore();
        queue.QueueUnit(7, queue.AllocateProductionId(), 100, 1, bank);

        for (var i = 0; i < 25; i++)
        {
            queue.AdvanceFront(100);
        }

        // 25/100 = exactly 0.25 in Fix64.
        Assert.Equal(Fix64.One / new Fix64(4), queue.GetFrontProgress(100));
        Assert.Equal(Fix64.One, queue.GetFrontProgress(25));  // frames >= total
        Assert.Equal(Fix64.One, queue.GetFrontProgress(0));   // degenerate total
    }

    [Fact]
    public void Queue_QuantityModifierProducesN()
    {
        // GPL QuantityModifier: pay once, build four (Chinese Red Guards shape).
        var bank = new ResourceBank(1000);
        var queue = new ProductionQueueCore();
        queue.QueueUnit(7, queue.AllocateProductionId(), 300, quantity: 4, bank);
        Assert.Equal(700u, bank.Money);   // paid once

        queue.AdvanceFront(1);
        Assert.Equal(4, queue.Front.QuantityRemaining);

        Assert.Equal(3, queue.MarkFrontUnitProduced());
        Assert.Equal(2, queue.MarkFrontUnitProduced());
        Assert.Equal(1, queue.MarkFrontUnitProduced());
        Assert.Equal(0, queue.MarkFrontUnitProduced());
        queue.RemoveFront();
        Assert.False(queue.IsProducing);
    }

    [Fact]
    public void Queue_XferRoundTripsMidProduction()
    {
        var bank = new ResourceBank(1000);
        var live = new ProductionQueueCore();
        live.QueueUnit(7, live.AllocateProductionId(), 300, 4, bank);
        live.QueueUpgrade(42, 200, bank);
        for (var i = 0; i < 6; i++)
        {
            live.AdvanceFront(10);
        }
        live.MarkFrontUnitProduced();

        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            live.Xfer(save);
        }

        // Load into a differently-stated shadow.
        var shadowBank = new ResourceBank(50);
        var shadow = new ProductionQueueCore();
        shadow.QueueUnit(99, shadow.AllocateProductionId(), 10, 1, shadowBank);
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            shadow.Xfer(load);
        }

        // CRC equality (the walk the Players/Objects channel folds).
        var liveCrc = new XferCrcVisitor();
        live.Xfer(liveCrc);
        var shadowCrc = new XferCrcVisitor();
        shadow.Xfer(shadowCrc);
        Assert.Equal(liveCrc.Value, shadowCrc.Value);

        // Continuation identical: both complete the head entry on the same tick.
        Assert.Equal(2, shadow.Count);
        Assert.Equal(6, shadow.Front.FramesUnderConstruction);
        Assert.Equal(1, shadow.Front.QuantityProduced);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(live.AdvanceFront(10), shadow.AdvanceFront(10));
        }
        Assert.Equal(ProductionAdvanceResult.Complete, live.AdvanceFront(10));
        Assert.Equal(ProductionAdvanceResult.Complete, shadow.AdvanceFront(10));

        // The id allocator survived too: next ids match.
        Assert.Equal(live.AllocateProductionId(), shadow.AllocateProductionId());
    }

    // ================================================================
    // Veterancy: GPL ExperienceTracker progression
    // ================================================================

    private static VeterancyLevelTable ZhStyleTable()
        // ZH shape: 4 levels, thresholds [0, 100, 300, 600], awards per level.
        => new(
            requiredExperience: [0, 100, 300, 600],
            experienceAward: [10, 20, 40, 80]);

    [Fact]
    public void Xp_LevelScanWalksThresholds()
    {
        var core = new ExperienceCore(ZhStyleTable(), isTrainable: true);

        var change = core.AddExperiencePoints(99, canScaleForBonus: false);
        Assert.False(change.Changed);
        Assert.Equal(0, core.CurrentLevel);

        change = core.AddExperiencePoints(1, false);
        Assert.True(change.Changed);
        Assert.Equal(0, change.OldLevel);
        Assert.Equal(1, change.NewLevel);

        // A big grant jumps multiple levels in one edge (GPL scan-from-zero).
        change = core.AddExperiencePoints(500, false);
        Assert.Equal(1, change.OldLevel);
        Assert.Equal(3, change.NewLevel);
        Assert.Equal(600, core.CurrentExperience);
    }

    [Fact]
    public void Xp_ScalarTruncatesTowardZero()
    {
        var core = new ExperienceCore(ZhStyleTable(), isTrainable: true)
        {
            ExperienceScalar = Fix64.Half,
        };

        // GPL `Int amount *= Real scalar` truncates: 5 * 0.5 = 2.5 -> 2.
        core.AddExperiencePoints(5, canScaleForBonus: true);
        Assert.Equal(2, core.CurrentExperience);

        // Unscaled path ignores the scalar.
        core.AddExperiencePoints(5, canScaleForBonus: false);
        Assert.Equal(7, core.CurrentExperience);

        // Negative gain truncates toward zero: -5 * 0.5 = -2.5 -> -2.
        core.AddExperiencePoints(-5, canScaleForBonus: true);
        Assert.Equal(5, core.CurrentExperience);
    }

    [Fact]
    public void Xp_NotTrainableIsNoOp_SinkForwardScales()
    {
        var untrainable = new ExperienceCore(ZhStyleTable(), isTrainable: false);
        Assert.False(untrainable.IsAcceptingExperiencePoints);
        var change = untrainable.AddExperiencePoints(1000, false);
        Assert.False(change.Changed);
        Assert.Equal(0, untrainable.CurrentExperience);

        // With a sink set, the object accepts points (they belong to someone else) and
        // the forwarded amount is trunc(gain * scalar) - GPL addExperiencePoints sink
        // branch.
        untrainable.ExperienceSink = new ObjectId(77);
        Assert.True(untrainable.IsAcceptingExperiencePoints);
        untrainable.ExperienceScalar = Fix64.Half;
        Assert.Equal(7, untrainable.PrepareSinkForward(15));
    }

    [Fact]
    public void Xp_SetVeterancyLevelSnapsExperience()
    {
        var core = new ExperienceCore(ZhStyleTable(), isTrainable: true);

        var change = core.SetVeterancyLevel(2);
        Assert.True(change.Changed);
        Assert.Equal(300, core.CurrentExperience);   // threshold of the set level

        // SetMin: upward only.
        Assert.False(core.SetMinVeterancyLevel(1).Changed);
        Assert.Equal(2, core.CurrentLevel);
        var up = core.SetMinVeterancyLevel(3);
        Assert.True(up.Changed);
        Assert.Equal(600, core.CurrentExperience);

        // Clamped at the last level.
        Assert.False(core.SetVeterancyLevel(99).Changed);
        Assert.Equal(3, core.CurrentLevel);
    }

    [Fact]
    public void Xp_GainExpForLevel()
    {
        var core = new ExperienceCore(ZhStyleTable(), isTrainable: true);
        core.AddExperiencePoints(50, false);

        Assert.True(core.CanGainExpForLevel(2));
        var change = core.GainExpForLevel(2, canScaleForBonus: false);
        Assert.Equal(2, change.NewLevel);
        Assert.Equal(300, core.CurrentExperience);   // exactly the level-2 threshold

        // From the last level there is nothing to gain.
        core.GainExpForLevel(5, false);
        Assert.Equal(3, core.CurrentLevel);
        Assert.False(core.CanGainExpForLevel(1));
        Assert.False(core.GainExpForLevel(1, false).Changed);
    }

    [Fact]
    public void Xp_SetExperienceAndLevelCanGoDown()
    {
        var core = new ExperienceCore(ZhStyleTable(), isTrainable: true);
        core.AddExperiencePoints(600, false);
        Assert.Equal(3, core.CurrentLevel);

        // GPL's own "paradox! this may be a level lost!" branch.
        var change = core.SetExperienceAndLevel(150);
        Assert.Equal(3, change.OldLevel);
        Assert.Equal(1, change.NewLevel);
        Assert.Equal(150, core.CurrentExperience);
    }

    [Fact]
    public void Xp_ExperienceValueForKiller()
    {
        var core = new ExperienceCore(ZhStyleTable(), isTrainable: true);
        core.AddExperiencePoints(300, false);   // level 2

        Assert.Equal(0, core.GetExperienceValue(killerIsAlly: true));   // no XP for allies
        Assert.Equal(40, core.GetExperienceValue(killerIsAlly: false)); // level-2 award
    }

    [Fact]
    public void Xp_HealthBonusMultiplierIsRatio()
    {
        // ZH GameData HealthBonus_* shape: [100%, 110%, 120%, 130%].
        var table = new VeterancyLevelTable(
            [0, 100, 300, 600],
            [10, 20, 40, 80],
            ranks: null,
            healthBonuses: [Fix64.One, Percent(110), Percent(120), Percent(130)]);

        // GPL ActiveBody::onVeterancyLevelChanged: mult = newBonus / oldBonus.
        Assert.Equal(Percent(110), table.HealthBonusMultiplier(0, 1));
        // And back down (setExperienceAndLevel can demote).
        Assert.Equal(Fix64.One, table.HealthBonusMultiplier(1, 1));
        var down = table.HealthBonusMultiplier(1, 0);
        Assert.Equal(Fix64.One / Percent(110), down);
    }

    [Fact]
    public void Xp_XferRoundTripsAndCrcMatches()
    {
        var table = ZhStyleTable();
        var live = new ExperienceCore(table, isTrainable: true)
        {
            ExperienceScalar = Fix64.Half,
            ExperienceSink = new ObjectId(31),
        };
        live.AddExperiencePoints(700, canScaleForBonus: true);   // 350 -> level 2

        var stream = new MemoryStream();
        using (var save = new XferSave(stream, leaveOpen: true))
        {
            live.Xfer(save);
        }

        var shadow = new ExperienceCore(table, isTrainable: true);
        shadow.AddExperiencePoints(50, false);   // differently-stated
        stream.Position = 0;
        using (var load = new XferLoad(stream, leaveOpen: true))
        {
            shadow.Xfer(load);
        }

        Assert.Equal(350, shadow.CurrentExperience);
        Assert.Equal(2, shadow.CurrentLevel);
        Assert.Equal(new ObjectId(31), shadow.ExperienceSink);

        var liveCrc = new XferCrcVisitor();
        live.Xfer(liveCrc);
        var shadowCrc = new XferCrcVisitor();
        shadow.Xfer(shadowCrc);
        Assert.Equal(liveCrc.Value, shadowCrc.Value);

        // Continuation identical.
        var liveChange = live.AddExperiencePoints(500, true);
        var shadowChange = shadow.AddExperiencePoints(500, true);
        Assert.Equal(liveChange.NewLevel, shadowChange.NewLevel);
        Assert.Equal(live.CurrentExperience, shadow.CurrentExperience);
    }

    // ================================================================
    // The BFME2 ExperienceLevel chain -> VeterancyLevelTable compiler,
    // fed by REAL parsed INI on HeadlessSimGame
    // ================================================================

    private const string ExperienceLevelDefinitions = @"
ExperienceLevel TestHordeLevel1
  TargetNames = TestHorde
  RequiredExperience = 1
  ExperienceAward = 0
  Rank = 1
End

ExperienceLevel TestHordeLevel2
  TargetNames = TestHorde
  RequiredExperience = 60
  ExperienceAward = 30
  Rank = 2
End

ExperienceLevel TestHordeLevel3
  TargetNames = TestHorde
  RequiredExperience = 180
  ExperienceAward = 60
  Rank = 3
End
";

    [Fact]
    public void Compiler_BuildsTableFromParsedExperienceLevels()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2, matchSeed: 0xEC0u);
        game.LoadIniText(ExperienceLevelDefinitions);

        // Owner-side extraction: collect the blocks targeting this template.
        var rows = new List<CompiledVeterancyLevel>();
        foreach (var level in game.AssetStore.ExperienceLevels)
        {
            foreach (var target in level.TargetNames)
            {
                if (target == "TestHorde")
                {
                    rows.Add(new CompiledVeterancyLevel
                    {
                        RequiredExperience = level.RequiredExperience,
                        ExperienceAward = level.ExperienceAward,
                        Rank = level.Rank,
                    });
                }
            }
        }
        Assert.Equal(3, rows.Count);

        var compiled = VeterancyTableCompiler.Compile(rows);

        // The chain starts at RequiredExperience 1 (the AotR level-1 convention), so an
        // implicit base level 0 is prepended: 4 levels total.
        Assert.Equal(4, compiled.Table.LevelCount);
        Assert.Equal(0, compiled.Table.GetExperienceRequired(0));
        Assert.Equal(1, compiled.Table.GetExperienceRequired(1));
        Assert.Equal(60, compiled.Table.GetExperienceRequired(2));
        Assert.Equal(180, compiled.Table.GetExperienceRequired(3));
        Assert.Equal(2, compiled.Table.GetRank(2));

        // Progression over the parsed data: a fresh horde is base, hits rank 1 on its
        // first XP, rank 2 at 60.
        var core = new ExperienceCore(compiled.Table, isTrainable: true);
        Assert.Equal(0, core.CurrentRank);
        core.AddExperiencePoints(1, false);
        Assert.Equal(1, core.CurrentRank);
        var change = core.AddExperiencePoints(59, false);
        Assert.True(change.Changed);
        Assert.Equal(2, core.CurrentRank);
        Assert.Equal(30, core.GetExperienceValue(killerIsAlly: false));
    }
}
