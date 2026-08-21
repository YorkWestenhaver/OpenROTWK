// R9 partition wiring tests (sys/partition-wiring, closes F-PV-1): ISimContext.Partition
// is now served by the deterministic Fix64 SimPartitionGrid instead of the float
// quadtree. These tests drive the seam end-to-end on the headless host: real parsed
// GameObjects, real GameLogic lifecycle hooks, real SimContext adapter.
//
// The strictness pin: the legacy quadtree's FindNearby was an inclusive sphere-collider
// overlap; the grid is GPL's Center2D measure with GPL's strict '<' predicate
// (PartitionManager::getClosestObjects). The grid semantics win - pinned here so a future
// backing change cannot silently reintroduce the '<=' fringe.

using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenSage.Logic.Object;
using OpenSage.Logic.Sim;
using OpenSage.Logic.Sync;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using Xunit;

namespace OpenSage.Tests.Logic.Object.Partition;

public class PartitionWiringTests
{
    private const string Definitions = @"
Object WiringDummy
  KindOf = INFANTRY
  Geometry = CYLINDER
  GeometryMajorRadius = 1
  GeometryHeight = 10
  VisionRange = 50.0
  Body = ActiveBody ModuleTag_Body
    MaxHealth = 100
  End
End
";

    private static HeadlessSimGame NewGame()
    {
        var game = new HeadlessSimGame(SageGame.Bfme2);
        game.LoadIniText(Definitions);
        return game;
    }

    private static uint[] QueryIds(HeadlessSimGame game, GameObject center, int radius)
        => game.GameEngine.SimContext.Partition
            .QueryObjectsInRadius(center, new Fix64(radius))
            .Select(o => o.Id.Index)
            .ToArray();

    [Fact]
    public void QueryRoutesThroughGrid_AscendingObjectId()
    {
        var game = NewGame();
        var center = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(100, 100, 0));
        // Spawn far-to-near so insertion order differs from id order inside a cell scan.
        var far = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(130, 100, 0));
        var near = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(105, 100, 0));

        var ids = QueryIds(game, center, 40);

        Assert.Equal(new[] { far.Id.Index, near.Id.Index }, ids);
        Assert.True(ids[0] < ids[1]);
    }

    [Fact]
    public void BoundaryExactDistance_IsExcluded_GridStrictness()
    {
        // The reconciliation pin: at EXACTLY the query radius the quadtree used to say
        // yes (inclusive collider overlap); GPL's partition predicate is strict '<' and
        // the grid is the authority now.
        var game = NewGame();
        var center = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(100, 100, 0));
        var atBoundary = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(130, 100, 0));
        var inside = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(129, 100, 0));

        var ids = QueryIds(game, center, 30);

        Assert.DoesNotContain(atBoundary.Id.Index, ids);
        Assert.Contains(inside.Id.Index, ids);
    }

    [Fact]
    public void DestroyedObjectLeavesTheIndex()
    {
        var game = NewGame();
        var center = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(100, 100, 0));
        var other = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(110, 100, 0));

        Assert.Contains(other.Id.Index, QueryIds(game, center, 30));

        game.GameLogic.DestroyObject(other);
        game.GameLogic.DeleteDestroyed();

        Assert.DoesNotContain(other.Id.Index, QueryIds(game, center, 30));
    }

    [Fact]
    public void MovementIsPickedUpByTheGrid()
    {
        var game = NewGame();
        var center = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(100, 100, 0));
        var mover = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(500, 500, 0));

        Assert.DoesNotContain(mover.Id.Index, QueryIds(game, center, 30));

        // Move it into range on the float transform; the wiring re-syncs (quantized once)
        // before the next query.
        mover.UpdateTransform(new Vector3(110, 100, 0));
        game.Step();

        Assert.Contains(mover.Id.Index, QueryIds(game, center, 30));

        mover.UpdateTransform(new Vector3(600, 600, 0));

        Assert.DoesNotContain(mover.Id.Index, QueryIds(game, center, 30));
    }

    // ------------------------------------------------------------------
    // Determinism: the same scenario yields identical range-query results (run-twice)
    // ------------------------------------------------------------------

    private static (List<uint[]> queries, uint partitionCrc, uint shroudCrc) RunScenario()
    {
        var game = NewGame();
        var queries = new List<uint[]>();

        var a = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(100, 100, 0));
        var b = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(140, 100, 0));
        var c = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(100, 140, 0));

        for (var frame = 0; frame < 12; frame++)
        {
            // A little deterministic churn: b spirals inward, c dies mid-run.
            b.UpdateTransform(new Vector3(140 - frame * 3, 100, 0));
            if (frame == 6)
            {
                game.GameLogic.DestroyObject(c);
            }

            game.Step();

            queries.Add(QueryIds(game, a, 35));
        }

        var partition = new XferCrcVisitor();
        new PartitionChannelSource(game.GameLogic).Xfer(partition);
        var shroud = new XferCrcVisitor();
        new ShroudChannelSource(game.GameLogic).Xfer(shroud);
        return (queries, partition.Value, shroud.Value);
    }

    [Fact]
    public void SameScenarioTwice_IdenticalRangeQueryResultsAndChannelCrcs()
    {
        var first = RunScenario();
        var second = RunScenario();

        Assert.Equal(first.queries.Count, second.queries.Count);
        for (var i = 0; i < first.queries.Count; i++)
        {
            Assert.Equal(first.queries[i], second.queries[i]);
        }

        Assert.Equal(first.partitionCrc, second.partitionCrc);
        Assert.Equal(first.shroudCrc, second.shroudCrc);

        // The channels carry real content once objects exist (they are no longer the
        // empty placeholders the R8 integration note called out).
        Assert.NotEqual(0u, first.partitionCrc);
    }

    [Fact]
    public void AreaDamageFlowsThroughTheGrid()
    {
        // The DamagePipeline crossing F-PV-1/D-7 flagged: DealAreaDamage's victim set now
        // comes from the grid. Boundary-exact victims are OUT (strict '<').
        var game = NewGame();
        var source = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(100, 100, 0));
        var victim = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(101, 100, 0));
        var inside = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(110, 100, 0));
        var atBoundary = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(120, 100, 0));

        var input = new CombatDamageInput
        {
            SourceId = source.Id,
            DamageType = DamageType.Unresistable,
            Amount = new Fix64(25),
        };

        // Radius 20 measured from the primary victim at (101,100): 'inside' at distance 9
        // splashes; 'atBoundary' at EXACTLY 19... make it exact: distance from victim to
        // atBoundary is 19 (in range); instead measure the strictness from the source
        // query center - covered by BoundaryExactDistance test. Here: everything strictly
        // inside the radius takes splash, the same-owner bystanders via the Allies flag.
        DamagePipeline.DealAreaDamage(
            game.GameEngine.SimContext, source, victim, new Fix64(20),
            WeaponAffectsTypes.Allies, input);

        Assert.Equal(new Fix64(75), BodyOf(victim).DamageCore.CurrentHealth);
        Assert.Equal(new Fix64(75), BodyOf(inside).DamageCore.CurrentHealth);
        // The source stood inside the radius and is skipped without the Self flag.
        Assert.Equal(new Fix64(100), BodyOf(source).DamageCore.CurrentHealth);

        // Strictness through the pipeline: a victim at EXACTLY the radius distance from
        // the query center is not splashed (grid strict '<').
        var exact = game.SpawnObject("WiringDummy", game.CivilianPlayer, new Vector3(121, 100, 0));
        _ = exact; // at distance 20 from victim (101,100)
        DamagePipeline.DealAreaDamage(
            game.GameEngine.SimContext, source, victim, new Fix64(20),
            WeaponAffectsTypes.Allies, input);
        Assert.Equal(new Fix64(100), BodyOf(exact).DamageCore.CurrentHealth);
    }

    private static ActiveBody BodyOf(GameObject gameObject)
        => Assert.IsType<ActiveBody>(gameObject.BodyModule, exactMatch: false);
}
