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
