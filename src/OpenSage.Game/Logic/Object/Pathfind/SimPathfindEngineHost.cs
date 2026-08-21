// S5 pathfinding - the engine-side owner of the pathfind grid + pathfinder
// (the SimPartitionEngineHost pattern; a float-boundary file, NOT [SimState]).
//
// Responsibilities:
//   - construct ONE SimPathfindGrid per match (extents from the map when there is one,
//     mirroring the partition host's extent selection; GPL newMap shape);
//   - stamp/unstamp IMMOBILE-or-STRUCTURE objects' footprints as obstacles in the
//     GameLogic object lifecycle (GPL addObjectToPathfindMap/removeObjectFromPathfindMap);
//   - run the pathfind queue once per logic frame in GPL's frame slot: AI::update runs
//     AFTER the sleepy module update loop and BEFORE BuildAssistant/PartitionManager
//     (GameLogic.cpp order) - GameLogic.Update ticks this host right after the module
//     loop, before the partition host;
//   - resolve queued ObjectIds to their SimLocomotorUpdate (the doPathfind target).
//
// Footprint note (PATH-F9): GPL stamps the rotated geometry footprint; this host stamps
// the axis-aligned square of the quantized bounding-circle radius - identical for the
// square/circular footprints the harness uses; the rotated-rect refinement lands with
// the transform port.

using System;
using System.Collections.Generic;
using System.Linq;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object.Pathfind;

internal sealed class SimPathfindEngineHost
{
    private readonly IGame _game;
    private readonly SimPathfindGrid _grid;
    private readonly SimPathfinder _pathfinder;

    // Stamped footprint rects by object id (for exact removal).
    private readonly Dictionary<uint, (int LoX, int LoY, int HiX, int HiY)> _stampedFootprints = new();

    // Obstacles created this frame whose transform is not final yet (GameLogic.CreateObject
    // runs BEFORE the engine places the object - the same lazy-ingestion reality as
    // LOCO-F8). They stamp at the next frame tick, in creation (= ObjectId) order, before
    // the queue processes - so a path computed that frame already sees them.
    private readonly List<GameObject> _pendingStamps = new();

    internal SimPathfindGrid Grid => _grid;
    internal SimPathfinder Pathfinder => _pathfinder;

    internal SimPathfindEngineHost(IGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        _game = game;

        // Extent selection mirrors SimPartitionEngineHost: map borders when loaded
        // (x10 heightmap factor), else the headless 2000x2000 world on the origin.
        float loX, loY, width, height;
        var heightMapData = _game.Scene3D?.MapFile?.HeightMapData;
        if (heightMapData != null && heightMapData.Borders.Length > 0)
        {
            var border = heightMapData.Borders[0];
            loX = border.Corner1X * 10f;
            loY = border.Corner1Y * 10f;
            width = (border.X - border.Corner1X) * 10f;
            height = (border.Y - border.Corner1Y) * 10f;
        }
        else
        {
            loX = -1000f;
            loY = -1000f;
            width = 2000f;
            height = 2000f;
        }

        // GPL newMap: hi = floor(hi/cellsize) - 1 (the extent is inclusive cell indices).
        var cellLoX = (int)MathF.Floor(loX / SimPathfindGrid.CellSize);
        var cellLoY = (int)MathF.Floor(loY / SimPathfindGrid.CellSize);
        var cellHiX = (int)MathF.Floor((loX + width) / SimPathfindGrid.CellSize) - 1;
        var cellHiY = (int)MathF.Floor((loY + height) / SimPathfindGrid.CellSize) - 1;

        _grid = new SimPathfindGrid(cellLoX, cellLoY, cellHiX, cellHiY);
        _pathfinder = new SimPathfinder(_grid);
    }

    // ------------------------------------------------------------------
    // Object lifecycle (GPL classifyObjectFootprint insert/remove)
    // ------------------------------------------------------------------

    internal void OnObjectAdded(GameObject gameObject)
    {
        if (gameObject is null || !gameObject.Id.IsValid)
        {
            return;
        }
        if (!IsPathfindObstacle(gameObject))
        {
            return;
        }

        _pendingStamps.Add(gameObject);
    }

    internal void OnObjectRemoved(GameObject gameObject)
    {
        if (gameObject is null)
        {
            return;
        }
        _pendingStamps.Remove(gameObject);
        if (!_stampedFootprints.Remove(gameObject.Id.Index, out var rect))
        {
            return;
        }
        _grid.RemoveObstacle(gameObject.Id.Index, rect.LoX, rect.LoY, rect.HiX, rect.HiY);
    }

    private static bool IsPathfindObstacle(GameObject gameObject)
    {
        // GPL classifies STRUCTUREs (immobile things with footprints) as obstacles;
        // mobile units go through the (deferred, PATH-F3) occupancy layer instead.
        var kindOf = gameObject.Definition.KindOf;
        return kindOf != null &&
            (kindOf.Get(ObjectKinds.Structure) || kindOf.Get(ObjectKinds.Immobile));
    }

    private (int LoX, int LoY, int HiX, int HiY) FootprintCells(GameObject gameObject)
    {
        // Quantized position + bounding radius (the F4 wire boundary in the bridge).
        var position = SimTransformBridge.PullPosition(gameObject);
        var (bounding, _) = SimTransformBridge.PullGeometry(gameObject);
        var lo = new FixVector3(position.X - bounding, position.Y - bounding, Fix64.Zero);
        var hi = new FixVector3(position.X + bounding, position.Y + bounding, Fix64.Zero);
        _grid.WorldToCell(lo, out var loX, out var loY);
        _grid.WorldToCell(hi, out var hiX, out var hiY);
        return (loX, loY, hiX, hiY);
    }

    // ------------------------------------------------------------------
    // The per-frame queue slot (GPL AI::update -> processPathfindQueue)
    // ------------------------------------------------------------------

    internal void Update()
    {
        // Stamp newly created obstacles (their transforms are final by now), THEN
        // process path requests - GPL's map is likewise up to date before its queue runs.
        if (_pendingStamps.Count > 0)
        {
            foreach (var gameObject in _pendingStamps)
            {
                if (gameObject.IsDestroyed)
                {
                    continue;
                }
                var (rectLoX, rectLoY, rectHiX, rectHiY) = FootprintCells(gameObject);
                _stampedFootprints[gameObject.Id.Index] = (rectLoX, rectLoY, rectHiX, rectHiY);
                _grid.StampObstacle(gameObject.Id.Index, rectLoX, rectLoY, rectHiX, rectHiY);
            }
            _pendingStamps.Clear();
        }

        _pathfinder.ProcessQueue(ResolveClient);
    }

    internal bool QueueForPath(ObjectId id) => _pathfinder.QueueForPath(id);

    private ISimPathfindClient ResolveClient(ObjectId id)
    {
        var gameObject = _game.GameLogic.GetObjectById(id);
        if (gameObject is null || gameObject.IsDestroyed)
        {
            return null;
        }
        return gameObject.BehaviorModules.OfType<ISimPathfindClient>().FirstOrDefault();
    }
}
