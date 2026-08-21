// Channel 2 of the walk: the logic RNG state (api-freeze-v1 F5 - the 24-byte state is folded
// into every checkpoint, exactly where the original hashes its seed state via the byte-rolling
// helper at 0x7ed74f). The state is xfered word-order-fixed as six uints plus the base seed,
// which makes the same walk serve all four visitors: a save round-trips the stream position,
// and the CRC folds the state every checkpoint.

using OpenSage.SimCore.Rng;

namespace OpenSage.SimCore.Sync;

public sealed class LogicRandomChannelSource : ICrcChannelSource
{
    private static readonly string[] WordNames =
    {
        "State0", "State1", "State2", "State3", "State4", "State5",
    };

    private readonly LogicRandom _random;

    public LogicRandomChannelSource(LogicRandom random)
    {
        System.ArgumentNullException.ThrowIfNull(random);
        _random = random;
    }

    public CrcChannel Channel => CrcChannel.LogicRandom;

    public bool IsActive => true;

    public void Xfer(IXfer xfer)
    {
        var seed = _random.Seed;
        System.Span<uint> words = stackalloc uint[LogicRandom.StateWordCount];
        _random.CopyStateTo(words);

        xfer.XferUInt("Seed", ref seed);
        for (var i = 0; i < LogicRandom.StateWordCount; i++)
        {
            var word = words[i];
            xfer.XferUInt(WordNames[i], ref word);
            words[i] = word;
        }

        if (xfer.Mode == XferMode.Load)
        {
            _random.RestoreState(words, seed);
        }
    }
}
