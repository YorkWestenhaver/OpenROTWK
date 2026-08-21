// One registered object in the deterministic partition grid — the fresh analog of GPL
// PartitionData (cell coverage + per-player shroudedness cache) fused with the Object's
// partition-facing sighting state (GPL Object::m_partitionLastLook /
// m_partitionRevealAllLastLook / m_partitionLastShroud, each a SightingInfo).
//
// All sim math Fix64; [SimState] file. The entry never touches the float substrate: the
// engine-side bridge quantizes a GameObject once (F4 wire boundary) and hands the grid
// pure values.

using System;
using System.Collections.Generic;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

/// <summary>
/// Immutable registration facts about an object (quantized once at the seam).
/// </summary>
[SimState]
public readonly struct PartitionObjectInfo
{
    /// <summary>The object's id — the deterministic iteration key of every query.</summary>
    public readonly ObjectId Id;

    /// <summary>Bounding-circle radius of the object's footprint (GPL bounding circle).</summary>
    public readonly Fix64 BoundingRadius;

    /// <summary>Geometry top above the position — the LOS "eye height" (GPL getMaxHeightAbovePosition).</summary>
    public readonly Fix64 HeightAbovePosition;

    public readonly int OwnerPlayerIndex;

    /// <summary>KindOf IMMOBILE — fog-memory rule input (GPL getShroudedStatus).</summary>
    public readonly bool IsImmobile;

    /// <summary>KindOf MINE — always shrouded inside fog regardless of memory (GPL).</summary>
    public readonly bool IsMine;

    /// <summary>KindOf REVEAL_TO_ALL — the look mask becomes every player (GPL Object::look).</summary>
    public readonly bool RevealToAll;

    public PartitionObjectInfo(
        ObjectId id,
        Fix64 boundingRadius,
        Fix64 heightAbovePosition,
        int ownerPlayerIndex,
        bool isImmobile = false,
        bool isMine = false,
        bool revealToAll = false)
    {
        Id = id;
        BoundingRadius = boundingRadius;
        HeightAbovePosition = heightAbovePosition;
        OwnerPlayerIndex = ownerPlayerIndex;
        IsImmobile = isImmobile;
        IsMine = isMine;
        RevealToAll = revealToAll;
    }
}

/// <summary>
/// One remembered area interaction with the shroud (GPL <c>SightingInfo</c>): where the
/// object last looked/shrouded, how far, and for whom. Invalid = radius zero (GPL isInvalid).
/// </summary>
[SimState]
public struct PartitionSightingInfo
{
    public Fix64 X;
    public Fix64 Y;
    public Fix64 Radius;
    public uint PlayerMask;

    public readonly bool IsInvalid => Radius == Fix64.Zero;

    public void Reset()
    {
        X = Fix64.Zero;
        Y = Fix64.Zero;
        Radius = Fix64.Zero;
        PlayerMask = 0;
    }

    public void Xfer(IXfer xfer, string name)
    {
        xfer.XferFix64(name + ".X", ref X);
        xfer.XferFix64(name + ".Y", ref Y);
        xfer.XferFix64(name + ".Radius", ref Radius);
        xfer.XferUInt(name + ".PlayerMask", ref PlayerMask);
    }
}

[SimState]
public sealed class SimPartitionEntry
{
    // ---- registration facts (rebuilt on load by re-registration, per GPL: partition
    //      data is not persisted, shroud + sighting infos are) ----
    public PartitionObjectInfo Info { get; }

    /// <summary>Current sim position (Fix64-authoritative for this system).</summary>
    public FixVector3 Position { get; internal set; }

    /// <summary>
    /// Shroud-clearing range (defaults to vision range at the seam - GPL "backwards
    /// compatible and perfectly logical default"). Mutable: upgrades change it via
    /// <see cref="SimPartitionGrid.SetShroudClearingRange"/>.
    /// </summary>
    public Fix64 ShroudClearingRange { get; internal set; }

    /// <summary>Template ShroudRevealToAllRange (0 = none).</summary>
    public Fix64 RevealToAllRange { get; internal set; }

    /// <summary>Active shroud-generation range (0 = none) - GPL Object::getShroudRange.</summary>
    public Fix64 ShroudRange { get; internal set; }

    /// <summary>
    /// When false the entry does not look at all (GPL: dead / under-non-garrisonable-
    /// container objects don't reveal shroud). Set via <see cref="SimPartitionGrid.SetCanLook"/>.
    /// </summary>
    public bool CanLook { get; internal set; } = true;

    // ---- cell coverage (GPL COI list, ours: indices into the grid's cell array) ----
    internal readonly List<int> CoveredCells = new();

    // ---- query de-duplication stamp (GPL doneFlag) ----
    internal uint QueryStamp;

    // ---- sighting state (GPL Object's three SightingInfos) ----
    internal PartitionSightingInfo LastLook;
    internal PartitionSightingInfo LastRevealAllLook;
    internal PartitionSightingInfo LastShroud;

    // ---- per-player shroudedness cache (GPL PartitionData) ----
    private readonly PartitionObjectShroudStatus[] _shroudedness;
    private readonly PartitionObjectShroudStatus[] _shroudednessPrevious;
    private readonly bool[] _everSeenByPlayer;

    internal SimPartitionEntry(in PartitionObjectInfo info, int playerCount)
    {
        Info = info;
        _shroudedness = new PartitionObjectShroudStatus[playerCount];
        _shroudednessPrevious = new PartitionObjectShroudStatus[playerCount];
        _everSeenByPlayer = new bool[playerCount];
        for (var i = 0; i < playerCount; i++)
        {
            _shroudedness[i] = PartitionObjectShroudStatus.Invalid;
            _shroudednessPrevious[i] = PartitionObjectShroudStatus.Invalid;
        }
    }

    public ObjectId Id => Info.Id;

    internal void InvalidateShroudedStatus(int playerIndex)
    {
        if (_shroudedness[playerIndex] != PartitionObjectShroudStatus.InvalidButPreviousValid)
        {
            _shroudedness[playerIndex] = PartitionObjectShroudStatus.Invalid;
        }
    }

    /// <summary>
    /// Restores the remembered previous status after load (GPL
    /// friend_setShroudednessPrevious): ever-seen is implied by "was anything but
    /// shrouded", and a pending recompute keeps the restored previous value.
    /// </summary>
    internal void SetShroudednessPrevious(int playerIndex, PartitionObjectShroudStatus status)
    {
        _shroudednessPrevious[playerIndex] = status;
        _everSeenByPlayer[playerIndex] = status != PartitionObjectShroudStatus.Shrouded;
        if (_shroudedness[playerIndex] == PartitionObjectShroudStatus.Invalid)
        {
            _shroudedness[playerIndex] = PartitionObjectShroudStatus.InvalidButPreviousValid;
        }
    }

    internal PartitionObjectShroudStatus GetShroudednessPrevious(int playerIndex)
        => _shroudednessPrevious[playerIndex];

    /// <summary>
    /// The whole-object shroud status for one player (GPL
    /// PartitionData::getShroudedStatus, minus the client-side ghost-object snapshots -
    /// those are draw concerns; see design note finding F-PV-3). Cached until a covered
    /// cell's status edge-triggers an invalidation.
    /// </summary>
    public PartitionObjectShroudStatus GetShroudedStatus(int playerIndex, SimPartitionGrid grid)
    {
        var cached = _shroudedness[playerIndex];
        if (cached != PartitionObjectShroudStatus.Invalid &&
            cached != PartitionObjectShroudStatus.InvalidButPreviousValid)
        {
            return cached;
        }

        var updatePrevious = cached != PartitionObjectShroudStatus.InvalidButPreviousValid;

        var shroudedCells = 0;
        var foggedCells = 0;
        for (var i = 0; i < CoveredCells.Count; i++)
        {
            switch (grid.GetCellShroudStatusByIndex(CoveredCells[i], playerIndex))
            {
                case CellShroudStatus.Shrouded:
                    shroudedCells++;
                    break;
                case CellShroudStatus.Fogged:
                    foggedCells++;
                    break;
            }
        }

        PartitionObjectShroudStatus status;
        if (CoveredCells.Count == 0)
        {
            // Off the map = no coverage = shrouded, never seen.
            status = PartitionObjectShroudStatus.Shrouded;
            _everSeenByPlayer[playerIndex] = false;
        }
        else if (shroudedCells == CoveredCells.Count)
        {
            status = PartitionObjectShroudStatus.Shrouded;
            _everSeenByPlayer[playerIndex] = false;
        }
        else if (shroudedCells + foggedCells == CoveredCells.Count)
        {
            // Fogged, then the fog-memory downgrades (GPL: neutral movers vanish; enemy
            // units vanish unless an already-seen immobile; mines always vanish).
            status = PartitionObjectShroudStatus.Fogged;
            var relationship = grid.Players.GetRelationship(playerIndex, Info.OwnerPlayerIndex);
            if (relationship == RelationshipType.Neutral)
            {
                if (!Info.IsImmobile)
                {
                    status = PartitionObjectShroudStatus.Shrouded;
                }
            }
            else
            {
                if (!(Info.IsImmobile && _everSeenByPlayer[playerIndex]) || Info.IsMine)
                {
                    status = PartitionObjectShroudStatus.Shrouded;
                }
            }
        }
        else if (shroudedCells == 0 && foggedCells == 0)
        {
            _everSeenByPlayer[playerIndex] = true;
            status = PartitionObjectShroudStatus.Clear;
        }
        else
        {
            _everSeenByPlayer[playerIndex] = true;
            status = PartitionObjectShroudStatus.PartialClear;
        }

        _shroudedness[playerIndex] = status;
        if (CoveredCells.Count > 0 && updatePrevious)
        {
            _shroudednessPrevious[playerIndex] = status;
        }

        return status;
    }

    /// <summary>
    /// The entry's persistent walk: the three sighting infos plus the per-player
    /// previous-shroudedness memory (GPL xfers SightingInfos with the Object and
    /// shroudednessPrevious via friend_setShroudednessPrevious on load). Declaration
    /// order ours (F9); every field Exact - integers and quantized Fix64 only.
    /// </summary>
    public void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        LastLook.Xfer(xfer, "LastLook");
        LastRevealAllLook.Xfer(xfer, "LastRevealAllLook");
        LastShroud.Xfer(xfer, "LastShroud");

        var playerCount = _shroudednessPrevious.Length;
        var playerCountCheck = playerCount;
        xfer.XferInt("PlayerCount", ref playerCountCheck);
        if (playerCountCheck != playerCount)
        {
            throw new InvalidOperationException(
                $"SimPartitionEntry player count mismatch: {playerCountCheck} != {playerCount}");
        }

        for (var i = 0; i < playerCount; i++)
        {
            var previous = _shroudednessPrevious[i];
            xfer.XferEnum($"ShroudednessPrevious[{i}]", ref previous);
            if (xfer.Mode == XferMode.Load)
            {
                SetShroudednessPrevious(i, previous);
            }
        }
    }
}
