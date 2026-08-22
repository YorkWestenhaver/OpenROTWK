// L4 victory/defeat lane (VD-2) — MATCH-VERDICT-V1, the machine-decidable match result.
//
// Schema FROZEN at R1 close in workbench research/design-victory-defeat.md §5 (blackboard
// VD-1 #3). Any change after that is a MATCH-VERDICT-V2 prefix, never a silent edit.
//
// This file declares no [SimState] type on purpose: the verdict carries diagnostic player
// NAMES and is a reporting shape, not sim state. The sim state it summarises lives entirely
// in VictoryConditionsCore, and every field here is derived from that core's GPL readers.
// The wire form (one stdout line + the optional OPENSAGE_VERDICT_OUT sink) is VD-6's;
// this is the record it serialises.

using System;
using System.Collections.Generic;

namespace OpenSage.Logic.Victory;

/// <summary>§5.1. Derived only from the GPL readers — see <see cref="MatchVerdict.From"/>.</summary>
public enum MatchOutcome
{
    /// <summary>The match has not decided. The only outcome valid alongside <c>endFrame = 0</c>.</summary>
    Undecided,

    /// <summary>GPL <c>isLocalAlliedVictory()</c>.</summary>
    LocalVictory,

    /// <summary>GPL <c>isLocalAlliedDefeat()</c> for a non-observer.</summary>
    LocalDefeat,

    /// <summary>Observer, and the match decided — GPL's observer quit path.</summary>
    ObserverEnd,

    /// <summary>
    /// The match decided with zero slots left standing (everyone died on the same frame).
    /// GPL reaches this state and reports it as an allied defeat; we name it, so a harness
    /// cannot mistake a mutual wipe for a win. Reporting-only divergence (§5.2).
    /// </summary>
    Draw,
}

/// <summary>§5.1. Which terminal event ended the match. The <i>first</i> one wins.</summary>
public enum MatchEndReason
{
    /// <summary>No terminal event yet.</summary>
    NotEnded,

    /// <summary>The victory sweep's alliance boundary collapsed.</summary>
    Elimination,

    /// <summary>A script ran <c>ScriptActionType::DEFEAT</c> (VD-4).</summary>
    ScriptDefeat,

    /// <summary>A script ran <c>ScriptActionType::LOCALDEFEAT</c> (VD-4).</summary>
    ScriptLocalDefeat,

    /// <summary>
    /// The existing <c>SimScriptEngine.MapExitRequested</c> path. Always reported with
    /// <see cref="MatchOutcome.Undecided"/> so an <c>--until-frame</c> / map-exit run is never
    /// mistaken for a decided match.
    /// </summary>
    MapExit,
}

/// <summary>
/// The frozen MATCH-VERDICT-V1 record (design-victory-defeat.md §5.1). Emitted exactly once
/// per match, at the latch, by VD-6's reporter.
/// </summary>
/// <param name="Schema">Always <see cref="SchemaId"/>.</param>
/// <param name="Outcome">§5.2, derived only from the GPL readers.</param>
/// <param name="Reason">Which terminal event ended the match.</param>
/// <param name="EndFrame">The logic frame the alliance boundary collapsed (GPL <c>m_endFrame</c>). Written once, never revised.</param>
/// <param name="LocalSlot"><c>-1</c> when observer / no local player.</param>
/// <param name="Winners">Slot indices of the surviving alliance at <paramref name="EndFrame"/>, ascending; empty if none.</param>
/// <param name="Defeated">Slot indices latched defeated, ascending.</param>
/// <param name="PlayerNames">Index-aligned to slots; diagnostic only, never keyed on.</param>
/// <param name="Observer">Whether the local viewpoint is an observer.</param>
public sealed record MatchVerdict(
    string Schema,
    MatchOutcome Outcome,
    MatchEndReason Reason,
    uint EndFrame,
    int LocalSlot,
    IReadOnlyList<int> Winners,
    IReadOnlyList<int> Defeated,
    IReadOnlyList<string> PlayerNames,
    bool Observer)
{
    /// <summary>The frozen schema id, and the grep key VD-6 prefixes its stdout line with.</summary>
    public const string SchemaId = "MATCH-VERDICT-V1";

    /// <summary>
    /// Build the verdict for a core's current state. <paramref name="playerNames"/> is
    /// index-aligned to the core's slots and is diagnostic only; a shorter or absent list is
    /// padded, never keyed on.
    /// </summary>
    /// <remarks>
    /// <paramref name="reason"/> records <i>which</i> terminal event fired first; the outcome is
    /// still derived only from the GPL readers (§5.2). A script-driven end
    /// (<see cref="MatchEndReason.ScriptDefeat"/> / <see cref="MatchEndReason.ScriptLocalDefeat"/>)
    /// that has not also collapsed the alliance boundary therefore reports
    /// <see cref="MatchOutcome.Undecided"/> — GPL's <c>doDefeat</c>/<c>doLocalDefeat</c> genuinely
    /// do not touch <c>VictoryConditions</c> (§1.8), so the sim state says the match is still
    /// contested. VD-4/VD-6 own how that pairing surfaces; VD-2 does not invent an outcome for it.
    /// </remarks>
    public static MatchVerdict From(
        VictoryConditionsCore core,
        MatchEndReason reason,
        IReadOnlyList<string> playerNames)
    {
        ArgumentNullException.ThrowIfNull(core);

        var names = new List<string>(core.PlayerCount);
        for (var i = 0; i < core.PlayerCount; i++)
        {
            names.Add(playerNames != null && i < playerNames.Count ? playerNames[i] : string.Empty);
        }

        var outcome = reason == MatchEndReason.MapExit ? MatchOutcome.Undecided : core.CurrentOutcome;

        return new MatchVerdict(
            SchemaId,
            outcome,
            reason,
            core.EndFrame.Value,
            core.LocalSlot,
            new List<int>(core.Winners),
            new List<int>(core.DefeatedSlots),
            names,
            core.IsObserver);
    }
}
