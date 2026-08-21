using System;
using System.Collections.Generic;

namespace OpenSage.Logic;

public sealed class TeamFactory : IPersistableObject
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly IGame _game;

    private readonly List<TeamTemplate> _teamTemplates;
    private readonly Dictionary<uint, TeamTemplate> _teamTemplatesById;
    private readonly Dictionary<string, TeamTemplate> _teamTemplatesByName;

    private uint _lastTeamId;

    public TeamFactory(IGame game)
    {
        _game = game;

        _teamTemplates = new List<TeamTemplate>();
        _teamTemplatesById = new Dictionary<uint, TeamTemplate>();
        _teamTemplatesByName = new Dictionary<string, TeamTemplate>();

        _lastTeamId = 0;
    }

    public void Initialize(Data.Map.Team[] mapTeams)
    {
        _teamTemplates.Clear();
        _teamTemplatesById.Clear();
        _teamTemplatesByName.Clear();

        foreach (var mapTeam in mapTeams)
        {
            var name = mapTeam.Name;

            var ownerName = mapTeam.Owner;
            var owner = _game.PlayerManager.GetPlayerByName(ownerName);

            var isSingleton = mapTeam.IsSingleton;

            AddTeamTemplate(name, owner, isSingleton);
        }
    }

    private void AddTeamTemplate(string name, Player owner, bool isSingleton)
    {
        // id assignment is intentionally always driven by _teamTemplatesById.Count, and every
        // template (duplicate-named or not) is unconditionally added to _teamTemplates and
        // _teamTemplatesById below. Only the by-name registration is conditional (first-wins,
        // see below), so this counter stays monotonic and collision-free regardless of
        // duplicate names -- the id-counter invariant that Persist()/FindTeamTemplateById()/
        // FindTeamById() rely on is preserved for every template that gets constructed.
        var id = (uint)(_teamTemplatesById.Count + 1);

        var teamTemplate = new TeamTemplate(
            this,
            id,
            name,
            owner,
            isSingleton);

        _teamTemplates.Add(teamTemplate);
        _teamTemplatesById.Add(id, teamTemplate);

        // Retail first-wins semantics for duplicate team template names. GPL
        // TeamFactory::addTeamPrototypeToList (generals-gpl/Generals/Code/GameEngine/Source/Common/RTS/Team.cpp:255-266)
        // looks up the new TeamPrototype's name-derived key in m_prototypes and, if an entry is
        // already registered under that key, returns WITHOUT adding the new one -- only a
        // DEBUG_ASSERTCRASH-gated diagnostic fires (debug builds only; never a crash/throw in
        // retail). The first-registered prototype for a given name is the one every later
        // name-based lookup (findTeamPrototype, and findTeamPrototypeByID which also walks
        // m_prototypes) resolves to; the TeamPrototype object for a later duplicate still
        // exists (its constructor at Team.cpp:804-830 runs unconditionally and always adds it
        // to its owning player's team list), it just never becomes reachable by name.
        // AotR ships duplicate team template names on several maps (teamPlyrNeutral x6,
        // teamPlayer_1, teamPlyrAngmar, Frodo) which previously hard-crashed map load via
        // Dictionary.Add's duplicate-key exception; replicate first-wins + warn instead.
        if (_teamTemplatesByName.TryGetValue(name, out var existingTemplate))
        {
            Logger.Warn(
                $"TeamFactory.AddTeamTemplate: duplicate team template name '{name}' " +
                $"(existing id {existingTemplate.ID} kept for name lookup, new id {id} created but not name-addressable); " +
                "matches retail first-wins semantics (Team.cpp TeamFactory::addTeamPrototypeToList).");
        }
        else
        {
            _teamTemplatesByName.Add(name, teamTemplate);
        }

        if (isSingleton)
        {
            AddTeam(teamTemplate);
        }
    }

    internal Team AddTeam(TeamTemplate teamTemplate)
    {
        _lastTeamId++;

        var team = new Team(teamTemplate, _lastTeamId);

        teamTemplate.AddTeam(team);

        return team;
    }

    internal Team AddTeamWithId(TeamTemplate teamTemplate, uint id)
    {
        _lastTeamId = Math.Max(_lastTeamId, id);

        var team = new Team(teamTemplate, id);

        teamTemplate.AddTeam(team);

        return team;
    }

    public TeamTemplate FindTeamTemplateByName(string name)
    {
        if (_teamTemplatesByName.TryGetValue(name, out var result))
        {
            return result;
        }
        return null;
    }

    public TeamTemplate FindTeamTemplateById(uint id)
    {
        if (_teamTemplatesById.TryGetValue(id, out var result))
        {
            return result;
        }
        return null;
    }

    public Team FindTeamById(uint id)
    {
        foreach (var teamTemplate in _teamTemplates)
        {
            var team = teamTemplate.FindTeamById(id);
            if (team != null)
            {
                return team;
            }
        }
        return null;
    }

    public void Persist(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.PersistUInt32(ref _lastTeamId);

        var count = (ushort)_teamTemplates.Count;
        reader.PersistUInt16(ref count, "TeamTemplatesCount");

        if (count != _teamTemplates.Count)
        {
            throw new InvalidStateException();
        }

        reader.BeginArray("TeamTemplates");
        if (reader.Mode == StatePersistMode.Read)
        {
            for (var i = 0; i < count; i++)
            {
                reader.BeginObject();

                var id = 0u;
                reader.PersistUInt32(ref id);

                var teamTemplate = _teamTemplatesById[id];
                reader.PersistObject(teamTemplate);

                reader.EndObject();
            }
        }
        else
        {
            foreach (var teamTemplate in _teamTemplates)
            {
                reader.BeginObject();

                var id = teamTemplate.ID;
                reader.PersistUInt32(ref id);

                reader.PersistObject(teamTemplate);

                reader.EndObject();
            }
        }
        reader.EndArray();
    }
}
