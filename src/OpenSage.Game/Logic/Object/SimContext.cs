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
        Players = new PlayerListAdapter();
        Assets = new AssetStoreAdapter();
        Events = new SimEventsAdapter(engine);
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

    /// <summary>
    /// The client-bound event sink. Settable because events are OUTPUTS with no determinism
    /// obligation (S8): a host may redirect them without touching the simulation. The headless
    /// test host uses this to observe that a module fired the event it was supposed to fire -
    /// otherwise "fire-and-forget" would also mean "untestable".
    /// </summary>
    public ISimEvents Events { get; private set; }

    internal void SetEventSink(ISimEvents events) => Events = events;

    private sealed class GameLogicAdapter : IGameLogic
    {
        private readonly IGameEngine _engine;

        public GameLogicAdapter(IGameEngine engine) => _engine = engine;

        public GameObject GetObjectById(ObjectId id) => _engine.GameLogic.GetObjectById(id);

        // GameLogic's backing list is indexed by ObjectId, so its iteration is already
        // ascending ObjectId; nulls (destroyed slots) are filtered by the property.
        public IEnumerable<GameObject> ObjectsAscendingId => _engine.GameLogic.Objects;

        // Not a float boundary and not order-sensitive: GameLogic.DestroyObject only sets the
        // Destroyed status and appends to the destroy list, which is drained in creation
        // order at end of frame.
        public void DestroyObject(GameObject gameObject) => _engine.GameLogic.DestroyObject(gameObject);

        public IReadOnlyList<GameObject> CreateFromObjectCreationList(
            ObjectCreationList list,
            GameObject primary,
            GameObject secondary)
        {
            // Float boundary: object creation (offsets, dispositions, lifetimes) is
            // unmigrated substrate, so the whole call happens on this side of the seam and
            // the module only ever sees the resulting GameObjects. NOTE (finding
            // F-CODIE-1): the nuggets' lifetime roll still draws GameLogic.Random, the
            // legacy stream (D-6); the two streams collapse at F11.
            if (list is null)
            {
                return [];
            }

            return _engine.ObjectCreationLists.Create(list, primary, _engine, secondary);
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
    }

    private sealed class AssetStoreAdapter : IAssetStore
    {
    }

    /// <summary>
    /// The output side of the seam: names come in, the client-side FX system runs. This is a
    /// float-boundary adapter - it reads transforms and hands them to FXList, which is
    /// unmigrated client code. Nothing here feeds back into sim state, so none of it carries a
    /// determinism obligation (S8); a missing FX list is silently nothing, exactly as the
    /// original's null check does it.
    /// </summary>
    private sealed class SimEventsAdapter : ISimEvents
    {
        private readonly IGameEngine _engine;

        public SimEventsAdapter(IGameEngine engine) => _engine = engine;

        public void FireFXAtObject(string fxListName, ObjectId objectId) =>
            FireFXAtObject(fxListName, objectId, ObjectId.Invalid);

        public void FireFXAtObject(string fxListName, ObjectId objectId, ObjectId sourceObjectId)
        {
            var subject = Resolve(fxListName, objectId, out var fxList);
            if (subject is null)
            {
                return;
            }

            // sourceObjectId is the original's doFXObj SECONDARY object. OpenSAGE's
            // FXListExecutionContext carries only one transform, so no nugget can consume a
            // secondary yet; the id is accepted here (rather than being dropped at the module
            // call site) so that adding the second transform later is a change to this file
            // alone. Recorded as a finding, not invented into the context.
            _ = sourceObjectId;

            fxList.Execute(new FX.FXListExecutionContext(
                subject.Rotation,
                subject.Translation,
                _engine));
        }

        public void FireFXAtObjectPosition(string fxListName, ObjectId objectId)
        {
            var subject = Resolve(fxListName, objectId, out var fxList);
            if (subject is null)
            {
                return;
            }

            // Unoriented (doFXPos): identity rotation, the object's position only.
            fxList.Execute(new FX.FXListExecutionContext(
                System.Numerics.Quaternion.Identity,
                subject.Translation,
                _engine));
        }

        private GameObject Resolve(string fxListName, ObjectId objectId, out FX.FXList fxList)
        {
            fxList = null;
            if (string.IsNullOrEmpty(fxListName))
            {
                return null;
            }

            var subject = _engine.GameLogic.GetObjectById(objectId);
            if (subject is null)
            {
                return null;
            }

            fxList = _engine.AssetStore.FXLists.GetByName(fxListName);
            return fxList is null ? null : subject;
        }
    }
}
