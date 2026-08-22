// L4 victory/defeat lane (VD-2) — the deterministic victory core.
//
// Behavioral reference (clean-room, semantics only — no code transcribed):
// generals-gpl GeneralsMD VictoryConditions.cpp
//   reset()                        — clears every latch; default flags = NOBUILDINGS|NOUNITS
//   update()                       — the three phases, in this exact order:
//                                      A single-alliance detection (latch-once, writes endFrame)
//                                      B per-player elimination latch + killPlayer
//                                      C local-player defeat latch
//   hasAchievedVictory / hasBeenDefeated / hasSinglePlayerBeenDefeated
//   cachePlayerPtrs()              — the pool filtering; its tail (no local player =>
//                                    localPlayerDefeated AND observer) is ported here
//   isLocalAlliedVictory / isLocalAlliedDefeat / isLocalDefeat / amIObserver / getEndFrame
// Team.cpp hasAnyBuildings / hasAnyUnits / hasAnyObjects — the liveness sweeps, which live
// behind IVictoryWorld because they touch the world.
//
// Design: workbench research/design-victory-defeat.md §1 (per-member translation ledger,
// verdict PORT/ADAPT/DEFER/DROP), §4 (this [SimState] split and the persistence contract),
// §5 (MatchVerdict), §6 (the three ratified semantics decisions).
//
// Determinism: no float, no GameObject, no Player, no engine type at all. Slot iteration is
// ascending by index and that order is fixed at match start. All mutable state lives in the
// field inventory below and appears in Xfer exactly once, in declaration order.

using System;
using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Victory;

/// <summary>
/// GPL <c>VictoryConditions.h:44-45</c>: which emptiness makes a player eliminated.
/// </summary>
[Flags]
public enum VictoryFlags
{
    /// <summary>No condition set. GPL's dispatch falls through and nobody is ever eliminated.</summary>
    None = 0,

    /// <summary>Eliminated when the player has no live victory <i>structures</i>.</summary>
    NoBuildings = 1,

    /// <summary>Eliminated when the player has no live victory <i>units</i>.</summary>
    NoUnits = 2,
}

/// <summary>
/// The deterministic core of skirmish victory/defeat: GPL <c>VictoryConditions</c> ported
/// against the <see cref="IVictoryWorld"/> seam. Owns every latch and
/// <c>PersistVersion(1)</c>; owns no engine state.
/// </summary>
[SimState]
public sealed class VictoryConditionsCore
{
    /// <summary>GPL MAX_PLAYER_COUNT (GameCommon.h:113). An asserted upper bound, not a baked-in size.</summary>
    public const int MaxPlayerCount = 16;

    /// <summary>
    /// GPL <c>reset()</c>'s default (VictoryConditions.cpp:143) and, per design §6.1 (owner
    /// question O-1), the engine's live default too — deliberately <i>not</i> retail's
    /// <c>GameLogic.cpp:1630</c> override to buildings-only.
    /// </summary>
    public const VictoryFlags DefaultVictoryFlags = VictoryFlags.NoBuildings | VictoryFlags.NoUnits;

    private readonly IVictoryWorld _world;

    // ---- field inventory (Xfer walks these, in this order, and only these) ----
    private VictoryFlags _victoryConditions = DefaultVictoryFlags;
    private int _localSlot = -1;
    private LogicFrame _endFrame;
    private bool _singleAllianceRemaining;
    private bool _localPlayerDefeated;
    private bool _isObserver;
    private readonly List<bool> _isDefeated = new();

    // Appended at the tail (§4: appended fields never interleave). The surviving alliance as
    // of the Phase-A latch, snapshotted rather than recomputed — a winner who is later
    // script-killed stays a winner (§5.2).
    private readonly List<int> _winners = new();

    /// <summary>
    /// GPL's <c>TheRecorder-&gt;isMultiplayer()</c> guard, adapted: the sweep is inert outside
    /// multiplayer/skirmish (§1.3, §6.4). Fixed for the life of the match.
    /// </summary>
    private readonly bool _isMultiplayerMatch;

    public VictoryConditionsCore(IVictoryWorld world, bool isMultiplayerMatch)
    {
        _world = world ?? throw new ArgumentNullException(nameof(world));
        _isMultiplayerMatch = isMultiplayerMatch;
    }

    // ---- readers (GPL :343-367, all PORT verbatim) ----

    /// <summary>GPL <c>m_victoryConditions</c>. The setter is GPL <c>setVictoryConditions</c>.</summary>
    public VictoryFlags VictoryConditions
    {
        get => _victoryConditions;
        set => _victoryConditions = value;
    }

    /// <summary>GPL <c>m_localSlotNum</c>. <c>-1</c> = observer / no local player.</summary>
    public int LocalSlot => _localSlot;

    /// <summary>Cached pool size the last <see cref="Reset"/> sized the latches from.</summary>
    public int PlayerCount => _isDefeated.Count;

    /// <summary>GPL <c>getEndFrame()</c> — the frame the last alliance boundary collapsed. Written once.</summary>
    public LogicFrame EndFrame => _endFrame;

    /// <summary>GPL <c>m_singleAllianceRemaining</c> — the match-decided flag. One-way.</summary>
    public bool SingleAllianceRemaining => _singleAllianceRemaining;

    /// <summary>GPL <c>m_localPlayerDefeated</c> — "prevents condition from being signaled each frame".</summary>
    public bool LocalPlayerDefeatedLatched => _localPlayerDefeated;

    /// <summary>GPL <c>amIObserver()</c>.</summary>
    public bool IsObserver => _isObserver;

    /// <summary>GPL <c>m_isDefeated[i]</c> — the one-way per-slot elimination latch.</summary>
    public bool IsDefeatedLatched(int slot) => InPool(slot) && _isDefeated[slot];

    /// <summary>
    /// The surviving alliance snapshotted at the Phase-A latch, ascending. Empty until the
    /// match is decided, and empty for a mutual wipe (§5.2 <c>Draw</c>).
    /// </summary>
    public IReadOnlyList<int> Winners => _winners;

    /// <summary>Every slot whose elimination latch is set, ascending.</summary>
    public IReadOnlyList<int> DefeatedSlots
    {
        get
        {
            var result = new List<int>();
            for (var i = 0; i < _isDefeated.Count; i++)
            {
                if (_isDefeated[i])
                {
                    result.Add(i);
                }
            }

            return result;
        }
    }

    /// <summary>GPL <c>isLocalAlliedVictory()</c> — false for an observer, else victory of the local slot.</summary>
    public bool IsLocalAlliedVictory => !_isObserver && HasAchievedVictory(_localSlot);

    /// <summary>
    /// GPL <c>isLocalAlliedDefeat()</c>. For an observer this is <c>m_singleAllianceRemaining</c>
    /// — an observer "loses" when the match ends, which is what fires the observer quit screen.
    /// </summary>
    public bool IsLocalAlliedDefeat => _isObserver ? _singleAllianceRemaining : HasBeenDefeated(_localSlot);

    /// <summary>GPL <c>isLocalDefeat()</c> — FALSE for an observer, else the personal-defeat latch.</summary>
    public bool IsLocalDefeat => !_isObserver && _localPlayerDefeated;

    // ---- reset (GPL :129-144, plus the cachePlayerPtrs tail at :333-339) ----

    /// <summary>
    /// GPL <c>reset()</c> with the load-bearing tail of <c>cachePlayerPtrs()</c> folded in: the
    /// pool has already been filtered by the adapter (§1.6's four exclusions), so this call
    /// receives only its size — via <see cref="IVictoryWorld.PlayerCount"/> — and the local
    /// player's slot inside it.
    /// </summary>
    /// <param name="localSlot">
    /// The local player's index in the cached pool, or <c>-1</c> when no local player was
    /// cached. <c>-1</c> latches <c>LocalPlayerDefeated</c> <i>and</i> <c>IsObserver</c>,
    /// exactly as GPL's tail does ("if we have no local player, don't check for defeat").
    /// </param>
    public void Reset(int localSlot)
    {
        var playerCount = _world.PlayerCount;
        if (playerCount < 0 || playerCount > MaxPlayerCount)
        {
            throw new InvalidOperationException(
                $"Victory pool size {playerCount} is outside 0..{MaxPlayerCount} (GPL MAX_PLAYER_COUNT).");
        }

        if (localSlot < -1 || localSlot >= playerCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(localSlot),
                localSlot,
                "Local slot must be -1 (no local player) or an index into the cached pool.");
        }

        _victoryConditions = DefaultVictoryFlags;
        _localSlot = localSlot;
        _endFrame = LogicFrame.Zero;
        _singleAllianceRemaining = false;
        _isDefeated.Clear();
        for (var i = 0; i < playerCount; i++)
        {
            _isDefeated.Add(false);
        }

        _winners.Clear();

        // GPL :333-339 — both flags, together, and only when nothing was cached as local.
        _isObserver = localSlot < 0;
        _localPlayerDefeated = _isObserver;
    }

    // ---- the tick (GPL :147-240) ----

    /// <summary>
    /// GPL <c>update()</c>: the guard, then phases A, B, C in that order. Runs every logic
    /// frame, at the tail of <c>PartitionUpdate</c> (§3.2, wired by VD-5) so the sweep always
    /// sees a post-reap world.
    /// </summary>
    public void Update()
    {
        // GPL :149-150. Inert in single player; inert when there is neither a local player
        // nor observer status (an impossible pair after Reset, ported for faithfulness).
        if (!_isMultiplayerMatch || (_localSlot == -1 && !_isObserver))
        {
            return;
        }

        UpdateSingleAllianceRemaining();
        UpdatePerPlayerElimination();
        UpdateLocalPlayerDefeat();
    }

    /// <summary>
    /// Phase A, GPL :152-184. Latch-once: skipped entirely once the match is decided, so
    /// <see cref="EndFrame"/> is written exactly once per match.
    /// </summary>
    private void UpdateSingleAllianceRemaining()
    {
        if (_singleAllianceRemaining)
        {
            return;
        }

        var alive = -1;
        var multipleAlliances = false;

        for (var i = 0; i < _isDefeated.Count; i++)
        {
            if (HasSinglePlayerBeenDefeated(i))
            {
                continue;
            }

            if (alive < 0)
            {
                alive = i;
                continue;
            }

            // GPL compares every later live slot against the FIRST live slot only, not
            // pairwise. With a non-transitive alliance graph that declares a winner early.
            // Ported as-is, deliberately (§6.3) — connected components is a different
            // algorithm with different end frames.
            if (!_world.AreAllies(alive, i))
            {
                multipleAlliances = true;
                break;
            }
        }

        if (multipleAlliances)
        {
            return;
        }

        _singleAllianceRemaining = true;
        _endFrame = _world.CurrentFrame;

        // The surviving alliance as of endFrame. Taken with the fresh predicate in this same
        // pass, which is what "{ i : !IsDefeated[i] } at the latch" (§5.2) means once Phase B
        // of this very frame has run — the latch is one-way and Phase B latches exactly the
        // slots this pass found defeated. A mutual wipe leaves this empty: that is Draw.
        _winners.Clear();
        for (var i = 0; i < _isDefeated.Count; i++)
        {
            if (!HasSinglePlayerBeenDefeated(i))
            {
                _winners.Add(i);
            }
        }
    }

    /// <summary>
    /// Phase B, GPL :186-223. Latches each newly eliminated slot and fires
    /// <see cref="IVictoryWorld.OnPlayerEliminated"/> (GPL <c>killPlayer</c>) exactly once.
    /// </summary>
    /// <remarks>
    /// GPL's <c>getFrame() &gt; 1</c> guard at :193 gates only the presentation block, which is
    /// DEFER/DROP here — so it gates nothing in this port: a player who starts the match with
    /// nothing is latched defeated on frame 0, silently. Same scope as GPL.
    /// </remarks>
    private void UpdatePerPlayerElimination()
    {
        for (var i = 0; i < _isDefeated.Count; i++)
        {
            if (_isDefeated[i])
            {
                continue;
            }

            if (!HasSinglePlayerBeenDefeated(i))
            {
                continue;
            }

            // The latch is set BEFORE the side effect, so anything reacting to the ensuing
            // destruction already sees a player marked dead (GPL Player.cpp:2067's stated
            // reason: "so OCLs don't ever again spawn useful units for us").
            _isDefeated[i] = true;
            _world.OnPlayerEliminated(i);
        }
    }

    /// <summary>
    /// Phase C, GPL :225-239. Latches the local player's personal defeat once. GPL's
    /// radar force-on and chat-scope change are DEFER/DROP; the popup at :233 is commented out
    /// in retail on purpose (VD-8's banner honours that).
    /// </summary>
    private void UpdateLocalPlayerDefeat()
    {
        if (_localPlayerDefeated || _isObserver || !InPool(_localSlot))
        {
            return;
        }

        // Phase B has already run this frame, so the latch and a fresh predicate call agree;
        // the latch is used because it is the one-way state.
        if (_isDefeated[_localSlot])
        {
            _localPlayerDefeated = true;
        }
    }

    // ---- the predicates (GPL :243-305) ----

    /// <summary>
    /// GPL <c>hasSinglePlayerBeenDefeated(p)</c>, BFME2-adapted (§1.4, §2): the KindOf mask
    /// dispatch is preserved exactly, but each branch asks the world's filter-driven sweep
    /// instead of a <c>KINDOF_MP_COUNT_FOR_VICTORY</c> mask. A slot outside the pool is not
    /// defeated (GPL's null-player skip).
    /// </summary>
    public bool HasSinglePlayerBeenDefeated(int slot)
    {
        if (!InPool(slot))
        {
            return false;
        }

        var noBuildings = (_victoryConditions & VictoryFlags.NoBuildings) != 0;
        var noUnits = (_victoryConditions & VictoryFlags.NoUnits) != 0;

        if (noBuildings && noUnits)
        {
            return !_world.HasAnyVictoryObjects(slot);
        }

        if (noUnits)
        {
            return !_world.HasAnyVictoryUnits(slot);
        }

        if (noBuildings)
        {
            return !_world.HasAnyVictoryStructures(slot);
        }

        // No condition set: GPL's dispatch falls through and returns FALSE.
        return false;
    }

    /// <summary>
    /// GPL <c>hasAchievedVictory(p)</c> (:243-259). False until the match is decided; then true
    /// iff some slot that is still standing is <paramref name="slot"/> itself or a mutual ally
    /// of it.
    /// </summary>
    public bool HasAchievedVictory(int slot)
    {
        if (!_singleAllianceRemaining || !InPool(slot))
        {
            return false;
        }

        for (var i = 0; i < _isDefeated.Count; i++)
        {
            if (_isDefeated[i])
            {
                continue;
            }

            if (i == slot || _world.AreAllies(slot, i))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// GPL <c>hasBeenDefeated(p)</c> (:262-271). This is <i>alliance</i> defeat: true for a
    /// player who is personally still alive but whose alliance lost.
    /// </summary>
    public bool HasBeenDefeated(int slot) =>
        _singleAllianceRemaining && InPool(slot) && !HasAchievedVictory(slot);

    /// <summary>
    /// <c>true</c> when the match decided with nobody left standing (§5.2 <c>Draw</c>): every
    /// slot died on the same frame. GPL reaches this state and reports it as an allied defeat
    /// for the local player; we additionally report it as its own outcome so a harness cannot
    /// mistake a mutual wipe for a win. Sim state is identical — reporting only.
    /// </summary>
    public bool IsMutualWipe => _singleAllianceRemaining && _winners.Count == 0;

    /// <summary>
    /// The §5.2 outcome derivation, from the GPL readers and nothing else.
    /// <c>Draw</c> is tested first because a mutual wipe also satisfies allied defeat.
    /// </summary>
    public MatchOutcome CurrentOutcome
    {
        get
        {
            if (!_singleAllianceRemaining)
            {
                return MatchOutcome.Undecided;
            }

            if (IsMutualWipe)
            {
                return MatchOutcome.Draw;
            }

            if (_isObserver)
            {
                return MatchOutcome.ObserverEnd;
            }

            return IsLocalAlliedVictory ? MatchOutcome.LocalVictory : MatchOutcome.LocalDefeat;
        }
    }

    private bool InPool(int slot) => slot >= 0 && slot < _isDefeated.Count;

    // ---- persistence / checksum (§4: one walk, declaration order, all four visitors) ----

    /// <summary>
    /// <c>PersistVersion(1)</c>, owned by this core. <b>Field order is the CRC contract</b> —
    /// appended fields go at the tail, never interleaved. <c>Player.Persist</c> v8 is NOT
    /// touched: <c>Player.IsDefeated</c> / <c>IsPlayerObserver</c> (VD-3) are in-memory only and
    /// are reconstructed from this walk on load.
    /// </summary>
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);

        xfer.XferEnum("VictoryConditions", ref _victoryConditions);
        xfer.XferInt("LocalSlot", ref _localSlot);
        xfer.XferFrame("EndFrame", ref _endFrame);
        xfer.XferBool("SingleAllianceRemaining", ref _singleAllianceRemaining);
        xfer.XferBool("LocalPlayerDefeated", ref _localPlayerDefeated);
        xfer.XferBool("IsObserver", ref _isObserver);

        xfer.XferList("IsDefeated", _isDefeated, static (IXfer x, ref bool item) =>
        {
            x.XferBool("Value", ref item);
        });

        // Tail append: the Phase-A winners snapshot, which is not recomputable after the fact
        // (Phase B keeps latching defeats after the alliance boundary collapses).
        xfer.XferList("Winners", _winners, static (IXfer x, ref int item) =>
        {
            x.XferInt("Slot", ref item);
        });
    }
}
