// R15 bridge P4b (dr-0039, packet BR-P4B): the replay side of the one order pipe.
//
// A replay is the same pipe as the network (OrderIngest.cs: "replays are the same pipe"), so
// this connection is now driven the way a peer's packets arrive: READ AHEAD by the contract
// offset, hand each chunk over stamped with its OWN timecode, and let OrderIngest hold it
// until that frame comes round. NetworkMessageBuffer no longer executes what it drains, so a
// chunk handed over at frame N-2 still executes at frame N and not a moment earlier.
//
// The read-ahead is not a second +2 stamp. It exists so the pending window looks exactly like
// the live case: by the time the loop drains frame N, every chunk for frame N was submitted at
// frame N-2, which is what makes OrderIngest.DrainForFrame's "scheduled for a frame that was
// never dispatched" throw a real lockstep check on replay playback rather than a formality.
//
// dr-0036 canary (R1-W3 exit gate): a replay whose chunk timecodes are {3, 3, 7} must dispatch
// those three orders at frames 3, 3 and 7, with zero DrainForFrame throws.

using System;
using System.Collections.Generic;
using OpenSage.Data.Rep;
using OpenSage.Logic.Orders;
using OpenSage.SimCore.Orders;

namespace OpenSage.Network;

public sealed class ReplayConnection : IConnection
{
    private readonly Queue<ReplayChunk> _chunks = new Queue<ReplayChunk>();

    public ReplayConnection(ReplayFile replayFile)
    {
        foreach (var chunk in replayFile.Chunks)
        {
            _chunks.Enqueue(chunk);
        }
    }

    // Ignore locally generated orders.
    public void Send(uint frame, List<Order> orders) { }

    private static NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    public void Receive(uint frame, Action<uint, Order> packetFn)
    {
        Logger.Trace($"Replay frame {frame}");

        // Read ahead by the contract offset, exactly as a peer's packets arrive ahead of the
        // frame they execute on. Each chunk keeps its own timecode; the ingest side schedules
        // it there.
        var horizon = frame + OrderIngest.OrderSchedulingOffsetInFrames;

        while (_chunks.Count != 0 && _chunks.Peek().Header.Timecode <= horizon)
        {
            var chunk = _chunks.Dequeue();
            packetFn(chunk.Header.Timecode, chunk.Order);
        }
    }

    public void Dispose() { }
}
