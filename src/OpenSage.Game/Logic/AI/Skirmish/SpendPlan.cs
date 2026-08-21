#nullable enable

// S9-03 (R15 L3): the skirmish AI's income/spend model, v1.
//
// GPL anchor: GeneralsMD AIPlayer.cpp ~:804-842/1727 (shape only - Generals/ZH classify the AI's
// economic mood as Poor/Wealthy off AIData::m_resourcesPoor/m_resourcesWealthy and use it to
// speed up or slow down build timers). BFME2 income is NOT that model: money accrues passively
// every logic frame via each farm's AutoDepositUpdate, there is no harvester round-trip to time.
// So this packet borrows only the SHAPE (a three-state money mood that later managers can read)
// and re-grounds the actual numbers in AIData.Poor/AIData.Wealthy plus the BFME2/RotWK-only
// SkirmishAIData.FarmingThreshold field, which AiEconomyManager folds into the same
// classification (see AiEconomyManager.Classify). Nothing here reads Ghidra or game.dat - this
// is a first-pass model to be refined once the oracle can grade it against retail.
//
// CanAfford is deliberately the ONLY place a spend reserve is enforced. A later manager (S9-06
// base, S9-08/09 team/attack) that wants to know "can I afford this" must go through
// AiEconomyManager.CanAfford / SpendPlan.CanAfford rather than re-deriving a reserve of its own -
// that is what "single reserve policy" means in the packet brief.

using System;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// The three-state money mood the v1 economy model classifies a player into. Wealthy/Poor
/// mirror the Generals/ZH AIPlayer shape (see file header); Normal is everything between.
/// </summary>
public enum EconomyClassification
{
    /// <summary>Money is below the poor floor (AIData.Poor, or SkirmishAIData.FarmingThreshold
    /// when that is the tighter bound) - the plan reserves a cushion.</summary>
    Poor,

    /// <summary>Between the poor and wealthy thresholds. No reserve is held back.</summary>
    Normal,

    /// <summary>Money is above AIData.Wealthy. No reserve is held back.</summary>
    Wealthy,
}

/// <summary>
/// One frame's income/spend snapshot for a skirmish AI player. Immutable: <see
/// cref="AiEconomyManager"/> builds a fresh plan every <see cref="AiEconomyManager.Update"/> and
/// replaces its <see cref="AiEconomyManager.SpendPlan"/> property with it - nothing here is
/// mutated in place.
/// </summary>
/// <param name="Frame">The logic frame this plan was computed on (from <see cref="IAiWorldView.CurrentFrame"/>).</param>
/// <param name="Money">Money at plan time, copied from <see cref="IAiWorldView.Money"/> (already non-negative int).</param>
/// <param name="Classification">The money mood this frame's plan was built under.</param>
/// <param name="Reserve">
/// Non-negative amount of <see cref="Money"/> held back and never reported as spendable. Always
/// less than or equal to <see cref="Money"/> by construction (<see cref="AiEconomyManager"/>
/// never reserves more than the player has).
/// </param>
public readonly record struct SpendPlan(uint Frame, int Money, EconomyClassification Classification, int Reserve)
{
    /// <summary>The plan before any frame has run: no money, no reserve, Normal mood.</summary>
    public static readonly SpendPlan Empty = new(0, 0, EconomyClassification.Normal, 0);

    /// <summary>Money left after the reserve, i.e. what a manager is allowed to plan spending against.</summary>
    public int Available => Money - Reserve;

    /// <summary>
    /// The single reserve check every manager must use instead of comparing against
    /// <see cref="IAiWorldView.Money"/> directly. True when <paramref name="cost"/> fits inside
    /// <see cref="Available"/>.
    /// </summary>
    public bool CanAfford(int cost)
    {
        if (cost < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cost), "A spend cost cannot be negative.");
        }

        return cost <= Available;
    }
}
