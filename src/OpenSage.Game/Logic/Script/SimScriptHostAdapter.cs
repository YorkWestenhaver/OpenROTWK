// S8 script-engine runtime (subset) — the real-world host (deliberately NOT [SimState]).
//
// Implements ISimScriptHost over the actual engine: named-unit lookup through GameLogic's
// name table, spawning through GameLogic.CreateObject (monotonic ObjectId — spawn order IS
// the determinism guarantee), attacks through the S1 weapon path, waypoints/teams from
// small registries the map loader (or a test) fills in.
//
// Float crossings live HERE (D-7 shape): waypoint positions are float substrate, applied via
// UpdateTransform on the far side of the seam; the [SimState] runtime only ever passes names.
//
// Team model note (recorded finding SR-F2): the original resolves teams through
// TheTeamFactory prototypes and per-team instance lists, and a LAN lobby rewrites the map's
// player model (worldbuilder-semantics.md). This adapter keeps its own name->members
// registry instead of the legacy TeamFactory: deterministic (List append order, ascending-
// ObjectId iteration for orders), sufficient for the scenariogen surface, and trivially
// replaceable when a real team system ports.

using System;
using System.Collections.Generic;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Script;

public sealed class SimScriptHostAdapter : ISimScriptHost
{
    private readonly IGame _game;
    private readonly GameLogic _gameLogic;
    private readonly Player _defaultTeamOwner;

    private readonly List<(string Name, Vector3 Position)> _waypoints = new();
    private readonly List<TeamEntry> _teams = new();

    private sealed class TeamEntry
    {
        public string Name;
        public Player Owner;
        public readonly List<ObjectId> Members = new();
    }

    public SimScriptHostAdapter(IGame game, Player defaultTeamOwner)
    {
        _game = game ?? throw new ArgumentNullException(nameof(game));
        _gameLogic = (GameLogic)game.GameLogic;
        _defaultTeamOwner = defaultTeamOwner ?? throw new ArgumentNullException(nameof(defaultTeamOwner));
    }

    /// <summary>True once a MAP_EXIT action ran this session.</summary>
    public bool MapExitRequested { get; private set; }

    public LogicFrame CurrentFrame => _gameLogic.CurrentFrame;

    // ---- registration (map loader / tests) ----

    public void RegisterWaypoint(string name, in Vector3 position)
    {
        _waypoints.Add((name, position));
    }

    public void RegisterTeam(string teamName, Player owner)
    {
        if (FindTeam(teamName) == null)
        {
            _teams.Add(new TeamEntry { Name = teamName, Owner = owner });
        }
    }

    public void RegisterTeamMember(string teamName, GameObject gameObject)
    {
        var team = FindTeam(teamName) ?? CreateTeam(teamName);
        team.Members.Add(gameObject.Id);
    }

    // ---- ISimScriptHost ----

    public bool TryGetNamedUnit(string name, out bool aliveNotDead)
    {
        aliveNotDead = false;
        if (string.IsNullOrEmpty(name) || !_gameLogic.TryGetObjectByName(name, out var gameObject) || gameObject == null)
        {
            return false;
        }

        aliveNotDead = !gameObject.IsEffectivelyDead && !gameObject.IsDestroyed;
        return true;
    }

    public bool IsTeamDestroyed(string teamName)
    {
        var team = FindTeam(teamName);
        if (team == null)
        {
            return false; // unknown team is not "destroyed" (GPL: null team fails the check)
        }

        foreach (var id in team.Members)
        {
            var member = _gameLogic.GetObjectById(id);
            if (member != null && !member.IsEffectivelyDead && !member.IsDestroyed)
            {
                return false;
            }
        }

        return true;
    }

    public bool IsPlayerAllDestroyed(string playerName)
    {
        var player = _game.PlayerManager.GetPlayerByName(playerName);
        if (player == null)
        {
            return false;
        }

        foreach (var gameObject in _gameLogic.Objects)
        {
            if (gameObject.Owner == player && !gameObject.IsEffectivelyDead && !gameObject.IsDestroyed)
            {
                return false;
            }
        }

        return true;
    }

    public bool CreateUnitOnTeamAtWaypoint(string unitName, string objectTypeName, string teamName, string waypointName)
    {
        var definition = _game.AssetStore.ObjectDefinitions.GetByName(objectTypeName);
        if (definition == null)
        {
            return false; // GPL: template not found -> warn + no spawn
        }

        var team = FindTeam(teamName) ?? CreateTeam(teamName); // GPL: missing team is created
        var gameObject = _gameLogic.CreateObject(definition, team.Owner);
        if (gameObject == null)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(unitName))
        {
            gameObject.AssignScriptName(unitName);
        }

        if (TryGetWaypoint(waypointName, out var position))
        {
            gameObject.UpdateTransform(position);
            gameObject.UpdateColliders();
        }

        team.Members.Add(gameObject.Id);
        return true;
    }

    public void TeamAttackTeam(string attackerTeamName, string victimTeamName)
    {
        var attackers = FindTeam(attackerTeamName);
        var victims = FindTeam(victimTeamName);
        if (attackers == null || victims == null)
        {
            return; // GPL doAttack sanity bail
        }

        // Victim pick: the lowest-ObjectId live member (recorded deviation SR-D4 — the
        // original AI group picks per-attacker victims through its own logic; a fixed
        // total-order pick keeps the draw count untouched until an AI system exists).
        var victim = FirstLiveMember(victims);
        if (victim == null)
        {
            return;
        }

        foreach (var id in MembersAscending(attackers))
        {
            var attacker = _gameLogic.GetObjectById(id);
            if (attacker == null || attacker.IsEffectivelyDead || attacker.IsDestroyed)
            {
                continue;
            }

            OrderAttack(attacker, victim);
        }
    }

    public void NamedAttackNamed(string attackerName, string victimName)
    {
        if (!_gameLogic.TryGetObjectByName(attackerName, out var attacker) || attacker == null ||
            !_gameLogic.TryGetObjectByName(victimName, out var victim) || victim == null)
        {
            return;
        }

        OrderAttack(attacker, victim);
    }

    public void RequestMapExit()
    {
        MapExitRequested = true;
    }

    // ---- internals ----

    private void OrderAttack(GameObject attacker, GameObject victim)
    {
        // The same path the order pipe walks for MSG_DO_ATTACK_OBJECT (OrderProcessor):
        // point the current weapon at the victim. AI approach/pursuit is the S9 system;
        // in-range attackers fight through the full S1 pipeline today.
        if (attacker.CanAttack)
        {
            attacker.CurrentWeapon?.SetTarget(new WeaponTarget(_gameLogic, victim.Id));
        }
    }

    private TeamEntry FindTeam(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (var team in _teams)
        {
            if (string.Equals(team.Name, name, StringComparison.Ordinal))
            {
                return team;
            }
        }

        return null;
    }

    private TeamEntry CreateTeam(string name)
    {
        var team = new TeamEntry { Name = name, Owner = _defaultTeamOwner };
        _teams.Add(team);
        return team;
    }

    private GameObject FirstLiveMember(TeamEntry team)
    {
        GameObject best = null;
        foreach (var id in MembersAscending(team))
        {
            var member = _gameLogic.GetObjectById(id);
            if (member != null && !member.IsEffectivelyDead && !member.IsDestroyed)
            {
                best = member;
                break;
            }
        }

        return best;
    }

    private static IEnumerable<ObjectId> MembersAscending(TeamEntry team)
    {
        var ids = new List<ObjectId>(team.Members);
        ids.Sort(static (a, b) => a.Index.CompareTo(b.Index));
        return ids;
    }

    private bool TryGetWaypoint(string name, out Vector3 position)
    {
        position = default;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var (waypointName, waypointPosition) in _waypoints)
        {
            if (string.Equals(waypointName, name, StringComparison.Ordinal))
            {
                position = waypointPosition;
                return true;
            }
        }

        return false;
    }
}
