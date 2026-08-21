#nullable enable

// S9-06 (R15 L3): tests for BasePlotPlan, the pure half of AiBaseManager.
//
// Class name deliberately starts with AiBaseManager so the packet's single scoped filter
// (FullyQualifiedName~AiBaseManager) covers the planner as well as the state machine - one
// filter, one gate, no way to run half the packet's evidence by accident.
//
// Nothing here touches a frame, an order or a brain: these are functions of a snapshot, and
// that is exactly what makes the fill order reviewable.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Reflection;
using OpenSage.Logic.AI;
using OpenSage.Logic.AI.Skirmish;
using Xunit;

namespace OpenSage.Tests.Logic.AI.Skirmish;

public class AiBaseManagerPlanTests
{
    private static AiPlotView Plot(uint id, bool occupied = false)
        => new(new ObjectId(id), "CastlePlot", Vector3.Zero, AiPlotKind.BuildPlot, occupied, ObjectId.Invalid);

    private static AiPlotView PackedCastle(uint id)
        => new(new ObjectId(id), "CastleFoundation", Vector3.Zero, AiPlotKind.PackedCastle, false, ObjectId.Invalid);

    private static AiBuildableTemplate Template(int defId, string name, int cost, AiStructureRole role)
        => new(defId, name, cost, role);

    private static AiObjectView Structure(uint id, string name, bool isStructure = true)
        => new(new ObjectId(id), name, Vector3.Zero, 0, isStructure, false, 1.0f);

    private static SkirmishAIData MakeSkirmishAiData(int farmingThreshold)
    {
        var data = new SkirmishAIData();
        SetPrivate(data, nameof(SkirmishAIData.FarmingThreshold), farmingThreshold);
        return data;
    }

    private static DifficultyTuning MakeTuning(int economyMaxFarms)
    {
        var tuning = new DifficultyTuning();
        SetPrivate(tuning, nameof(DifficultyTuning.EconomyMaxFarms), economyMaxFarms);
        return tuning;
    }

    private static void SetPrivate(object target, string propertyName, object value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (property is null)
        {
            throw new InvalidOperationException($"Property '{propertyName}' not found on {target.GetType()}.");
        }

        property.SetValue(target, value);
    }

    // ---- role classification -------------------------------------------------------------

    [Fact]
    public void AnEconomyStructure_IsEconomy()
    {
        Assert.Equal(
            AiStructureRole.Economy,
            AiStructureRoles.Classify(isEconomyStructure: true, isCashProducer: false, isFactory: false));
    }

    [Fact]
    public void ACashProducer_IsEconomy()
    {
        Assert.Equal(
            AiStructureRole.Economy,
            AiStructureRoles.Classify(isEconomyStructure: false, isCashProducer: true, isFactory: false));
    }

    [Fact]
    public void AFactory_IsAProducer()
    {
        Assert.Equal(
            AiStructureRole.Producer,
            AiStructureRoles.Classify(isEconomyStructure: false, isCashProducer: false, isFactory: true));
    }

    [Fact]
    public void AnEconomyBuildingThatIsAlsoAFactory_IsStillEconomy()
    {
        // Real AotR data: several ECONOMY_STRUCTURE / FS_CASH_PRODUCER buildings also carry
        // FS_FACTORY because they sell upgrades. Producer-first classification would file every
        // farm as a barracks and the AI would never build income.
        Assert.Equal(
            AiStructureRole.Economy,
            AiStructureRoles.Classify(isEconomyStructure: true, isCashProducer: true, isFactory: true));
    }

    [Fact]
    public void SomethingThatIsNeitherIsOther()
    {
        Assert.Equal(
            AiStructureRole.Other,
            AiStructureRoles.Classify(isEconomyStructure: false, isCashProducer: false, isFactory: false));
    }

    // ---- plot selection -------------------------------------------------------------------

    [Fact]
    public void FindPackedCastle_TakesTheLowestId()
    {
        var plots = new List<AiPlotView> { PackedCastle(9), Plot(2), PackedCastle(4) };

        Assert.Equal(new ObjectId(4), BasePlotPlan.FindPackedCastle(plots)!.Value.Id);
    }

    [Fact]
    public void FindPackedCastle_IsNullWhenNothingIsPacked()
    {
        Assert.Null(BasePlotPlan.FindPackedCastle(new List<AiPlotView> { Plot(2), Plot(3) }));
        Assert.Null(BasePlotPlan.FindPackedCastle(new List<AiPlotView>()));
        Assert.Null(BasePlotPlan.FindPackedCastle(null!));
    }

    [Fact]
    public void FindFreePlot_SkipsOccupiedPlotsAndPackedCastles()
    {
        var plots = new List<AiPlotView> { Plot(2, occupied: true), PackedCastle(3), Plot(8), Plot(5) };

        Assert.Equal(new ObjectId(5), BasePlotPlan.FindFreePlot(plots)!.Value.Id);
    }

    [Fact]
    public void FindFreePlot_IsNullWhenEveryPlotIsTaken()
    {
        Assert.Null(BasePlotPlan.FindFreePlot(new List<AiPlotView> { Plot(2, occupied: true) }));
        Assert.Null(BasePlotPlan.FindFreePlot(null!));
    }

    // ---- template selection ----------------------------------------------------------------

    [Fact]
    public void CheapestOfRole_TakesTheCheapestOfThatRoleOnly()
    {
        var templates = new List<AiBuildableTemplate>
        {
            Template(1, "PricyFarm", 900, AiStructureRole.Economy),
            Template(2, "CheapBarracks", 100, AiStructureRole.Producer),
            Template(3, "CheapFarm", 300, AiStructureRole.Economy),
        };

        Assert.Equal(3, BasePlotPlan.CheapestOfRole(templates, AiStructureRole.Economy)!.Value.DefinitionId);
        Assert.Equal(2, BasePlotPlan.CheapestOfRole(templates, AiStructureRole.Producer)!.Value.DefinitionId);
        Assert.Null(BasePlotPlan.CheapestOfRole(templates, AiStructureRole.Other));
    }

    [Fact]
    public void CheapestOfRole_BreaksCostTiesOnOrdinalName()
    {
        var templates = new List<AiBuildableTemplate>
        {
            Template(1, "Zebra", 300, AiStructureRole.Economy),
            Template(2, "Alpha", 300, AiStructureRole.Economy),
        };

        Assert.Equal("Alpha", BasePlotPlan.CheapestOfRole(templates, AiStructureRole.Economy)!.Value.TemplateName);
    }

    // ---- fill order ------------------------------------------------------------------------

    [Fact]
    public void EconomyTarget_PrefersTheModsFarmCap()
    {
        Assert.Equal(7, BasePlotPlan.EconomyTarget(MakeTuning(7)));
    }

    [Fact]
    public void EconomyTarget_FallsBackToTheDefault_WhenTheModShippedNone()
    {
        Assert.Equal(BasePlotPlan.DefaultEconomyTarget, BasePlotPlan.EconomyTarget(null));
        Assert.Equal(BasePlotPlan.DefaultEconomyTarget, BasePlotPlan.EconomyTarget(MakeTuning(0)));
    }

    [Fact]
    public void PrefersEconomy_UntilTheTargetIsMet()
    {
        var tuning = MakeTuning(3);

        Assert.True(BasePlotPlan.PrefersEconomy(money: 99_999, economyCount: 2, null, tuning));
        Assert.False(BasePlotPlan.PrefersEconomy(money: 99_999, economyCount: 3, null, tuning));
    }

    [Fact]
    public void PrefersEconomy_AgainWhenMoneyIsUnderTheFarmingThreshold()
    {
        var tuning = MakeTuning(1);
        var data = MakeSkirmishAiData(farmingThreshold: 5_000);

        Assert.True(BasePlotPlan.PrefersEconomy(money: 4_999, economyCount: 9, data, tuning));
        Assert.False(BasePlotPlan.PrefersEconomy(money: 5_000, economyCount: 9, data, tuning));
    }

    [Fact]
    public void PrefersEconomy_TreatsMissingDataAsNoThreshold()
    {
        Assert.False(BasePlotPlan.PrefersEconomy(money: 0, economyCount: 99, null, MakeTuning(1)));
    }

    // ---- counting what is already built -----------------------------------------------------

    [Fact]
    public void CountOwnStructures_CountsOnlyStructuresWithAKnownTemplate()
    {
        var templates = new List<AiBuildableTemplate>
        {
            Template(1, "Farm", 300, AiStructureRole.Economy),
            Template(2, "Barracks", 400, AiStructureRole.Producer),
        };

        var own = new List<AiObjectView>
        {
            Structure(1, "Farm"),
            Structure(2, "farm"),                      // case-insensitive template match
            Structure(3, "Barracks"),
            Structure(4, "CastleKeep"),                // not buildable: counts for nothing
            Structure(5, "Farm", isStructure: false),  // a unit named like a building: ignored
        };

        Assert.Equal(2, BasePlotPlan.CountOwnStructures(own, templates, AiStructureRole.Economy));
        Assert.Equal(1, BasePlotPlan.CountOwnStructures(own, templates, AiStructureRole.Producer));
        Assert.Equal(0, BasePlotPlan.CountOwnStructures(own, templates, AiStructureRole.Other));
        Assert.Equal(0, BasePlotPlan.CountOwnStructures(null!, templates, AiStructureRole.Economy));
    }

    // ---- the whole decision -----------------------------------------------------------------

    [Fact]
    public void Choose_PutsTheCheapestEconomyBuildingOnTheLowestFreePlot()
    {
        var plots = new List<AiPlotView> { Plot(9), Plot(3) };
        var templates = new List<AiBuildableTemplate>
        {
            Template(1, "PricyFarm", 900, AiStructureRole.Economy),
            Template(2, "CheapFarm", 300, AiStructureRole.Economy),
            Template(3, "Barracks", 50, AiStructureRole.Producer),
        };

        var choice = BasePlotPlan.Choose(plots, templates, new List<AiObjectView>(), 10_000, null, null);

        Assert.NotNull(choice);
        Assert.Equal(new ObjectId(3), choice!.Value.PlotId);
        Assert.Equal("CheapFarm", choice.Value.Template.TemplateName);
        Assert.Equal("economy", choice.Value.Reason);
    }

    [Fact]
    public void Choose_SwitchesToProducer_OnceTheEconomyTargetIsMet()
    {
        var plots = new List<AiPlotView> { Plot(3) };
        var templates = new List<AiBuildableTemplate>
        {
            Template(1, "Farm", 300, AiStructureRole.Economy),
            Template(2, "Barracks", 400, AiStructureRole.Producer),
        };
        var own = new List<AiObjectView> { Structure(1, "Farm"), Structure(2, "Farm") };

        var choice = BasePlotPlan.Choose(plots, templates, own, 10_000, null, MakeTuning(2));

        Assert.Equal("Barracks", choice!.Value.Template.TemplateName);
        Assert.Equal("producer", choice.Value.Reason);
    }

    [Fact]
    public void Choose_FallsBackToTheOtherRole_WhenTheSideHasNoneOfThePreferredOne()
    {
        var plots = new List<AiPlotView> { Plot(3) };
        var templates = new List<AiBuildableTemplate> { Template(2, "Barracks", 400, AiStructureRole.Producer) };

        var choice = BasePlotPlan.Choose(plots, templates, new List<AiObjectView>(), 10_000, null, null);

        Assert.Equal("Barracks", choice!.Value.Template.TemplateName);
        Assert.Equal("fallback", choice.Value.Reason);
    }

    [Fact]
    public void Choose_FallsBackAllTheWayToOther_RatherThanStandingStill()
    {
        var plots = new List<AiPlotView> { Plot(3) };
        var templates = new List<AiBuildableTemplate> { Template(2, "Wall", 100, AiStructureRole.Other) };

        var choice = BasePlotPlan.Choose(plots, templates, new List<AiObjectView>(), 10_000, null, null);

        Assert.Equal("Wall", choice!.Value.Template.TemplateName);
        Assert.Equal("fallback", choice.Value.Reason);
    }

    [Fact]
    public void Choose_IsNullWithNoFreePlotOrNoTemplate()
    {
        var templates = new List<AiBuildableTemplate> { Template(1, "Farm", 300, AiStructureRole.Economy) };

        Assert.Null(BasePlotPlan.Choose(
            new List<AiPlotView> { Plot(3, occupied: true) }, templates, new List<AiObjectView>(), 10_000, null, null));

        Assert.Null(BasePlotPlan.Choose(
            new List<AiPlotView> { Plot(3) }, new List<AiBuildableTemplate>(), new List<AiObjectView>(), 10_000, null, null));
    }

    [Fact]
    public void Choose_IgnoresMoney_TheAffordGateBelongsToTheManager()
    {
        // The planner is deliberately price-blind beyond the FarmingThreshold rule: whether the
        // AI can pay is AiEconomyManager's single reserve policy (S9-03), and duplicating it here
        // would give the AI two disagreeing wallets.
        var plots = new List<AiPlotView> { Plot(3) };
        var templates = new List<AiBuildableTemplate> { Template(1, "Farm", 300, AiStructureRole.Economy) };

        var choice = BasePlotPlan.Choose(plots, templates, new List<AiObjectView>(), 0, null, null);

        Assert.Equal("Farm", choice!.Value.Template.TemplateName);
    }
}
