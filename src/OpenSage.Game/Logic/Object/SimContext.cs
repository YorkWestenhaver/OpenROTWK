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
using System.Numerics;
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
        GameLogic = new GameLogicAdapter(engine, CompletedSpecialPowers);
        Partition = new PartitionAdapter(engine);
        Terrain = new TerrainAdapter(engine);
        Players = new PlayerListAdapter(engine);
        Assets = new AssetStoreAdapter(engine);
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

    /// <summary>
    /// The completed-special-power log (GPL <c>ScriptEngine::m_finishedSpecialPowers</c>).
    /// Lives here only until the script engine ports - see SPCD-1 in
    /// research/die/SpecialPowerCompletionDie.md.
    /// </summary>
    internal CompletedSpecialPowerLog CompletedSpecialPowers { get; } = new();

    private sealed class GameLogicAdapter : IGameLogic
    {
        private readonly IGameEngine _engine;
        private readonly CompletedSpecialPowerLog _completedSpecialPowers;

        public GameLogicAdapter(IGameEngine engine, CompletedSpecialPowerLog completedSpecialPowers)
        {
            _engine = engine;
            _completedSpecialPowers = completedSpecialPowers;
        }

        public GameObject GetObjectById(ObjectId id) => _engine.GameLogic.GetObjectById(id);

        public GameObject CreateObjectAt(ObjectDefinition definition, Player owner, GameObject at, Fix64 orientation)
        {
            var created = _engine.GameLogic.CreateObject(definition, owner);
            if (created is null)
            {
                return null;
            }

            // Float boundary: object transforms are unmigrated substrate. The quantized angle
            // is turned into a rotation exactly once, here, and the donor's translation is
            // copied verbatim (no arithmetic, so no rounding of its own).
            created.UpdateTransform(
                at.Transform.Translation,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, orientation.ToFloatForDisplay()));
            created.Layer = at.Layer;
            created.UpdateColliders();
            return created;
        }

        public GameObject CreateObjectAt(ObjectDefinition definition, Player owner, GameObject at, in FixVector3 offset, Fix64 orientation)
        {
            var created = _engine.GameLogic.CreateObject(definition, owner);
            if (created is null)
            {
                return null;
            }

            // Float boundary (D-7, R12): the donor's translation is copied verbatim, then the
            // Fix64 offset - computed entirely module-side - is converted to float exactly
            // once, here, and added on top. Same single-crossing shape as the orientation-only
            // overload above.
            var donor = at.Transform.Translation;
            var translation = donor + new Vector3(
                offset.X.ToFloatForDisplay(),
                offset.Y.ToFloatForDisplay(),
                offset.Z.ToFloatForDisplay());

            created.UpdateTransform(
                translation,
                Quaternion.CreateFromAxisAngle(Vector3.UnitZ, orientation.ToFloatForDisplay()));
            created.Layer = at.Layer;
            created.UpdateColliders();
            return created;
        }

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

        public void NotifyOfCompletedSpecialPower(int playerIndex, string specialPowerName, ObjectId sourceObjectId)
            => _completedSpecialPowers.Add(playerIndex, specialPowerName, sourceObjectId);

        // S5 pathfinding (additive): route to the GameLogic-owned pathfind host.
        public bool PathfindQueueForPath(ObjectId id)
            => _engine.GameLogic.SimPathfind.QueueForPath(id);

        public OpenSage.Logic.Object.Pathfind.SimPathfindGrid PathfindGrid
            => _engine.GameLogic.SimPathfind.Grid;
    }

    private sealed class PartitionAdapter : IPartitionQuery
    {
        private readonly IGameEngine _engine;

        public PartitionAdapter(IGameEngine engine) => _engine = engine;

        public IEnumerable<GameObject> QueryObjectsInRadius(GameObject center, Fix64 radius)
        {
            // S3 partition wiring (sys/partition-wiring, closes F-PV-1): the deterministic
            // Fix64 SimPartitionGrid is the partition authority; the float quadtree no
            // longer serves sim queries. Same signature, same ascending-ObjectId contract;
            // the measure is GPL Center2D with GPL's strict '<' predicate (the old
            // quadtree collider test was inclusive - reconciliation recorded in
            // research/partition-wiring-r9.md).
            return _engine.GameLogic.SimPartition.QueryObjectsInRadius(center, radius);
        }

        // R9 mod/stealthdetectorupdate (additive, F-SDU-1): vision range crossing. The
        // object's vision range is float substrate; it is quantized through the F4 wire
        // boundary here, exactly once, so only a Fix64 reaches the [SimState] caller (D-7).
        // (R9 integration: GameObject.VisionRange is now the Fix64 facade from
        // mod/enemynearupdate — same FromWireFloat quantization, applied exactly once there.)
        public OpenSage.SimCore.Numerics.Fix64 GetVisionRange(GameObject gameObject)
            => gameObject.VisionRange;
    }

    private sealed class TerrainAdapter : ITerrainLogic
    {
        private readonly IGameEngine _engine;

        public TerrainAdapter(IGameEngine engine) => _engine = engine;

        public bool IsSignificantlyAboveTerrain(GameObject gameObject)
        {
            // Float boundary (D-7): height-above-terrain and the gravity constant are both
            // unmigrated substrate. The comparison happens entirely on that side and only a
            // bool crosses, so no float ever reaches the [SimState] caller.
            return gameObject.IsSignificantlyAboveTerrain;
        }

        public OpenSage.SimCore.Numerics.Fix64 GetGroundHeight(
            in OpenSage.SimCore.Numerics.FixVector3 position)
        {
            // Float boundary (D-7, S2 locomotor): the heightmap sample is float substrate;
            // the result crosses back through the F4 wire boundary so every peer quantizes
            // identical float bits to identical Fix64.
            var height = _engine.Game.TerrainLogic.GetGroundHeight(
                position.X.ToFloatForDisplay(),
                position.Y.ToFloatForDisplay());
            return OpenSage.SimCore.Numerics.Fix64.FromWireFloat(
                System.BitConverter.SingleToUInt32Bits(height));
        }
    }

    private sealed class PlayerListAdapter : IPlayerList
    {
        private readonly IGameEngine _engine;

        public PlayerListAdapter(IGameEngine engine) => _engine = engine;

        public Player NeutralPlayer => _engine.Game.PlayerManager.NeutralPlayer;

        public int GetPlayerIndex(OpenSage.Logic.Player player)
            => _engine.Game.PlayerManager.GetPlayerIndex(player);
    }

    private sealed class AssetStoreAdapter : IAssetStore
    {
        private readonly IGameEngine _engine;

        public AssetStoreAdapter(IGameEngine engine) => _engine = engine;

        // S6 horde system: template lookup for banner-carrier respawn. Immutable parsed
        // data behind the seam; no float crossing.
        public ObjectDefinition GetObjectDefinition(string name) =>
            _engine.AssetLoadContext.AssetStore.ObjectDefinitions.GetByName(name);
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

        public void FireUnitSoundAtObject(string unitSpecificSoundKey, ObjectId objectId)
        {
            // Same story as FireFXAtObject: the client-bound event queue does not exist yet.
            // Recording the call is what a ported module owes; playing it is the client's.
        }

        // R12 (UnitCrateCollide): a global MiscAudio sting, not per-object, so unlike the FX
        // methods above there is no transform to read - PlayAudioEvent(string) resolves the
        // event by name directly. Null-tolerant (AudioSystem, MiscAudio scope) so the headless
        // sim host can collect crates same as GameObject.OnVeterancyLevelChanged does for
        // UnitPromoted.
        public void FireCrateFreeUnitPickupSound()
        {
            var soundName = _engine.AssetLoadContext.AssetStore.MiscAudio.Current?.CrateFreeUnit;
            if (string.IsNullOrEmpty(soundName))
            {
                return;
            }
            _engine.AudioSystem?.PlayAudioEvent(soundName);
        }

        public void FireParticleSystemAtObject(string particleSystemName, ObjectId objectId, string bone, bool randomBone)
        {
            // Output side of the seam (S8): create the emitter and attach it to the object's
            // transform. Bone resolution and the randomBone pick are client model concerns
            // (the emitter follows the object's world matrix here; a bone-relative offset and
            // any random-bone selection belong to the unmigrated client draw code - see
            // F-TDF-2). A missing template or object is silently nothing, exactly as the
            // original's null checks do it. The created system's lifetime is the client's; the
            // sim keeps no id (F-TDF-1).
            if (string.IsNullOrEmpty(particleSystemName))
            {
                return;
            }

            var subject = _engine.GameLogic.GetObjectById(objectId);
            if (subject is null)
            {
                return;
            }

            var template = _engine.AssetStore.FXParticleSystemTemplates.GetByName(particleSystemName);
            if (template is null || _engine.ParticleSystems is null)
            {
                return;
            }

            _ = randomBone;
            _ = bone;

            _engine.ParticleSystems.Create(template, subject.TransformMatrix);
        }

        // R12 (HeightDieUpdate): same story as FireUnitSoundAtObject - the event is recorded
        // as owed, but ParticleSystemManager has no object-attachment tracking yet (F-HDU-1),
        // so there is nothing here to tear down.
        public void DestroyAttachedParticleSystems(ObjectId objectId)
        {
            _ = objectId;
        }
    }
}

/// <summary>One completed-special-power record (GPL <c>AsciiStringObjectIDPair</c>).</summary>
internal readonly record struct CompletedSpecialPower(int PlayerIndex, string Name, ObjectId SourceObjectId);

/// <summary>
/// The per-match completed-special-power log: GPL's <c>ScriptEngine::m_finishedSpecialPowers</c>
/// (one list per player) flattened into a single append-ordered list, which is equivalent for
/// the two operations the engine performs on it - append, and the scan
/// <c>isSpecialPowerComplete</c> does - and keeps the "append order is sim order" property that
/// makes the log deterministic.
/// <para>
/// NOT sim state under the freeze yet: it is not walked by any Xfer and not persisted, because
/// its real owner (the script engine) has not been ported. Filed as SPCD-1.
/// </para>
/// </summary>
internal sealed class CompletedSpecialPowerLog
{
    private readonly List<CompletedSpecialPower> _entries = new();

    public IReadOnlyList<CompletedSpecialPower> Entries => _entries;

    public void Add(int playerIndex, string specialPowerName, ObjectId sourceObjectId)
        => _entries.Add(new CompletedSpecialPower(playerIndex, specialPowerName, sourceObjectId));

    /// <summary>
    /// GPL <c>ScriptEngine::isSpecialPowerComplete</c>: first matching entry in append order,
    /// where an invalid <paramref name="sourceObjectId"/> matches any source. Removing on match
    /// is the script condition's "consume" mode.
    /// </summary>
    public bool IsComplete(int playerIndex, string specialPowerName, ObjectId sourceObjectId, bool removeFromList)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.PlayerIndex != playerIndex || entry.Name != specialPowerName)
            {
                continue;
            }

            if (sourceObjectId.IsValid && entry.SourceObjectId != sourceObjectId)
            {
                continue;
            }

            if (removeFromList)
            {
                _entries.RemoveAt(i);
            }
            return true;
        }

        return false;
    }
}
