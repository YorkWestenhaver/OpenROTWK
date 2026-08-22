// S8 script-engine runtime (subset) — the world seam.
//
// The [SimState] runtime (SimScriptEngine) is world-agnostic: every condition that reads the
// world and every action that changes it goes through this interface. The real adapter
// (SimScriptHostAdapter, non-SimState) implements it over GameLogic; tests may implement it
// directly. Determinism obligation on implementers: every answer must be a pure function of
// sim state, and every world mutation must run through deterministic engine paths
// (ascending-ObjectId iteration, the monotonic ObjectId counter).

using OpenSage.SimCore;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Script;

[SimState]
public interface ISimScriptHost
{
    /// <summary>The 5 Hz logic frame (F6). Read-only; the runtime never advances it.</summary>
    LogicFrame CurrentFrame { get; }

    /// <summary>
    /// Named-unit lookup (GPL TheScriptEngine-&gt;getUnitNamed over the named cache).
    /// Returns false when no object of that name is in the world; when true,
    /// <paramref name="aliveNotDead"/> reports !isEffectivelyDead.
    /// </summary>
    bool TryGetNamedUnit(string name, out bool aliveNotDead);

    /// <summary>True when the named team exists and has no live members (GPL TEAM_DESTROYED).</summary>
    bool IsTeamDestroyed(string teamName);

    /// <summary>True when the named player has no live objects left (GPL PLAYER_ALL_DESTROYED subset).</summary>
    bool IsPlayerAllDestroyed(string playerName);

    /// <summary>
    /// GPL ScriptActions::createUnitOnTeamAt minus the duplicate-name guard (the runtime
    /// performs that guard itself so its bookkeeping stays authoritative): spawn
    /// <paramref name="objectTypeName"/> on team <paramref name="teamName"/> at waypoint
    /// <paramref name="waypointName"/>, naming it <paramref name="unitName"/> (null/empty =
    /// unnamed). Returns true when an object entered the world. Position/orientation are
    /// float substrate on the far side of this seam (D-7 shape).
    /// </summary>
    bool CreateUnitOnTeamAtWaypoint(string unitName, string objectTypeName, string teamName, string waypointName);

    /// <summary>
    /// GPL doAttack: every live member of the attacker team is ordered against the victim
    /// team. Member iteration must be ascending ObjectId.
    /// </summary>
    void TeamAttackTeam(string attackerTeamName, string victimTeamName);

    /// <summary>GPL doNamedAttack: one named unit force-attacks another.</summary>
    void NamedAttackNamed(string attackerName, string victimName);

    /// <summary>
    /// GPL doTransferTeamToPlayer: the named team (and every object on it) changes
    /// ownership to the named player, "maintaining team-ness"; unknown team or player
    /// is a silent no-op.
    /// </summary>
    void TeamTransferToPlayer(string teamName, string playerName);

    /// <summary>
    /// BFME2 MAP_EXIT (content id 496; no GPL reference — observed behavior: the session
    /// ends). The host records the request and ends the session however it sees fit; the
    /// runtime also latches the frame in its own Xfer'd state.
    /// </summary>
    void RequestMapExit();

    /// <summary>
    /// GPL doNamedMoveToWaypoint (MOVE_NAMED_UNIT_TO, ZH id 38): plain move, no combat
    /// engagement. Unknown unit or waypoint is a silent no-op.
    /// </summary>
    void NamedMoveToWaypoint(string unitName, string waypointName);

    /// <summary>
    /// BFME2 ATTACK_MOVE_NAMED_UNIT_TO (content id 546; no GPL ScriptAction case — a
    /// BFME2-only addition, observed behavior inferred from the sibling GPL AI entry point
    /// AIUpdateInterface::privateAttackMoveToPosition / AI_ATTACK_MOVE_TO: move toward the
    /// waypoint, but engage opportunistically along the way and resume once the fight ends.
    /// Unknown unit or waypoint is a silent no-op.
    /// </summary>
    void NamedAttackMoveToWaypoint(string unitName, string waypointName);

    // ---- L4 victory/defeat lane (VD-4) ----
    //
    // The three readers are the GPL VictoryConditions convenience readers, verbatim and
    // uncomposed: MULTIPLAYER_PLAYER_DEFEAT's "and-not" is composed by the runtime, exactly
    // where the original composes it (ScriptConditions::evaluateMultiplayerPlayerDefeat),
    // so this seam stays a set of independent facts about the world.
    //
    // The two requests follow the RequestMapExit pattern: the host records the request and
    // ends/annotates the session however it sees fit (windows and banners are VD-8), while
    // the runtime independently latches the fact and the frame in its own Xfer'd state.

    /// <summary>
    /// GPL <c>TheVictoryConditions-&gt;isLocalAlliedVictory()</c>: a single alliance remains
    /// and the local player is in it. False when there is no local player (observer).
    /// </summary>
    bool IsLocalAlliedVictory { get; }

    /// <summary>
    /// GPL <c>isLocalAlliedDefeat()</c>: the local player's whole alliance has been
    /// eliminated. For an observer this is the "match decided" fact instead.
    /// </summary>
    bool IsLocalAlliedDefeat { get; }

    /// <summary>
    /// GPL <c>isLocalDefeat()</c>: the local player's own defeat latch. False for an
    /// observer. Do NOT pre-compose the and-not against
    /// <see cref="IsLocalAlliedDefeat"/> here — the runtime does that.
    /// </summary>
    bool IsLocalDefeat { get; }

    /// <summary>
    /// GPL doDefeat (ScriptActions.cpp): announce the local player's defeat. Everything the
    /// original does is presentation (Defeat.wnd / ObserverQuit.wnd, input disable, the
    /// end-game timer); a host with no presentation layer may record it and no more.
    /// </summary>
    void RequestDefeat();

    /// <summary>
    /// GPL doLocalDefeat: announce a defeat that is local-only (the match continues for the
    /// rest of the alliance). The original's one sim-visible side effect,
    /// <c>markMPLocalDefeatWindowShown</c>, is latched by the runtime, not by the host.
    /// </summary>
    void RequestLocalDefeat();
}
