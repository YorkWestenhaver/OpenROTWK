// Partition / Vision / LOS / Shroud — shared vocabulary (build-roadmap pillar
// partition-vision, S3).
//
// Behavioral reference (clean-room, semantics only, fresh code): generals-gpl GeneralsMD
// GameLogic/System PartitionManager.h/.cpp — CellShroudStatus / ObjectShroudStatus /
// ShroudLevel / DistanceCalculationType. All sim math is Fix64 (api-freeze-v1 F1-F4);
// every type here is [SimState], so SIMCORE001-010 run as errors over this file.

using OpenSage.SimCore;

namespace OpenSage.Logic.Object;

/// <summary>
/// What one partition cell looks like TO ONE PLAYER (GPL <c>CellShroudStatus</c>).
/// There is no absolute answer - shrouded only means "shrouded for him".
/// </summary>
public enum CellShroudStatus : byte
{
    Clear = 0,     // someone on the player's looking mask actively sees the cell
    Fogged = 1,    // explored, nobody actively looking (GPL CELLSHROUD_FOGGED)
    Shrouded = 2,  // never explored, or actively re-shrouded (GPL CELLSHROUD_SHROUDED)
}

/// <summary>
/// What a whole (possibly multi-cell) object looks like to one player
/// (GPL <c>ObjectShroudStatus</c>). Numbering is GPL's: the fog-memory rule compares
/// previous &lt; Fogged, so the relative order of Clear/PartialClear/Fogged is contract.
/// </summary>
public enum PartitionObjectShroudStatus : byte
{
    Invalid = 0,                  // indeterminate, recompute on next ask
    Clear = 1,                    // no covered cell is fogged or shrouded
    PartialClear = 2,             // at least one covered cell is clear
    Fogged = 3,                   // covered cells are fogged (none clear)
    Shrouded = 4,                 // every covered cell is shrouded (or object off-map)
    InvalidButPreviousValid = 5,  // recompute, but keep the remembered previous status
}

/// <summary>
/// GPL <c>DistanceCalculationType</c>: how the query family measures distance.
/// Bounding-sphere variants measure edge-to-edge (distance minus the two bounding
/// radii, clamped at zero).
/// </summary>
public enum PartitionDistanceType : byte
{
    Center2D = 0,
    Center3D = 1,
    BoundingSphere2D = 2,
    BoundingSphere3D = 3,
}

/// <summary>
/// One cell's per-player shroud ledger (GPL <c>ShroudLevel</c>):
/// <see cref="CurrentShroud"/> — 1 = shrouded, 0 = fogged (explored, nobody looking),
/// negative = minus the count of active lookers;
/// <see cref="ActiveShroudLevel"/> — 0 = passive shroud only, positive = count of
/// active shrouders (shroud generation).
/// </summary>
[SimState]
public struct PartitionShroudLevel
{
    public short CurrentShroud;
    public short ActiveShroudLevel;
}

/// <summary>
/// The grid's read-only view of the player roster: ally masks for the vision model and
/// viewer→owner relationships for the fogged-object downgrade rules. Implemented by the
/// engine adapter (and directly by tests); masks are player-index bitmasks
/// (<c>1u &lt;&lt; playerIndex</c>), identical on every peer.
/// </summary>
[SimState]
public interface IPartitionPlayerView
{
    int PlayerCount { get; }

    /// <summary>
    /// The players who see through this owner's eyes: the owner itself plus every player
    /// whose relationship to the owner is Allies (GPL Object::look mask construction).
    /// </summary>
    uint GetLookerMask(int ownerPlayerIndex);

    /// <summary>
    /// The players a reveal-to-all / shroud-generation effect reaches: everyone whose
    /// relationship to the owner is Enemies or Neutral (GPL getPlayersWithRelationship
    /// ALLOW_ENEMIES | ALLOW_NEUTRAL).
    /// </summary>
    uint GetEnemyAndNeutralMask(int ownerPlayerIndex);

    /// <summary>Viewer's relationship to the owner (GPL player->getRelationship).</summary>
    RelationshipType GetRelationship(int viewerPlayerIndex, int ownerPlayerIndex);
}
