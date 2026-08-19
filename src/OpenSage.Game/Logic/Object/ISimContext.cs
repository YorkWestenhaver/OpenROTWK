// The module-facing sim context (api-freeze-v1 §3 / seam S8; design-module-api §1.2).
//
// ISimContext is the ONLY door from a ported behavior module to the rest of the simulation.
// Its member list is frozen; the member interfaces below start minimal and grow one member at
// a time as porting tasks need them (their surfaces are deliberately not frozen).
// Deliberately absent, forever: audio, rendering, UI, wall-clock, file system, network,
// System.Random (S8).

using System.Collections.Generic;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Rng;

namespace OpenSage.Logic.Object;

public interface ISimContext
{
    /// <summary>The 5 Hz logic frame counter (F6).</summary>
    LogicFrame CurrentFrame { get; }

    /// <summary>The logic RNG stream, draw-counted (S3; conformance channel 5).</summary>
    ISimRandom GameLogicRandom { get; }

    /// <summary>Object lookup by ObjectId; spawn/destroy requests.</summary>
    IGameLogic GameLogic { get; }

    /// <summary>Deterministic spatial queries.</summary>
    IPartitionQuery Partition { get; }

    /// <summary>Heights, cliffs - Fix64-valued view.</summary>
    ITerrainLogic Terrain { get; }

    IPlayerList Players { get; }

    /// <summary>Immutable parsed data (templates, weapons, FX ids).</summary>
    IAssetStore Assets { get; }

    /// <summary>
    /// Fire-and-forget FX/sound/EVA event queue, drained client-side. Events are outputs,
    /// never sim inputs, so they carry no determinism obligation (S8).
    /// </summary>
    ISimEvents Events { get; }
}

/// <summary>Module-facing slice of the game logic. Grows one member per porting need.</summary>
public interface IGameLogic
{
    GameObject GetObjectById(ObjectId id);

    /// <summary>
    /// Spawns a new object of <paramref name="definition"/> owned by <paramref name="owner"/>,
    /// standing where <paramref name="at"/> stands (same position and pathfind layer) and facing
    /// <paramref name="orientation"/> radians. Returns null when the definition is null.
    /// <para>
    /// The spawn-at-a-donor shape (rather than a raw position) is deliberate: position and
    /// orientation are float substrate until the transform subsystem migrates, so the ONE
    /// crossing lives behind this seam in <c>SimContext</c> and never in module code. Modules
    /// that need to place an object somewhere other than a donor's feet must wait for the
    /// FixVector3 transform port - that is a finding, not a cast.
    /// </para>
    /// </summary>
    GameObject CreateObjectAt(ObjectDefinition definition, Player owner, GameObject at, Fix64 orientation);

    /// <summary>
    /// Live objects in ascending ObjectId order - the one blessed whole-world iteration
    /// (iteration order is never a desync source, design-module-api §6).
    /// </summary>
    IEnumerable<GameObject> ObjectsAscendingId { get; }

    /// <summary>
    /// Removes an object from the world: it is marked destroyed immediately and reaped from
    /// the object list at the end of the frame, so a module that walks
    /// <see cref="ObjectsAscendingId"/> later in the SAME frame still sees it (with
    /// <c>IsDestroyed</c> true). Idempotent - destroying a destroyed object is a no-op, which
    /// is what makes a second lethal blow harmless.
    /// Grown for the DestroyDie port (the first destroy-requesting module); the member list
    /// of <see cref="ISimContext"/> itself is unchanged and still frozen.
    /// </summary>
    void DestroyObject(GameObject gameObject);

    /// <summary>
    /// Runs an ObjectCreationList and returns what it created, in creation order (GPL
    /// <c>ObjectCreationList::create(ocl, primary, secondary)</c>, whose callers read the
    /// FIRST created object). An empty list is returned for a null <paramref name="list"/>,
    /// matching the original's null-OCL guard.
    /// </summary>
    /// <param name="primary">The object the list is created for (the dying object, for Die).</param>
    /// <param name="secondary">
    /// The original's second creator argument - for a Die module, the object that dealt the
    /// killing damage. It may be null (no source, or the source already left the world).
    /// </param>
    /// <remarks>
    /// This is the spawn half of the member the frozen ISimContext doc line promises
    /// ("object lookup by ObjectId; spawn/destroy requests"). Object creation is still
    /// unmigrated float substrate (positions, dispositions, lifetimes), so the crossing
    /// lives in the SimContext adapter, never in [SimState] module code (D-7).
    /// </remarks>
    IReadOnlyList<GameObject> CreateFromObjectCreationList(
        ObjectCreationList list,
        GameObject primary,
        GameObject secondary);

    /// <summary>
    /// Spawns a live object from a template, owned by <paramref name="owner"/>, standing at
    /// the position and orientation of <paramref name="at"/>. This is GPL's
    /// <c>newObject</c> + <c>setPosition</c> + <c>setOrientation</c> triple fused into one
    /// member deliberately: the transform is unmigrated float substrate, so the placement
    /// stays behind this seam and never appears in [SimState] module code.
    /// The new object's ObjectId is assigned by the engine's monotonic counter, so the
    /// spawn is a deterministic function of the order in which modules request it.
    /// </summary>
    GameObject CreateObjectAt(ObjectDefinition definition, Player owner, GameObject at);

    /// <summary>
    /// Record that a special power ran to completion, for the script engine's
    /// "player completed special power" condition (GPL
    /// <c>ScriptEngine::notifyOfCompletedSpecialPower</c>: an append to a per-player
    /// (name, sourceObjectId) list, which the condition later scans and optionally
    /// consumes). Appended in sim order, so the log is deterministic.
    /// <para>
    /// MIGRATION NOTE (SpecialPowerCompletionDie port, first consumer): OpenSAGE has no
    /// ported script engine, so the SimContext adapter holds the log and nothing drains
    /// it yet. It moves onto ScriptingSystem - and into that subsystem's persist walk -
    /// when the script engine ports; see research/die/SpecialPowerCompletionDie.md
    /// finding SPCD-1.
    /// </para>
    /// </summary>
    void NotifyOfCompletedSpecialPower(int playerIndex, string specialPowerName, ObjectId sourceObjectId);
}

/// <summary>
/// Deterministic spatial queries: results are always in ascending ObjectId order (frozen
/// contract, design-module-api §6), so partition iteration is never a desync source.
/// NOTE: until the partition subsystem itself migrates to Fix64, the underlying quadtree
/// query runs on the float substrate behind this seam (same-binary deterministic; the
/// cross-arch guarantee arrives with the partition port).
/// </summary>
public interface IPartitionQuery
{
    /// <summary>
    /// All live objects within <paramref name="radius"/> of <paramref name="center"/>
    /// (excluding none - callers filter), ascending ObjectId.
    /// </summary>
    IEnumerable<GameObject> QueryObjectsInRadius(GameObject center, Fix64 radius);
}

/// <summary>Fix64-valued terrain view. Grows one member per porting need.</summary>
public interface ITerrainLogic
{
    /// <summary>
    /// The original's <c>Thing::isSignificantlyAboveTerrain</c>: true when the object is high
    /// enough that it would take more than three logic frames to fall back to the ground
    /// (height above terrain &gt; -9 * gravity). Exposed as a predicate rather than a height so
    /// the comparison stays on one side of the seam.
    /// NOTE (migration): the height and the gravity constant are still float substrate, so
    /// this predicate is same-binary deterministic today and becomes bit-deterministic across
    /// architectures when terrain migrates to Fix64 (the D-7 boundary, same shape as radius).
    /// </summary>
    bool IsSignificantlyAboveTerrain(GameObject gameObject);

    /// <summary>
    /// Ground height at a 2D position, Fix64-valued (grown for the S2 locomotor system:
    /// the integrator's ground clamp and the z-behaviors read it every frame).
    /// NOTE (migration, D-7 shape): the heightmap sample is float substrate behind this
    /// seam; the crossing quantizes through the F4 wire boundary, so it is same-binary
    /// deterministic today and becomes bit-deterministic cross-arch when terrain
    /// migrates. The headless test host's flat map returns exact constants already.
    /// </summary>
    Fix64 GetGroundHeight(in FixVector3 position);
}

/// <summary>Player roster view. Grows one member per porting need.</summary>
public interface IPlayerList
{
    /// <summary>
    /// The neutral player (GPL <c>ThePlayerList-&gt;getNeutralPlayer()</c>), player index 0.
    /// Several structure-death rules key off "is this object still owned by somebody",
    /// which in SAGE is spelled "its controlling player is not the neutral player".
    /// </summary>
    Player NeutralPlayer { get; }

    /// <summary>
    /// The player's index in the match roster (GPL <c>Player::getPlayerIndex</c>). Stable
    /// for the match and identical on every peer, which is why script/AI bookkeeping is
    /// keyed by it rather than by a reference.
    /// </summary>
    int GetPlayerIndex(OpenSage.Logic.Player player);
}

/// <summary>Immutable parsed-data view. Grows one member per porting need.</summary>
public interface IAssetStore
{
    /// <summary>
    /// Object template lookup by name (grown for the S6 horde system: the banner-carrier
    /// respawn path resolves BannerCarriersAllowed template names at runtime). Immutable
    /// parsed data, so the read carries no determinism hazard; null when no such template.
    /// </summary>
    ObjectDefinition GetObjectDefinition(string name);
}

/// <summary>Fire-and-forget client-bound events (S8): outputs only, never sim inputs.</summary>
/// <remarks>
/// Every request names its subject by <see cref="ObjectId"/>, never by position: positions are
/// float substrate, and a <c>[SimState]</c> module may not type one. The adapter reads the
/// transform on the far side of the seam.
/// </remarks>
public interface ISimEvents
{
    /// <summary>
    /// Request the named FX list oriented to an object (e.g. UnitHealPulseFX): the FX takes
    /// the object's position AND rotation.
    /// </summary>
    void FireFXAtObject(string fxListName, ObjectId objectId);

    /// <summary>
    /// Request the named FX list oriented to an object, naming a secondary object as the
    /// effect's source (the original's doFXObj primary/secondary pair - e.g. a death FX
    /// oriented to the corpse and sourced at whatever killed it). An invalid
    /// <paramref name="sourceObjectId"/> means "no source", which is legal.
    /// </summary>
    void FireFXAtObject(string fxListName, ObjectId objectId, ObjectId sourceObjectId);

    /// <summary>
    /// Request the named FX list at an object's position but UNORIENTED (the original's
    /// doFXPos): identity rotation, so the effect ignores which way the object was facing.
    /// </summary>
    void FireFXAtObjectPosition(string fxListName, ObjectId objectId);

    /// <summary>
    /// Request one of an object's UnitSpecificSounds entries by key (e.g. "VoiceEject",
    /// "SoundEject") at that object's position. The key is resolved against the object's own
    /// template client-side, so no audio asset ever crosses into sim code (S8: audio is
    /// deliberately absent from the context; only the event is not).
    /// </summary>
    void FireUnitSoundAtObject(string unitSpecificSoundKey, ObjectId objectId);

    /// <summary>
    /// Request a named particle system attached to an object, placed at a bone (or the
    /// object's own transform when <paramref name="bone"/> is empty). Grown for the
    /// TransitionDamageFX port (the first module whose output is an attached emitter rather
    /// than a one-shot FXList): the original's <c>createParticleSystem</c> +
    /// <c>attachToObject</c> pair. The bone lookup and the <paramref name="randomBone"/>
    /// pick are client-side model concerns resolved on the far side of the seam - a
    /// <c>[SimState]</c> module names the bone and never touches a transform (see
    /// research/modules-r7/TransitionDamageFX.md finding F-TDF-2 on the original's
    /// logic-stream random-bone draw, which cannot be reproduced sim-side and is deliberately
    /// not drawn here). The client owns the created emitter's lifetime, so the sim keeps no
    /// particle-system id (F-TDF-1).
    /// </summary>
    void FireParticleSystemAtObject(string particleSystemName, ObjectId objectId, string bone, bool randomBone);
}
