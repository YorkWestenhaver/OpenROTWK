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
using OpenSage.Logic.Map;
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

    public SimMapRun(SageGame sageGame, uint seed, MapFile mapFile, IReadOnlyList<string> iniTexts, bool retailLobbyWipe = false)
    {
        ArgumentNullException.ThrowIfNull(mapFile);
        ArgumentNullException.ThrowIfNull(iniTexts);

        // Opt-in retail-lobby conformance (SCRIPT-O2): only well-known players' script
        // lists reach the compiler; the default path keeps the GPL/SP behavior of
        // running every authored player's scripts.
        IReadOnlyList<ScriptList> scriptLists = mapFile.PlayerScriptsList?.ScriptLists ?? [];
        if (retailLobbyWipe)
        {
            scriptLists = SidesListUtility.ApplyRetailLobbyPlayerWipe(
                mapFile.SidesList?.Players ?? [], scriptLists);
        }

        Program = SimScriptCompiler.Compile(scriptLists);
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

        // Replace the host's default two-player world with the map's authored sides so
        // scenario teams get real distinct owners (with the map's playerEnemies/Allies)
        // instead of all collapsing onto the civilian player — TEAM_ATTACK_TEAM needs
        // hostile sides for a real damage exchange. Runs before any object spawns, so no
        // stale Player reference survives the swap. Scenariogen (and WorldBuilder) maps
        // always author Neutral first and PlyrCivilian second, the slots PlayerManager's
        // accessors assume.
        if (mapFile.SidesList?.Players is { Count: > 0 } mapPlayers)
        {
            Game.PlayerManager.OnNewGame([.. mapPlayers], GameType.Skirmish);
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
        Host.TickCombat();
        Game.Step();
    }

    private Player ResolveOwner(string ownerName) =>
        (string.IsNullOrEmpty(ownerName) ? null : Game.PlayerManager.GetPlayerByName(ownerName))
        ?? Game.CivilianPlayer;
}
