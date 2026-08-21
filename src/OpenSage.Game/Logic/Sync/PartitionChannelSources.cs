// The Partition and Shroud CRC channel sources (api-freeze-v1 F8 channels 2 and 4),
// wired by sys/partition-wiring (R9, closes the F-PV-1 channel-walk item).
//
// Partition (channel 2): the grid's own walk - geometry guards, the per-cell per-player
// shroud ledger (GPL "checksum the shroud to catch shroud cheaters"), and the pending
// timed-undo queue (SimPartitionGrid.Xfer).
//
// Shroud (channel 4): the per-object sighting state - each registered entry's three
// SightingInfos and per-player shroudedness-previous (SimPartitionEntry.Xfer), walked in
// ascending ObjectId (EntriesAscendingId is sorted by construction), each wrapped in a
// BeginModule/EndModule frame keyed by the ObjectId so a diverging object is nameable in
// deep dumps.
//
// Both fold nothing while no partition host exists yet (before the first object): an
// empty channel is a stable CRC, and the host appears at the same sim point on every peer
// (the first CreateObject), so activation is never a desync source.

using OpenSage.Logic.Object;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Sync;

internal sealed class PartitionChannelSource : ICrcChannelSource
{
    private readonly GameLogic _gameLogic;

    internal PartitionChannelSource(GameLogic gameLogic)
    {
        _gameLogic = gameLogic;
    }

    public CrcChannel Channel => CrcChannel.Partition;

    public bool IsActive => true;

    public void Xfer(IXfer xfer)
    {
        _gameLogic.SimPartitionIfCreated?.Grid.Xfer(xfer);
    }
}

internal sealed class ShroudChannelSource : ICrcChannelSource
{
    private readonly GameLogic _gameLogic;

    internal ShroudChannelSource(GameLogic gameLogic)
    {
        _gameLogic = gameLogic;
    }

    public CrcChannel Channel => CrcChannel.Shroud;

    public bool IsActive => true;

    public void Xfer(IXfer xfer)
    {
        var host = _gameLogic.SimPartitionIfCreated;
        if (host is null)
        {
            return;
        }

        var entries = host.Grid.EntriesAscendingId;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            xfer.BeginModule(new XferModuleId(
                entry.Id.Index,
                0,
                "Shroud",
                nameof(SimPartitionEntry)));
            entry.Xfer(xfer);
            xfer.EndModule();
        }
    }
}
