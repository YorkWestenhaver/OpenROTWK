// S8 map-scenario core - the engine half of the Target-B conformance harness's map path.
//
// Takes a parsed .map (scenariogen or WorldBuilder output), compiles its PlayerScriptsList
// through SimScriptCompiler, and stands the result on a HeadlessSimGame: waypoints and teams
// registered on a SimScriptHostAdapter from the map chunks, non-waypoint ObjectsList entries
// spawned through the real GameLogic.CreateObject path, one SimScriptEngine driving the
// compiled program. StepFrame runs the same per-frame order the end-to-end test pinned:
// engine.Update() (reads the pre-increment GameLogic frame) then HeadlessSimGame.Step()
// (GameLogic.Update + DeleteDestroyed).
//
// Team owner resolution is name-based against the live PlayerManager; a map player that the
// headless host does not know (it only creates Neutral + Civilian) falls back to the
// civilian player, which keeps every scenariogen map runnable without porting the lobby's
// player-model rewrite (SR-F2's registry team model).

using System;
using System.Collections.Generic;
using System.IO;
using OpenSage.Data.Map;
using OpenSage.Logic.Sim;
using OpenSage.Scripting;
using Player = OpenSage.Logic.Player;

namespace OpenSage.Logic.Script;

internal sealed class SimMapRun
{
    public HeadlessSimGame Game { get; }
    public SimScriptHostAdapter Host { get; }
    public SimScriptEngine Engine { get; }
    public SimScriptProgram Program { get; }

    public int MapObjectsSpawned { get; }
    public int MapObjectsSkipped { get; }

    public SimMapRun(SageGame sageGame, uint seed, MapFile mapFile, IReadOnlyList<string> iniTexts)
    {
        ArgumentNullException.ThrowIfNull(mapFile);
        ArgumentNullException.ThrowIfNull(iniTexts);

        Program = SimScriptCompiler.Compile(mapFile.PlayerScriptsList);
        if (Program.UnknownConditionIds.Count > 0 || Program.UnknownActionIds.Count > 0)
        {
            throw new InvalidDataException(
                "map scripts use ids outside the compiled subset: " +
                $"conditions [{string.Join(", ", Program.UnknownConditionIds)}] " +
                $"actions [{string.Join(", ", Program.UnknownActionIds)}]");
        }

        Game = new HeadlessSimGame(sageGame, seed);
        foreach (var iniText in iniTexts)
        {
            Game.LoadIniText(iniText);
        }

        Host = new SimScriptHostAdapter(Game, Game.CivilianPlayer);

        var teamOwners = new Dictionary<string, Player>(StringComparer.Ordinal);
        foreach (var team in mapFile.GetTeams())
        {
            var owner = ResolveOwner(team.Owner);
            teamOwners[team.Name] = owner;
            Host.RegisterTeam(team.Name, owner);
        }

        foreach (var mapObject in mapFile.ObjectsList.Objects)
        {
            if (mapObject.TypeName == Waypoint.ObjectTypeName)
            {
                if (mapObject.Properties.TryGetValue("waypointName", out var waypointName))
                {
                    Host.RegisterWaypoint((string)waypointName.Value, mapObject.Position);
                }
                continue;
            }

            if (Game.AssetStore.ObjectDefinitions.GetByName(mapObject.TypeName) == null)
            {
                // GPL parity with the script spawn path: unknown template -> warn + no spawn.
                MapObjectsSkipped++;
                continue;
            }

            var teamName = mapObject.Properties.TryGetValue("originalOwner", out var originalOwner)
                ? (string)originalOwner.Value
                : null;
            var spawnOwner = teamName != null && teamOwners.TryGetValue(teamName, out var teamOwner)
                ? teamOwner
                : Game.CivilianPlayer;

            var gameObject = Game.SpawnObject(mapObject.TypeName, spawnOwner, mapObject.Position);
            if (teamName != null && teamOwners.ContainsKey(teamName))
            {
                Host.RegisterTeamMember(teamName, gameObject);
            }
            MapObjectsSpawned++;
        }

        Engine = new SimScriptEngine(Program, Host, Game.GameEngine.SimContext.GameLogicRandom);
    }

    public bool MapExitRequested => Host.MapExitRequested;

    public void StepFrame()
    {
        Engine.Update();
        Game.Step();
    }

    private Player ResolveOwner(string ownerName) =>
        (string.IsNullOrEmpty(ownerName) ? null : Game.PlayerManager.GetPlayerByName(ownerName))
        ?? Game.CivilianPlayer;
}
