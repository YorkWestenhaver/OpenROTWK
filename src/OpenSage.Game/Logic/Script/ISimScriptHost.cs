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
    /// BFME2 MAP_EXIT (content id 496; no GPL reference — observed behavior: the session
    /// ends). The host records the request and ends the session however it sees fit; the
    /// runtime also latches the frame in its own Xfer'd state.
    /// </summary>
    void RequestMapExit();
}
