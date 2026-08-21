#nullable enable

// S9-06 (R15 L3): the base manager's world types and its pure planner.
//
// WHAT THIS FILE IS
//
// AiBaseManager.cs owns the state machine (one construct in flight, cooldowns, confirmation);
// this file owns everything that is a pure function of a snapshot. Splitting them that way is
// what makes the fill order testable without a clock: BasePlotPlan.Choose takes plots,
// templates and a handful of ints and returns "build THIS template on THAT plot" - no frames,
// no orders, no manager state.
//
// DELIBERATELY HEURISTIC (packet S9-06, and it is the packet's headline caveat)
//
// Retail BFME2 decides castle layout from .bse castle templates: the plot ring, which slot is
// which, and per-faction preferred contents all come out of that data. Reading .bse is packet
// S9-13 and is explicitly NOT a round-1 dependency. Until it lands, "which template may go on
// a plot" is answered by exactly the rule the SIM already enforces - the same rule that makes
// CastleOrderHandler.HandleFoundationConstruct return TemplateNotBuildableOnFoundation:
//
//     the object definition exists AND carries KINDOF NEED_BASE_FOUNDATION
//
// (CastleOrderHandler.cs, the template-validation guard). Using the sim's own acceptance test
// as the AI's candidate filter means the AI can never propose a build the handler will refuse
// for that reason - the two cannot drift, because there is one rule and the sim owns it. Side
// filtering and the economy/producer split on top of it are heuristics, and they are the parts
// S9-13 is expected to replace wholesale.
//
// DETERMINISM
//
// Every list this file consumes is already in a defined order (ascending object id for plots,
// cost-then-ordinal-name for templates - see LiveAiWorldView). Choose() breaks every remaining
// tie explicitly and does no floating-point comparison: costs, counts and thresholds are all
// int, matching AiEconomyManager's int-only rule.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>What a build plot currently is, from the AI's point of view.</summary>
public enum AiPlotKind
{
    /// <summary>An ordinary castle build plot: a free one can take a FoundationConstruct.</summary>
    BuildPlot,

    /// <summary>
    /// A still-packed castle/camp foundation. The AI must unpack this before any plots exist -
    /// unpacking is what stamps the plot ring into the world.
    /// </summary>
    PackedCastle,
}

/// <summary>
/// The AI's per-frame snapshot of one owned castle plot (KINDOF BASE_FOUNDATION object).
/// </summary>
/// <param name="Id">Engine object id. FoundationConstruct / CastleUnpack orders address this.</param>
/// <param name="TemplateName">The plot's own object definition name. Trace text only.</param>
/// <param name="Position">World position of the plot.</param>
/// <param name="Kind">Whether this is a free-standing build plot or a packed castle.</param>
/// <param name="IsOccupied">
/// True when a live non-foundation STRUCTURE already stands on the plot - the same occupancy
/// probe the sim's own guard uses, so "occupied here" means "FoundationOccupied there".
/// </param>
/// <param name="OccupantId">The occupying structure's id, or an invalid id when free.</param>
public readonly record struct AiPlotView(
    ObjectId Id,
    string TemplateName,
    Vector3 Position,
    AiPlotKind Kind,
    bool IsOccupied,
    ObjectId OccupantId)
{
    /// <summary>A plot the AI may issue a FoundationConstruct against right now.</summary>
    public bool IsFreeBuildPlot => Kind == AiPlotKind.BuildPlot && !IsOccupied;

    /// <summary>A castle foundation still waiting to be unpacked.</summary>
    public bool IsPackedCastle => Kind == AiPlotKind.PackedCastle;
}

/// <summary>
/// The role the base manager's fill order sorts on. Derived from KINDOF flags in the mod's own
/// data, never from a hardcoded template-name list - AotR renames buildings freely.
/// </summary>
public enum AiStructureRole
{
    /// <summary>Passive-income building (farm, mine, lumber mill...). Built first.</summary>
    Economy,

    /// <summary>Unit-producing building (barracks, stable, war factory...). Built after economy.</summary>
    Producer,

    /// <summary>Anything else buildable on a plot: walls, wells, defensive towers. Last resort.</summary>
    Other,
}

/// <summary>
/// One structure the AI believes it may place on a free plot.
/// </summary>
/// <param name="DefinitionId">
/// <c>ObjectDefinition.InternalId</c> - the id form
/// <c>Order.CreateFoundationConstruct</c> carries and OrderProcessor resolves.
/// </param>
/// <param name="TemplateName">Definition name, e.g. "MordorSlaughterHouse". Trace text and role matching.</param>
/// <param name="Cost">Build cost as int sim money. Compared against the economy manager's spend plan.</param>
/// <param name="Role">Fill-order bucket. See <see cref="AiStructureRoles"/>.</param>
public readonly record struct AiBuildableTemplate(
    int DefinitionId,
    string TemplateName,
    int Cost,
    AiStructureRole Role);

/// <summary>
/// The single place a set of KINDOF flags becomes an <see cref="AiStructureRole"/>.
/// </summary>
/// <remarks>
/// Split out as a pure function so the classification can be tested without an AssetStore, and
/// so the live view has exactly one rule to call. Flag choice is read off the shipped AotR data
/// (aotr/maps/*/map.ini object blocks), where a farm-shaped building carries
/// <c>ECONOMY_STRUCTURE</c> and/or <c>FS_CASH_PRODUCER</c> and a unit-producing building carries
/// <c>FS_FACTORY</c>. Economy is tested FIRST on purpose: several AotR economy buildings carry
/// <c>FS_FACTORY</c> as well (they sell upgrades), so a producer-first test would classify every
/// farm as a barracks and the AI would never build income.
/// </remarks>
public static class AiStructureRoles
{
    /// <summary>Classifies one template from the three KINDOF facts the roles depend on.</summary>
    /// <param name="isEconomyStructure">KINDOF ECONOMY_STRUCTURE.</param>
    /// <param name="isCashProducer">KINDOF FS_CASH_PRODUCER.</param>
    /// <param name="isFactory">KINDOF FS_FACTORY.</param>
    public static AiStructureRole Classify(bool isEconomyStructure, bool isCashProducer, bool isFactory)
    {
        if (isEconomyStructure || isCashProducer)
        {
            return AiStructureRole.Economy;
        }

        return isFactory ? AiStructureRole.Producer : AiStructureRole.Other;
    }
}

/// <summary>One decision: put <see cref="Template"/> on plot <see cref="PlotId"/>.</summary>
/// <param name="PlotId">The chosen free build plot.</param>
/// <param name="Template">The chosen template.</param>
/// <param name="Reason">Short stable tag for the trace line ("economy", "producer", "fallback").</param>
public readonly record struct BaseBuildChoice(ObjectId PlotId, AiBuildableTemplate Template, string Reason);

/// <summary>
/// The pure half of the base manager: given a snapshot, which structure goes on which plot.
/// </summary>
public static class BasePlotPlan
{
    /// <summary>
    /// Economy buildings to aim for when the mod ships no
    /// <see cref="DifficultyTuning.EconomyMaxFarms"/>. v1 placeholder tuning, not a recovered
    /// retail constant; four is "enough farms that a normal-difficulty AI keeps producing"
    /// without eating a whole castle's plot ring.
    /// </summary>
    public const int DefaultEconomyTarget = 4;

    /// <summary>
    /// The packed castle the AI should unpack first, or null when none is packed.
    /// </summary>
    /// <remarks>
    /// Lowest object id wins. On a normal skirmish start a player owns exactly one packed
    /// castle, so the tie-break only ever matters on hand-built maps that hand a player two;
    /// having one anyway keeps the choice independent of enumeration order.
    /// </remarks>
    public static AiPlotView? FindPackedCastle(IReadOnlyList<AiPlotView> plots)
    {
        if (plots == null)
        {
            return null;
        }

        AiPlotView? best = null;

        for (var i = 0; i < plots.Count; i++)
        {
            var plot = plots[i];

            if (!plot.IsPackedCastle)
            {
                continue;
            }

            if (best is null || plot.Id.Index < best.Value.Id.Index)
            {
                best = plot;
            }
        }

        return best;
    }

    /// <summary>
    /// The free build plot to fill next, or null when every plot is taken. Lowest object id, so
    /// a castle fills in a stable ring order rather than in whatever order the world enumerated.
    /// </summary>
    public static AiPlotView? FindFreePlot(IReadOnlyList<AiPlotView> plots)
    {
        if (plots == null)
        {
            return null;
        }

        AiPlotView? best = null;

        for (var i = 0; i < plots.Count; i++)
        {
            var plot = plots[i];

            if (!plot.IsFreeBuildPlot)
            {
                continue;
            }

            if (best is null || plot.Id.Index < best.Value.Id.Index)
            {
                best = plot;
            }
        }

        return best;
    }

    /// <summary>
    /// Counts the player's own structures (finished or still building) whose template classifies
    /// as <paramref name="role"/>.
    /// </summary>
    /// <remarks>
    /// Matching is by template NAME against the buildable list, because
    /// <see cref="AiObjectView"/> deliberately carries no KINDOF flags (S9-01 kept it a value
    /// snapshot) and this packet does not widen it. Structures whose template is not in the
    /// buildable list - the castle keep itself, captured buildings, map props - simply do not
    /// count towards any role, which is the wanted behaviour: the fill order is about what this
    /// AI has BUILT, not about what it happens to own.
    /// </remarks>
    public static int CountOwnStructures(
        IReadOnlyList<AiObjectView> ownObjects,
        IReadOnlyList<AiBuildableTemplate> templates,
        AiStructureRole role)
    {
        if (ownObjects == null || templates == null)
        {
            return 0;
        }

        var count = 0;

        for (var i = 0; i < ownObjects.Count; i++)
        {
            var own = ownObjects[i];

            if (!own.IsStructure)
            {
                continue;
            }

            for (var t = 0; t < templates.Count; t++)
            {
                if (string.Equals(templates[t].TemplateName, own.TemplateName, StringComparison.OrdinalIgnoreCase))
                {
                    if (templates[t].Role == role)
                    {
                        count++;
                    }

                    break;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// The economy-building target: the mod's <see cref="DifficultyTuning.EconomyMaxFarms"/>
    /// when it shipped a positive one, otherwise <see cref="DefaultEconomyTarget"/>.
    /// </summary>
    public static int EconomyTarget(DifficultyTuning? tuning)
        => tuning is not null && tuning.EconomyMaxFarms > 0 ? tuning.EconomyMaxFarms : DefaultEconomyTarget;

    /// <summary>
    /// The fill-order question: should the next plot get an economy building?
    /// </summary>
    /// <remarks>
    /// "Economy to FarmingThreshold, then producer" (packet S9-06), which is two rules:
    /// <list type="number">
    ///   <item>while the AI owns fewer economy buildings than <see cref="EconomyTarget"/>, build
    ///   economy - that is the ordinary opening;</item>
    ///   <item>while <paramref name="money"/> is under the mod's
    ///   <see cref="SkirmishAIData.FarmingThreshold"/>, build economy regardless of the target.
    ///   BFME2 income is passive farm income (see SpendPlan.cs), so an AI sitting under the
    ///   farming floor needs income more than it needs another barracks it cannot fill.</item>
    /// </list>
    /// Both inputs are int; there is no float comparison anywhere in the fill order. A null
    /// SkirmishAIData contributes no threshold rather than a crash, matching the null policy on
    /// <see cref="IAiWorldView.SkirmishAIData"/>.
    /// </remarks>
    public static bool PrefersEconomy(int money, int economyCount, SkirmishAIData? skirmishAiData, DifficultyTuning? tuning)
    {
        if (economyCount < EconomyTarget(tuning))
        {
            return true;
        }

        return skirmishAiData is not null && money < skirmishAiData.FarmingThreshold;
    }

    /// <summary>
    /// Picks the cheapest template of <paramref name="role"/>, or null when the player's side
    /// has none.
    /// </summary>
    /// <remarks>
    /// Cheapest, not best: an AI that always reaches for the most expensive building it can name
    /// stalls at the affordability gate and never gets a base up, which is exactly the dr-0039
    /// failure. Ties break on ordinal template name so the choice is stable across machines.
    /// </remarks>
    public static AiBuildableTemplate? CheapestOfRole(IReadOnlyList<AiBuildableTemplate> templates, AiStructureRole role)
    {
        if (templates == null)
        {
            return null;
        }

        AiBuildableTemplate? best = null;

        for (var i = 0; i < templates.Count; i++)
        {
            var candidate = templates[i];

            if (candidate.Role != role)
            {
                continue;
            }

            if (best is null
                || candidate.Cost < best.Value.Cost
                || (candidate.Cost == best.Value.Cost
                    && string.CompareOrdinal(candidate.TemplateName, best.Value.TemplateName) < 0))
            {
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>
    /// The whole fill decision: which template goes on which free plot this frame, or null when
    /// there is nothing to do (no free plot, or no buildable template at all).
    /// </summary>
    /// <param name="plots">Owned plots, from <see cref="IAiWorldView.Plots"/>.</param>
    /// <param name="templates">Buildable templates, from <see cref="IAiWorldView.BuildableStructures"/>.</param>
    /// <param name="ownObjects">Owned objects, used to count what is already built.</param>
    /// <param name="money">Current funds, for the FarmingThreshold rule.</param>
    /// <param name="skirmishAiData">Mod tuning, may be null.</param>
    /// <param name="tuning">Difficulty tuning, may be null.</param>
    /// <remarks>
    /// Role fallback is deliberate and one-way: if the preferred role has no template on this
    /// side, the other role is tried, then <see cref="AiStructureRole.Other"/>. A side whose data
    /// only ever yields walls still builds walls rather than standing still - "did nothing" is
    /// the one outcome this packet exists to rule out.
    /// </remarks>
    public static BaseBuildChoice? Choose(
        IReadOnlyList<AiPlotView> plots,
        IReadOnlyList<AiBuildableTemplate> templates,
        IReadOnlyList<AiObjectView> ownObjects,
        int money,
        SkirmishAIData? skirmishAiData,
        DifficultyTuning? tuning)
    {
        var plot = FindFreePlot(plots);
        if (plot is null)
        {
            return null;
        }

        var economyCount = CountOwnStructures(ownObjects, templates, AiStructureRole.Economy);
        var wantsEconomy = PrefersEconomy(money, economyCount, skirmishAiData, tuning);

        var preferred = wantsEconomy ? AiStructureRole.Economy : AiStructureRole.Producer;
        var alternate = wantsEconomy ? AiStructureRole.Producer : AiStructureRole.Economy;

        var pick = CheapestOfRole(templates, preferred);
        var reason = wantsEconomy ? "economy" : "producer";

        if (pick is null)
        {
            pick = CheapestOfRole(templates, alternate);
            reason = "fallback";
        }

        if (pick is null)
        {
            pick = CheapestOfRole(templates, AiStructureRole.Other);
            reason = "fallback";
        }

        if (pick is null)
        {
            return null;
        }

        return new BaseBuildChoice(plot.Value.Id, pick.Value, reason);
    }
}
