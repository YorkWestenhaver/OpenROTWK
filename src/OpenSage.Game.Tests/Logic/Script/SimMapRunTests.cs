// SimMapRun system tests: the map-v1 scenario's core path on the real scenariogen asset
// (job005_spawn_fight.map) - map load, script compile, waypoint/team registration, real
// spawns through GameLogic, run to the scripted MAP_EXIT - plus the OracleView channel
// dumped through DeepCrcWriter, the record group the Target-B comparator consumes.
//
// Hermetic like the neighbouring end-to-end test: the AotR templates the map references
// (GondorFighterHorde / MordorFighterHorde) are minimal stand-in definitions with a body
// and a weapon, embedded as INI text.

using System.Collections.Generic;
using System.IO;
using System.Text;
using OpenSage.Data.Map;
using OpenSage.Logic.Object;
using OpenSage.Logic.Script;
using OpenSage.Logic.Sim;
using OpenSage.Logic.Sync;
using OpenSage.SimCore.Rng;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using Xunit;

namespace OpenSage.Tests.Logic.Script;

public class SimMapRunTests
{
    private const string Definitions = @"
Weapon MapTestSword
  AttackRange = 500
  DamageNugget
    Damage = 10
    Radius = 0.0
    DamageType = SLASH
    DeathType = NORMAL
  End
End

Object GondorFighterHorde
  KindOf = INFANTRY CAN_ATTACK
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 300
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY MapTestSword
  End
End

Object MordorFighterHorde
  KindOf = INFANTRY CAN_ATTACK
  Geometry = CYLINDER
  GeometryMajorRadius = 5
  GeometryHeight = 10
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 300
  End
  WeaponSet
    Conditions = None
    Weapon = PRIMARY MapTestSword
  End
End
";

    private static MapFile LoadJob005Map()
    {
        var mapPath = Path.Combine("Logic", "Script", "Assets", "job005_spawn_fight.map");
        using var stream = File.OpenRead(mapPath);
        return MapFile.FromStream(stream);
    }

    [Fact]
    public void Job005Map_RunsToMapExit_WithOracleViewDump()
    {
        var run = new SimMapRun(SageGame.Bfme2, 0xB00, LoadJob005Map(), [Definitions]);

        Assert.Empty(run.Program.UnknownConditionIds);
        Assert.Empty(run.Program.UnknownActionIds);

        var context = (SimContext)run.Game.GameEngine.SimContext;
        var random = ((CountingSimRandom)context.GameLogicRandom).Random;
        var checker = new SyncChecker(new ICrcChannelSource[]
        {
            new GameObjectsChannelSource(run.Game.GameLogic),
            new LogicRandomChannelSource(random),
            new OracleViewChannelSource(run.Game.GameLogic),
        });

        var dumpPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".ddump");
        try
        {
            using (var stream = new StreamWriter(dumpPath, append: false, new UTF8Encoding(false)) { NewLine = "\n" })
            using (var writer = new DeepCrcWriter(stream, leaveOpen: true))
            {
                for (var f = 0u; f < 250 && !run.MapExitRequested; f++)
                {
                    run.StepFrame();
                    var frame = new LogicFrame(f);
                    var message = checker.ComputeDeepCheckpoint(frame, writer);
                    writer.CrcVector(frame.Value, message.Combined, message.ChannelCrcs);
                }
            }

            // The scripted exit fired (the map's shared-timer telemetry reads ~100).
            Assert.True(run.MapExitRequested);
            Assert.Equal(100u, run.Engine.MapExitFrame.Value);

            // Both scripted spawns are real engine objects with the map's template names.
            Assert.True(run.Game.GameLogic.TryGetObjectByName("Atk_1", out var attacker));
            Assert.True(run.Game.GameLogic.TryGetObjectByName("Def_1", out var defender));
            Assert.Equal("GondorFighterHorde", attacker.Definition.Name);
            Assert.Equal("MordorFighterHorde", defender.Definition.Name);

            // The dump carries OracleView record groups for both objects, all four fields.
            var lines = File.ReadAllLines(dumpPath);
            var oracleRecords = new List<string[]>();
            foreach (var line in lines)
            {
                var tokens = line.Split(' ');
                if (tokens[0] == "R" && tokens[3] == "OracleView")
                {
                    oracleRecords.Add(tokens);
                }
            }
            Assert.NotEmpty(oracleRecords);
            foreach (var templateName in new[] { "GondorFighterHorde", "MordorFighterHorde" })
            {
                foreach (var fieldName in new[] { "Position", "Angle", "Health", "MaxHealth" })
                {
                    Assert.Contains(oracleRecords, r => r[4] == templateName && r[5] == fieldName);
                }
            }
        }
        finally
        {
            File.Delete(dumpPath);
        }
    }

    [Fact]
    public void Job009Map_RealIniSubset_SpawnsHordeMembersAndExchangesDamage()
    {
        var mapPath = Path.Combine("Logic", "Script", "Assets", "job009_creep_fight.map");
        MapFile mapFile;
        using (var stream = File.OpenRead(mapPath))
        {
            mapFile = MapFile.FromStream(stream);
        }
        var subsetIni = File.ReadAllText(
            Path.Combine("Logic", "Script", "Assets", "job009_creep_fight_subset.ini"));

        var run = new SimMapRun(SageGame.Bfme2, 0xB00, mapFile, [subsetIni]);

        static int CountByTemplate(SimMapRun run, params string[] names)
        {
            var count = 0;
            foreach (var gameObject in run.Game.GameLogic.Objects)
            {
                foreach (var name in names)
                {
                    if (gameObject.Definition.Name == name)
                    {
                        count++;
                        break;
                    }
                }
            }
            return count;
        }

        static float TotalMemberHealth(SimMapRun run)
        {
            var total = 0f;
            foreach (var gameObject in run.Game.GameLogic.Objects)
            {
                var name = gameObject.Definition.Name;
                if (name is "GondorFighter" or "MordorFighter1" or "MordorFighter2")
                {
                    total += gameObject.BodyModule.Health;
                }
            }
            return total;
        }

        // Frame 0 runs the spawn scripts; the S6 contain spawns the payloads on the first
        // module update. Both hordes' full member rosters exist by frame 5.
        for (var f = 0; f < 5; f++)
        {
            run.StepFrame();
        }

        Assert.Equal(15, CountByTemplate(run, "GondorFighter"));
        Assert.Equal(20, CountByTemplate(run, "MordorFighter1", "MordorFighter2"));
        var healthAfterSpawn = TotalMemberHealth(run);
        Assert.Equal(15 * 330 + 20 * 75, (int)healthAfterSpawn);

        // The two sides' owners became hostile through TEAM_ATTACK_TEAM.
        Assert.True(run.Game.GameLogic.TryGetObjectByName("Atk_1", out var attacker));
        Assert.True(run.Game.GameLogic.TryGetObjectByName("Def_1", out var defender));
        Assert.NotEqual(attacker.Owner, defender.Owner);
        Assert.Contains(defender.Owner, attacker.Owner.Enemies);

        // March + melee: by frame 250 the hordes have closed the 300-unit gap and the
        // members' S1 weapons have drawn real blood on both sides.
        for (var f = 5; f < 250; f++)
        {
            run.StepFrame();
        }

        var healthAfterFight = TotalMemberHealth(run);
        Assert.True(healthAfterFight < healthAfterSpawn,
            $"expected member health to decrease ({healthAfterFight} >= {healthAfterSpawn})");
    }

    [Fact]
    public void Job005Map_RegistersMapTeamsAndWaypoints()
    {
        var run = new SimMapRun(SageGame.Bfme2, 0xB00, LoadJob005Map(), [Definitions]);

        // The map's ObjectsList holds only waypoints; nothing spawns at load time.
        Assert.Equal(0, run.MapObjectsSpawned);
        Assert.Equal(0, run.MapObjectsSkipped);

        // First frame: both sides' spawn scripts run against the registered wpAtk/wpDef.
        run.StepFrame();
        Assert.True(run.Game.GameLogic.TryGetObjectByName("Atk_1", out var attacker));
        Assert.True(run.Game.GameLogic.TryGetObjectByName("Def_1", out var defender));
        Assert.NotEqual(attacker.Translation, defender.Translation);
    }

    [Fact]
    public void Job005Map_RetailLobbyWipe_AuthoredScenarioScriptsDoNotRun()
    {
        var run = new SimMapRun(SageGame.Bfme2, 0xB00, LoadJob005Map(), [Definitions], retailLobbyWipe: true);

        // Every job005 script belongs to ScnAttacker/ScnDefender; the retail lobby
        // wipes those players WITH their script lists (SCRIPT-O2 — this exact map
        // hung in retail because its telemetry never ran).
        Assert.Empty(run.Program.Scripts);

        for (var f = 0; f < 20; f++)
        {
            run.StepFrame();
        }

        Assert.False(run.MapExitRequested);
        Assert.False(run.Game.GameLogic.TryGetObjectByName("Atk_1", out _));
    }
}
