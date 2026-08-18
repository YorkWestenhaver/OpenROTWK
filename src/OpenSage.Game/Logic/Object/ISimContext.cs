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
public interface ISimEvents
{
    /// <summary>Request the named FX list at an object's position (e.g. UnitHealPulseFX).</summary>
    void FireFXAtObject(string fxListName, ObjectId objectId);
}
