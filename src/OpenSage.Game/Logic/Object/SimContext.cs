// The one ISimContext implementation: an adapter over the (partially migrated) engine.
//
// This file is a float-boundary file, NOT [SimState]: it is where the Fix64 world of ported
// modules meets the float substrate that has not migrated yet (quadtree, Body). Every such
// crossing is localized here and disappears subsystem-by-subsystem per api-freeze-v1 F11.
//
// RNG (seam S3): the context owns THE logic stream a ported module can see - a
// LogicRandom born at the single blessed site (LogicRandom.CreateForSimContext), wrapped in
// the draw-counting CountingSimRandom. It is seeded from the match seed. NOTE (migration):
// the unmigrated float sim still draws from GameLogic.Random (the Mathematics SageRandom
// port of the same generator); the two streams collapse into one when GameLogic itself
// migrates onto the context. Recorded as a pilot finding.

using System.Collections.Generic;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Rng;

namespace OpenSage.Logic.Object;

internal sealed class SimContext : ISimContext
{
    private readonly IGameEngine _engine;
    private readonly CountingSimRandom _gameLogicRandom;

    internal SimContext(IGameEngine engine)
    {
        _engine = engine;
        _gameLogicRandom = new CountingSimRandom(
            LogicRandom.CreateForSimContext(engine.GameLogic.Random.Seed));
        GameLogic = new GameLogicAdapter(engine);
        Partition = new PartitionAdapter(engine);
        Terrain = new TerrainAdapter();
        Players = new PlayerListAdapter(engine);
        Assets = new AssetStoreAdapter();
        Events = new SimEventsAdapter();
    }

    /// <summary>The engine bridge for the BehaviorModule migration ctor. Engine-only.</summary>
    internal IGameEngine Engine => _engine;

    public LogicFrame CurrentFrame => _engine.GameLogic.CurrentFrame;

    public ISimRandom GameLogicRandom => _gameLogicRandom;

    public IGameLogic GameLogic { get; }
    public IPartitionQuery Partition { get; }
    public ITerrainLogic Terrain { get; }
    public IPlayerList Players { get; }
    public IAssetStore Assets { get; }
    public ISimEvents Events { get; }

    private sealed class GameLogicAdapter : IGameLogic
    {
        private readonly IGameEngine _engine;

        public GameLogicAdapter(IGameEngine engine) => _engine = engine;

        public GameObject GetObjectById(ObjectId id) => _engine.GameLogic.GetObjectById(id);

        // GameLogic's backing list is indexed by ObjectId, so its iteration is already
        // ascending ObjectId; nulls (destroyed slots) are filtered by the property.
        public IEnumerable<GameObject> ObjectsAscendingId => _engine.GameLogic.Objects;

        public GameObject CreateObjectAt(ObjectDefinition definition, Player owner, GameObject at)
        {
            // Float boundary: Transform/TransformMatrix are unmigrated substrate. A ported
            // module names the template, the owner and the object to stand at; the matrix
            // copy happens here so no float or System.Numerics type reaches [SimState] code.
            var spawned = _engine.GameLogic.CreateObject(definition, owner);
            spawned.SetTransformMatrix(at.TransformMatrix);
            spawned.UpdateColliders();
            return spawned;
        }
    }

    private sealed class PartitionAdapter : IPartitionQuery
    {
        private readonly IGameEngine _engine;

        public PartitionAdapter(IGameEngine engine) => _engine = engine;

        public IEnumerable<GameObject> QueryObjectsInRadius(GameObject center, Fix64 radius)
        {
            // Float boundary: the quadtree is unmigrated substrate. The float radius is
            // derived from the quantized Fix64 exactly once, here.
            var results = new List<GameObject>(
                _engine.Quadtree.FindNearby(center, center.Transform, radius.ToFloatForDisplay()));

            // The determinism contract: ascending ObjectId, never spatial-bucket order.
            results.Sort(static (a, b) => a.Id.Index.CompareTo(b.Id.Index));
            return results;
        }
    }

    private sealed class TerrainAdapter : ITerrainLogic
    {
    }

    private sealed class PlayerListAdapter : IPlayerList
    {
        private readonly IGameEngine _engine;

        public PlayerListAdapter(IGameEngine engine) => _engine = engine;

        public Player NeutralPlayer => _engine.Game.PlayerManager.NeutralPlayer;
    }

    private sealed class AssetStoreAdapter : IAssetStore
    {
    }

    private sealed class SimEventsAdapter : ISimEvents
    {
        public void FireFXAtObject(string fxListName, ObjectId objectId)
        {
            // Client-side FX dispatch is not wired yet; events are outputs with no
            // determinism obligation (S8), so a no-op is contract-legal.
        }
    }
}
