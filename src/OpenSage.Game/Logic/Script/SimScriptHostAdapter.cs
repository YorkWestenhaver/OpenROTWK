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
using OpenSage.Logic.Object.Horde;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Script;

public sealed class SimScriptHostAdapter : ISimScriptHost
{
    private readonly IGame _game;
    private readonly GameLogic _gameLogic;
    private readonly Player _defaultTeamOwner;

    private readonly List<(string Name, Vector3 Position)> _waypoints = new();
    private readonly List<TeamEntry> _teams = new();
    private readonly List<(string Attacker, string Victim)> _attackOrders = new();

    /// <summary>Members' own combat drives them once released; locomotor clamps to its max.</summary>
    private static readonly Fix64 AttackMoveSpeedSentinel = Fix64.FromDecimalLiteral("99999");

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

        // GPL semantics: a scripted attack forces hostility whatever the map authored —
        // TEAM_ATTACK_TEAM works between nominally neutral scenario sides (job-009's
        // creeps-vs-civilian pairing relies on it).
        if (attackers.Owner != victims.Owner)
        {
            attackers.Owner.AddEnemy(victims.Owner);
            victims.Owner.AddEnemy(attackers.Owner);
        }

        // Horde containers are driven per-frame by TickCombat (approach + member melee);
        // the standing order is what survives across frames.
        if (!_attackOrders.Contains((attackers.Name, victims.Name)))
        {
            _attackOrders.Add((attackers.Name, victims.Name));
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

            if (attacker.FindBehavior<SimHordeContain>() == null)
            {
                OrderAttack(attacker, victim);
            }
        }
    }

    /// <summary>
    /// Per-frame combat drive for the standing TEAM_ATTACK_TEAM orders (SimMapRun calls
    /// this each StepFrame). The AIUpdate family is deliberately unfrozen (api-freeze-v1
    /// §7), so the harness supplies the minimal HordeAIUpdate shape here: each attacking
    /// horde marches on the victim team's lead object until its rangefinder range, flips
    /// the S6 melee mux, and its members fight through the real S1 weapon pipeline
    /// (retargeting to the nearest live enemy member as targets die). Weapon state
    /// machines tick here because the headless host has no Scene3D.LogicTick.
    /// </summary>
    public void TickCombat()
    {
        foreach (var (attackerName, victimName) in _attackOrders)
        {
            var attackers = FindTeam(attackerName);
            var victims = FindTeam(victimName);
            if (attackers == null || victims == null)
            {
                continue;
            }

            var victimLead = FirstLiveMember(victims);

            foreach (var id in MembersAscending(attackers))
            {
                var attacker = _gameLogic.GetObjectById(id);
                if (attacker == null || attacker.IsEffectivelyDead || attacker.IsDestroyed)
                {
                    continue;
                }

                var horde = attacker.FindBehavior<SimHordeContain>();
                if (horde == null)
                {
                    continue;
                }

                TickHordeAttack(attacker, horde, victims, victimLead);
            }
        }
    }

    private void TickHordeAttack(GameObject attacker, SimHordeContain horde, TeamEntry victims, GameObject victimLead)
    {
        var mover = attacker.FindBehavior<SimLocomotorUpdate>();

        if (victimLead == null)
        {
            horde.SetMeleeAttacking(false);
            if (mover != null && mover.Mode == SimMoveMode.MoveToPosition)
            {
                mover.Stop();
            }
            return;
        }

        // Approach-vs-melee by the horde's own rangefinder (NormalMeleeHordeRangefinder
        // range 12): outside it the horde marches on the victim; inside, the S6 melee mux
        // releases the configured ranks and members fight in place.
        var range = attacker.CurrentWeapon?.Template.AttackRange ?? 0;
        var distance = Vector3.Distance(attacker.Translation, victimLead.Translation);
        if (distance > range && mover != null)
        {
            horde.SetMeleeAttacking(false);
            mover.SetTargetPosition(SimTransformBridge.PullPosition(victimLead), AttackMoveSpeedSentinel);
        }
        else
        {
            horde.SetMeleeAttacking(true);
            if (mover != null && mover.Mode == SimMoveMode.MoveToPosition)
            {
                mover.Stop();
            }
        }

        foreach (var memberId in horde.MemberIds)
        {
            var member = _gameLogic.GetObjectById(memberId);
            if (member == null || member.IsEffectivelyDead || member.IsDestroyed)
            {
                continue;
            }

            var weapon = member.CurrentWeapon;
            if (weapon == null)
            {
                continue;
            }

            // Track the nearest live enemy every frame (dead or out-of-range locks would
            // stall the melee — each member's original pick rarely ends up adjacent once
            // the formations collide).
            var victim = NearestLiveVictim(member, victims);
            if (victim?.Id != weapon.CurrentTarget?.TargetObjectId)
            {
                weapon.SetTarget(victim != null ? new WeaponTarget(_gameLogic, victim.Id) : null);
            }

            // Released melee members chase their victim (the HordeAIUpdate pursue shape):
            // without it the survivors of first contact stand out of range and the fight
            // stalls. Formation steering already skips released slots.
            if (victim != null && horde.IsMeleeAttacking)
            {
                var memberMover = member.FindBehavior<SimLocomotorUpdate>();
                if (memberMover != null)
                {
                    if (Vector3.Distance(member.Translation, victim.Translation) > weapon.Template.AttackRange)
                    {
                        memberMover.SetTargetPosition(SimTransformBridge.PullPosition(victim), AttackMoveSpeedSentinel);
                    }
                    else if (memberMover.Mode == SimMoveMode.MoveToPosition)
                    {
                        memberMover.Stop();
                    }
                }
            }

            weapon.LogicTick();
        }
    }

    /// <summary>
    /// Nearest live combatant on the victim team: horde containers contribute their
    /// members, plain objects themselves. Ties break on lowest ObjectId (the iteration
    /// order), keeping the pick deterministic.
    /// </summary>
    private GameObject NearestLiveVictim(GameObject member, TeamEntry victims)
    {
        GameObject best = null;
        var bestDistanceSquared = float.MaxValue;

        void Consider(GameObject candidate)
        {
            if (candidate == null || candidate.IsEffectivelyDead || candidate.IsDestroyed)
            {
                return;
            }
            var distanceSquared = Vector3.DistanceSquared(member.Translation, candidate.Translation);
            if (distanceSquared < bestDistanceSquared)
            {
                best = candidate;
                bestDistanceSquared = distanceSquared;
            }
        }

        foreach (var id in MembersAscending(victims))
        {
            var container = _gameLogic.GetObjectById(id);
            if (container == null || container.IsDestroyed)
            {
                continue;
            }

            var horde = container.FindBehavior<SimHordeContain>();
            if (horde != null)
            {
                foreach (var memberId in horde.MemberIds)
                {
                    Consider(_gameLogic.GetObjectById(memberId));
                }
            }
            else
            {
                Consider(container);
            }
        }

        return best;
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
