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
    /// Live objects in ascending ObjectId order - the one blessed whole-world iteration
    /// (iteration order is never a desync source, design-module-api §6).
    /// </summary>
    IEnumerable<GameObject> ObjectsAscendingId { get; }
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

/// <summary>Fix64-valued terrain view. Empty until the first terrain-consuming port.</summary>
public interface ITerrainLogic
{
}

/// <summary>Player roster view. Empty until the first player-consuming port.</summary>
public interface IPlayerList
{
}

/// <summary>Immutable parsed-data view. Empty until the first asset-consuming port.</summary>
public interface IAssetStore
{
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
}
