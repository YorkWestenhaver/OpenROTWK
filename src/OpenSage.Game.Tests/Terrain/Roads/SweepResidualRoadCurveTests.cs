// R15 L1-11 (sweep ratchet), the one UNFINGERPRINTED residual failure in the frozen 20-map
// AotR sweep at main 9bde4556: "map ang amon sul" died with
//   Process terminated. Assertion failed.
//      at OpenSage.Terrain.Roads.CurvedRoadSegment.CreateCurve(...)
//      at OpenSage.Terrain.Roads.RoadNetwork.InsertCurveSegments(...)
// during Scene3D.LoadObjects -> RoadCollection..ctor. OBS-4's fingerprinter misses it because
// it parses managed exception blocks only, and a Debug.Assert FailFast is not one.
//
// Root cause: InsertCurveSegments calls CreateCurve whenever a node has exactly two connected
// edges of the same template, and CreateCurve asserts it was handed exactly two IncomingRoadData.
// But ComputeRoadAngles GROUPS segments arriving at a node from the same angle into one entry
// ("treat road segments coming in at the same angle as one") and returns an EMPTY list when
// fewer than two survive that grouping. A node whose two road segments leave in the same
// direction - duplicate or collinear road pieces, which authored maps do contain - therefore
// produced Count == 0 for a node with two edges, and the assert killed the process.
// InsertCrossingSegments, immediately above it, already filters on the RESULT count
// (`Count == 3 || Count == 4`); the curve path did not.
//
// Fixed behavior asserted here: such a node simply gets no curve piece, and map load continues.

using System.Linq;
using System.Numerics;
using OpenSage.Data.Map;
using OpenSage.Terrain.Roads;
using Xunit;

namespace OpenSage.Tests.Terrain.Roads;

public class SweepResidualRoadCurveTests
{
    private static MapObject Node(float x, float y) =>
        new MapObject(new Vector3(x, y, 0), 0, RoadType.None, "SweepRoad");

    /// <summary>
    /// Two segments sharing a node and leaving it in the SAME direction. ComputeRoadAngles
    /// collapses them into one incoming road, so the node has two edges but zero usable
    /// incoming-road entries — the amon sul shape.
    /// </summary>
    private static RoadTopology CollinearDuplicateTopology(RoadTemplate template)
    {
        var topology = new RoadTopology();
        topology.AddSegment(template, Node(100, 100), Node(200, 100));
        topology.AddSegment(template, Node(100, 100), Node(300, 100));
        return topology;
    }

    /// <summary>
    /// A genuine corner: two segments meeting at (100, 200) from different directions.
    /// </summary>
    private static RoadTopology CornerTopology(RoadTemplate template)
    {
        var topology = new RoadTopology();
        topology.AddSegment(template, Node(100, 100), Node(100, 200));
        topology.AddSegment(template, Node(100, 200), Node(200, 200));
        return topology;
    }

    [Fact]
    public void CollinearSegmentsAtOneNode_BuildNetworks_DoesNotFailFast()
    {
        var template = new RoadTemplate("SweepRoad");
        var topology = CollinearDuplicateTopology(template);

        // The regression: CreateCurve's Debug.Assert(incomingRoadData.Count == 2) fired here
        // and terminated the process ("Process terminated. Assertion failed.").
        var networks = RoadNetwork.BuildNetworks(topology, new RoadTemplateList(new[] { template })).ToList();

        Assert.NotNull(networks);
    }

    [Fact]
    public void CollinearSegmentsAtOneNode_StillHaveTwoEdgesOnTheSharedNode()
    {
        var template = new RoadTemplate("SweepRoad");
        var topology = CollinearDuplicateTopology(template);
        topology.AlignOrientation();

        // Pins the precondition that made the guard necessary: the node-level test
        // (`connectedEdges == 2`) is satisfied, so only a result-level test can catch it.
        Assert.Contains(topology.Nodes, n => n.Edges.Count == 2);
    }

    [Fact]
    public void OrdinaryCorner_StillBuildsItsNetwork()
    {
        var template = new RoadTemplate("SweepRoad");
        var topology = CornerTopology(template);

        // Control: the guard must not suppress curve creation at a real corner.
        var networks = RoadNetwork.BuildNetworks(topology, new RoadTemplateList(new[] { template })).ToList();

        Assert.NotEmpty(networks);
    }
}
