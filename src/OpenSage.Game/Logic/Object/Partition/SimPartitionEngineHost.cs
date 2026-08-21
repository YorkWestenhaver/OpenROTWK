// The engine-side owner of the S3 partition grid (R9 partition wiring, closes F-PV-1).
//
// This file is a float-boundary file, NOT [SimState]: it stands between the float engine
// (GameObject transforms, the map's float extents, GameData's float PartitionCellSize) and
// the deterministic Fix64 SimPartitionGrid. Every float crossing goes through
// SimPartitionBridge / Fix64.FromWireFloat exactly once (F4), the same D-7 boundary shape
// as SimContext's other adapters.
//
// Responsibilities (the integrator list from partition-vision.md §7 F-PV-1):
//   - construct ONE SimPartitionGrid per match (extents from the map when there is one,
//     PartitionCellSize from GameData, roster view over PlayerManager);
//   - register/unregister entries in the GameLogic object lifecycle (hooked from
//     GameLogic.CreateObject / GameLogic.DestroyObject);
//   - push positions from the float transform into the grid (SyncPositions - S2's
//     SimPhysics stays the Fix64 source of truth for movement; until GameObject movement
//     itself notifies, positions are re-synced in ascending-ObjectId order before every
//     query and once per frame, which is deterministic because the transforms are
//     identical on every peer at those points);
//   - run the SimPhase.PartitionUpdate body (grid.Update) once per logic frame;
//   - serve ISimContext.Partition range queries from the grid (SimContext.PartitionAdapter
//     routes here), keeping the frozen ascending-ObjectId contract.
//
// STRICTNESS RECONCILIATION (the quadtree-<= vs grid-< diff flagged by F-PV-1): the legacy
// quadtree's FindNearby was a sphere-COLLIDER overlap test - inclusive at the boundary and
// inflated by the target's own collider extent. The grid measures GPL's Center2D distance
// with GPL's strict '<' predicate (PartitionManager::getClosestObjects). The grid semantics
// are the behavioral reference and WIN; the boundary-exact and collider-fringe inclusions
// the quadtree used to return were the approximation. Pinned by
// PartitionWiringTests.BoundaryExactDistance_IsExcluded_GridStrictness.

using System;
using System.Collections.Generic;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

internal sealed class SimPartitionEngineHost
{
    private readonly IGame _game;
    private readonly SimPartitionGrid _grid;
    private readonly Dictionary<uint, SimPartitionEntry> _entriesById = new();

    // Scratch list reused across queries (cleared per call; never escapes).
    private readonly List<SimPartitionEntry> _queryScratch = new();

    /// <summary>The one deterministic spatial index for the match.</summary>
    internal SimPartitionGrid Grid => _grid;

    internal SimPartitionEngineHost(IGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        _game = game;

        // Cell size: GameData's PartitionCellSize (float substrate, quantized once through
        // the wire path). A missing/degenerate value falls back to the original's shipped
        // default of 10 world units - NOT to the grid's clamp of 1, which would explode the
        // cell count on a large map. BFME2's actual INI value is a behavioral-spec pin (F-PV-5/7).
        var cellSizeFloat = _game.AssetStore?.GameData?.Current?.PartitionCellSize ?? 0f;
        if (cellSizeFloat < 1f)
        {
            cellSizeFloat = 10f;
        }

        // Extents: the map's terrain boundary when a map is loaded (the same border
        // PartitionCellManager uses, x10 heightmap factor); the headless/test host has no
        // MapFile and gets a fixed 2000x2000 world centered on the origin, which covers
        // every scenario the headless host stages.
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

        _grid = new SimPartitionGrid(
            Quantize(loX),
            Quantize(loY),
            Quantize(width),
            Quantize(height),
            Quantize(cellSizeFloat),
            new PlayerManagerView(_game));
    }

    private static Fix64 Quantize(float value)
        => Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(value));

    // ------------------------------------------------------------------
    // Lifecycle (hooked from GameLogic.CreateObject / DestroyObject)
    // ------------------------------------------------------------------

    internal void OnObjectAdded(GameObject gameObject)
    {
        if (gameObject is null || !gameObject.Id.IsValid || _entriesById.ContainsKey(gameObject.Id.Index))
        {
            return;
        }

        var ownerIndex = OwnerIndex(gameObject);
        var entry = SimPartitionBridge.Register(_grid, gameObject, ownerIndex, Now);
        _entriesById.Add(gameObject.Id.Index, entry);
    }

    internal void OnObjectRemoved(GameObject gameObject)
    {
        if (gameObject is null || !_entriesById.Remove(gameObject.Id.Index, out var entry))
        {
            return;
        }

        _grid.Unregister(entry, Now);
    }

    /// <summary>
    /// The SimPhase.PartitionUpdate body: re-anchor moved objects, then pop the due
    /// timed shroud undos (GPL PartitionManager::update shape).
    /// </summary>
    internal void Update(LogicFrame now)
    {
        SyncPositions(now);
        _grid.Update(now);
    }

    // ------------------------------------------------------------------
    // The ISimContext.Partition query surface (SimContext.PartitionAdapter routes here)
    // ------------------------------------------------------------------

    /// <summary>
    /// GPL iterateObjectsInRange FROM_CENTER_2D with the strict-&lt; predicate, ascending
    /// ObjectId (the frozen determinism contract - unchanged from the quadtree adapter's
    /// sorted output, so module code is agnostic to the backing swap).
    /// </summary>
    internal List<GameObject> QueryObjectsInRadius(GameObject center, Fix64 radius)
    {
        ArgumentNullException.ThrowIfNull(center);

        // Late-registration guard: an object created outside GameLogic.CreateObject's hook
        // (not a shipping path) still gets an entry rather than a miss.
        if (!_entriesById.TryGetValue(center.Id.Index, out var centerEntry))
        {
            OnObjectAdded(center);
            centerEntry = _entriesById[center.Id.Index];
        }

        SyncPositions(Now);

        _queryScratch.Clear();
        _grid.QueryObjectsInRange(centerEntry, radius, PartitionDistanceType.Center2D, _queryScratch);

        var results = new List<GameObject>(_queryScratch.Count);
        foreach (var entry in _queryScratch)
        {
            var gameObject = _game.GameLogic.GetObjectById(entry.Id);
            if (gameObject is not null)
            {
                results.Add(gameObject);
            }
        }

        _queryScratch.Clear();
        return results;
    }

    // ------------------------------------------------------------------
    // Position sync (float transform -> Fix64 grid, quantized once per change)
    // ------------------------------------------------------------------

    private void SyncPositions(LogicFrame now)
    {
        // Ascending ObjectId (EntriesAscendingId is sorted by construction), so the
        // unlook/look churn a move causes is ordered identically on every peer.
        var entries = _grid.EntriesAscendingId;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var gameObject = _game.GameLogic.GetObjectById(entry.Id);
            if (gameObject is null)
            {
                continue;
            }

            var position = SimPartitionBridge.QuantizePosition(gameObject.Translation);
            if (position.X != entry.Position.X ||
                position.Y != entry.Position.Y ||
                position.Z != entry.Position.Z)
            {
                _grid.UpdatePosition(entry, position, now);
            }
        }
    }

    private LogicFrame Now => _game.GameLogic.CurrentFrame;

    private int OwnerIndex(GameObject gameObject)
    {
        var owner = gameObject.Owner;
        return owner is null ? 0 : _game.PlayerManager.GetPlayerIndex(owner);
    }

    // ------------------------------------------------------------------
    // Roster view (IPartitionPlayerView over PlayerManager)
    // ------------------------------------------------------------------

    /// <summary>
    /// The grid's view of the live roster, reduced exactly like the combat layer's
    /// relationship helper (DamagePipeline.GetRelationship): same player or ally set =
    /// Allies, enemy set = Enemies, else Neutral. Masks are 1u &lt;&lt; playerIndex,
    /// identical on every peer because the roster and its indices are.
    /// </summary>
    private sealed class PlayerManagerView : IPartitionPlayerView
    {
        private readonly IGame _game;

        internal PlayerManagerView(IGame game) => _game = game;

        public int PlayerCount => Math.Max(_game.PlayerManager.Players.Count, 1);

        public uint GetLookerMask(int ownerPlayerIndex)
        {
            var mask = 1u << ownerPlayerIndex;
            for (var i = 0; i < PlayerCount; i++)
            {
                if (i != ownerPlayerIndex && GetRelationship(i, ownerPlayerIndex) == RelationshipType.Allies)
                {
                    mask |= 1u << i;
                }
            }
            return mask;
        }

        public uint GetEnemyAndNeutralMask(int ownerPlayerIndex)
        {
            var mask = 0u;
            for (var i = 0; i < PlayerCount; i++)
            {
                if (i == ownerPlayerIndex)
                {
                    continue;
                }
                var relationship = GetRelationship(i, ownerPlayerIndex);
                if (relationship is RelationshipType.Enemies or RelationshipType.Neutral)
                {
                    mask |= 1u << i;
                }
            }
            return mask;
        }

        public RelationshipType GetRelationship(int viewerPlayerIndex, int ownerPlayerIndex)
        {
            if (viewerPlayerIndex == ownerPlayerIndex)
            {
                return RelationshipType.Allies;
            }

            var players = _game.PlayerManager.Players;
            if (viewerPlayerIndex < 0 || viewerPlayerIndex >= players.Count ||
                ownerPlayerIndex < 0 || ownerPlayerIndex >= players.Count)
            {
                return RelationshipType.Neutral;
            }

            var viewer = players[viewerPlayerIndex];
            var owner = players[ownerPlayerIndex];
            if (viewer.Allies.Contains(owner))
            {
                return RelationshipType.Allies;
            }
            if (viewer.Enemies.Contains(owner))
            {
                return RelationshipType.Enemies;
            }
            return RelationshipType.Neutral;
        }
    }
}
