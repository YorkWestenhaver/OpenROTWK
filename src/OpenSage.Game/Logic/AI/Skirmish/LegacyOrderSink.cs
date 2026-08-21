#nullable enable

// S9-01 (R15 L3): the live implementation of IAiOrderSink, on the legacy order pipe.
//
// Ruling S9-R15-A puts the strategic brain on the LIVE legacy runtime, so AI orders take the
// same road a human's do: NetworkMessageBuffer.AddLocalOrder -> (net frame) -> OrderProcessor.
// Nothing here is AI-specific except the counters, which the match report (S9-02) reads to
// prove the AI actually ordered something.

using OpenSage.Logic.Orders;
using OpenSage.Network;

namespace OpenSage.Logic.AI.Skirmish;

/// <summary>
/// Submits AI orders into the legacy <see cref="NetworkMessageBuffer"/>, the same entry point
/// the selection system and the command bar use.
/// </summary>
public sealed class LegacyOrderSink : IAiOrderSink
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly IGame _game;
    private bool _warnedNoBuffer;

    /// <summary>Orders successfully handed to the network message buffer.</summary>
    public int SubmittedCount { get; private set; }

    /// <summary>
    /// Orders dropped because there was no message buffer to hand them to. Non-zero here means
    /// the AI is talking to a game that has no order pipe (e.g. a map loaded without a
    /// connection); the match report treats it as a hard failure rather than a quiet zero.
    /// </summary>
    public int DroppedCount { get; private set; }

    public LegacyOrderSink(IGame game)
    {
        _game = game;
    }

    public void Submit(Order order)
    {
        var buffer = _game.NetworkMessageBuffer;
        if (buffer == null)
        {
            DroppedCount++;

            if (!_warnedNoBuffer)
            {
                _warnedNoBuffer = true;
                Logger.Warn("[AI] no NetworkMessageBuffer - AI orders are being dropped");
            }

            return;
        }

        buffer.AddLocalOrder(order);
        SubmittedCount++;
    }
}
