#nullable enable

// S9-03 (R15 L3): AiEconomyManager - v1 income/spend model.
//
// GPL anchor: GeneralsMD AIPlayer.cpp ~:804-842/1727 (shape only; see SpendPlan.cs file header
// for why BFME2's passive AutoDepositUpdate income means this is not a line-for-line port).
//
// This manager does not spend anything itself and does not submit orders - S9-03 is the income
// side plus the shared afford-check later managers call into. It reads IAiWorldView.Money once
// per tick (already-live, already rising via passive farm income - see
// SkirmishAIBrainSpineTests.Heartbeat_TracksRisingMoney_TheMaEvidence for that contract at the
// spine level) and turns it into a SpendPlan: a money mood (Poor/Normal/Wealthy) plus a reserve.
// Everything is int arithmetic - Money, AIData.Poor/Wealthy and SkirmishAIData.FarmingThreshold
// are all ints, and this file must never grow a float-equality branch over them.

using System.Globalization;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// v1 economy manager: classifies the player's money mood and publishes a <see cref="SpendPlan"/>
/// that every other manager's afford-checks go through.
/// </summary>
public sealed class AiEconomyManager : IAiBrainManager
{
    /// <summary>Trace/report tag. Keep stable - the match report groups evidence on it.</summary>
    public const string ManagerName = "econ";

    /// <summary>
    /// Percent of money held back as a reserve while <see cref="EconomyClassification.Poor"/>.
    /// v1 placeholder tuning (not a GPL or retail constant - BFME2's passive-income economy has
    /// no equivalent to translate); a later oracle round may retune or replace this.
    /// </summary>
    private const int PoorReservePercent = 25;

    public string Name => ManagerName;

    /// <summary>
    /// The most recently computed spend plan. <see cref="Skirmish.SpendPlan.Empty"/> until the
    /// first <see cref="Update"/> call.
    /// </summary>
    public SpendPlan SpendPlan { get; private set; } = SpendPlan.Empty;

    /// <summary>
    /// The single reserve policy: delegates to <see cref="Skirmish.SpendPlan.CanAfford"/> on the
    /// current plan. Other managers call this (or read <see cref="SpendPlan"/> directly) instead
    /// of comparing against <see cref="IAiWorldView.Money"/> themselves.
    /// </summary>
    public bool CanAfford(int cost) => SpendPlan.CanAfford(cost);

    public void Update(SkirmishAIBrain brain)
    {
        var world = brain.World;
        var plan = BuildPlan(world);
        SpendPlan = plan;

        brain.Trace.Line(
            Name,
            string.Create(
                CultureInfo.InvariantCulture,
                $"f={plan.Frame} money={plan.Money} class={Tag(plan.Classification)} reserve={plan.Reserve} avail={plan.Available}"));
    }

    private static SpendPlan BuildPlan(IAiWorldView world)
    {
        var money = world.Money;
        var classification = Classify(money, world.AIData, world.SkirmishAIData);
        var reserve = classification == EconomyClassification.Poor
            ? money * PoorReservePercent / 100
            : 0;

        return new SpendPlan(world.CurrentFrame, money, classification, reserve);
    }

    /// <summary>
    /// Wealthy/Poor classification off <see cref="AIData"/> plus the BFME2/RotWK-only
    /// <see cref="SkirmishAIData.FarmingThreshold"/>. AIData.Poor/Wealthy set the base mood;
    /// FarmingThreshold is folded in as an additional Poor floor - a player who cleared
    /// AIData.Poor but is still under the skirmish data's farming floor still needs more economy,
    /// so is treated as Poor rather than Normal. Absent data (AIData or SkirmishAIData null,
    /// e.g. an INI that never shipped the block) degrades to leaving that input out, never to a
    /// thrown exception or a float-equality branch.
    /// </summary>
    private static EconomyClassification Classify(int money, AIData? aiData, SkirmishAIData? skirmishAiData)
    {
        var poor = aiData is not null && money < aiData.Poor;
        var wealthy = aiData is not null && money > aiData.Wealthy;

        if (skirmishAiData is not null && money < skirmishAiData.FarmingThreshold)
        {
            poor = true;
        }

        if (poor)
        {
            return EconomyClassification.Poor;
        }

        return wealthy ? EconomyClassification.Wealthy : EconomyClassification.Normal;
    }

    private static string Tag(EconomyClassification classification) => classification switch
    {
        EconomyClassification.Poor => "poor",
        EconomyClassification.Wealthy => "wealthy",
        _ => "normal",
    };
}
