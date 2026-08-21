// One cell of the deterministic partition grid (GPL PartitionCell, fresh code).
// Holds the per-player shroud ledger and the list of entries whose footprint covers
// the cell (the COI list, flattened). The four shroud algorithms that mutate the
// ledger live on SimPartitionGrid so their edge-trigger bookkeeping stays in one place.

using System.Collections.Generic;
using OpenSage.SimCore;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SimPartitionCell
{
    private readonly PartitionShroudLevel[] _shroudLevels;

    /// <summary>Entries covering this cell (insertion order; queries dedupe by stamp and
    /// re-sort by ObjectId, so this order is never observable).</summary>
    internal readonly List<SimPartitionEntry> Entries = new();

    internal SimPartitionCell(int playerCount)
    {
        _shroudLevels = new PartitionShroudLevel[playerCount];
        for (var i = 0; i < playerCount; i++)
        {
            // GPL PartitionCell ctor: default is passive shroud - current 1, active 0.
            _shroudLevels[i].CurrentShroud = 1;
            _shroudLevels[i].ActiveShroudLevel = 0;
        }
    }

    internal ref PartitionShroudLevel ShroudLevelFor(int playerIndex)
        => ref _shroudLevels[playerIndex];

    /// <summary>GPL PartitionCell::getShroudStatusForPlayer: 1 = shrouded, 0 = fogged,
    /// negative = clear (someone is looking).</summary>
    public CellShroudStatus GetShroudStatusForPlayer(int playerIndex)
    {
        var current = _shroudLevels[playerIndex].CurrentShroud;
        if (current == 1)
        {
            return CellShroudStatus.Shrouded;
        }
        return current == 0 ? CellShroudStatus.Fogged : CellShroudStatus.Clear;
    }
}
