// L4 victory/defeat lane (VD-2) — the world seam for the deterministic victory core.
//
// Behavioral reference (clean-room, semantics only — no code transcribed):
// generals-gpl GeneralsMD VictoryConditions.cpp (update / the three predicates /
// cachePlayerPtrs / the convenience readers) and Team.cpp's hasAnyBuildings /
// hasAnyUnits / hasAnyObjects liveness sweeps. Design: workbench
// research/design-victory-defeat.md §1 (translation ledger) and §4 (this split).
//
// The discipline is the one ISimScriptHost established: the [SimState] core
// (VictoryConditionsCore) holds no engine type, and every world read crosses this one
// narrow interface. Slots are plain ints; the slot -> Player mapping is fixed at match
// start and lives in the adapter (VictoryWorldAdapter, VD-3), never here.

using OpenSage.SimCore;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Victory;

/// <summary>
/// The minimum world surface <see cref="VictoryConditionsCore"/> needs — one member per GPL
/// call the core cannot make itself (design-victory-defeat.md §4).
/// </summary>
/// <remarks>
/// Determinism obligation on implementers: every answer must be a pure function of sim state,
/// and every world mutation must run through deterministic engine paths (ascending-ObjectId
/// iteration, the monotonic ObjectId counter). Nothing on this seam is a <c>Player</c>, a
/// <c>GameObject</c>, an <c>ObjectFilter</c>, a position, a money value or a player name.
/// </remarks>
[SimState]
public interface IVictoryWorld
{
    /// <summary>The 5 Hz logic frame (GPL TheGameLogic-&gt;getFrame). Read-only; the core never advances it.</summary>
    LogicFrame CurrentFrame { get; }

    /// <summary>
    /// Size of the cached victory pool — the number of slots after cachePlayerPtrs' four
    /// exclusions (neutral, template-less, civilian, observer; §1.6). Slot indices run
    /// <c>0 .. PlayerCount-1</c> and the order is fixed at match start: it is the CRC contract.
    /// </summary>
    int PlayerCount { get; }

    /// <summary>
    /// GPL <c>hasAnyBuildings(mask)</c>, BFME2-adapted: does this slot own at least one live
    /// object matching <c>VictoryConditionStructureObjectFilter</c>? (§2)
    /// </summary>
    bool HasAnyVictoryStructures(int slot);

    /// <summary>
    /// GPL <c>hasAnyUnits()</c>, BFME2-adapted: does this slot own at least one live object
    /// matching <c>VictoryConditionUnitObjectFilter</c>? (§2)
    /// </summary>
    bool HasAnyVictoryUnits(int slot);

    /// <summary>
    /// GPL <c>hasAnyObjects()</c>, BFME2-adapted: the union of the two victory filters (§6.1).
    /// This is the both-flags branch, and the one the engine defaults to.
    /// </summary>
    bool HasAnyVictoryObjects(int slot);

    /// <summary>
    /// GPL <c>areAllies(p1, p2)</c> (VictoryConditions.cpp:68-76): true only for a
    /// <b>mutual</b> alliance between two <b>distinct</b> slots. The adapter enforces
    /// mutuality (§6.3); the core just asks.
    /// </summary>
    bool AreAllies(int slotA, int slotB);

    /// <summary>
    /// GPL's <c>killPlayer()</c> + defeat-messaging block (§1.5). Fired by the core exactly
    /// once per slot, at the moment that slot's defeat latch is set — never again, even if
    /// the slot's world state later changes.
    /// </summary>
    void OnPlayerEliminated(int slot);
}
