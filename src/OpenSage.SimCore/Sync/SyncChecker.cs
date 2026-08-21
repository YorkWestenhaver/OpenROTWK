// The channel walk and checkpoint cadence (api-freeze-v1 F8; design-simcore-scaffolding
// §5.2). SimCore owns the walk STRUCTURE - fixed channel order, per-channel exclude switches,
// the interval clamp, the checkpoint message. OpenSage.Game owns walk CONTENT: its channel
// sources iterate objects in ascending ObjectId and modules in ascending ModuleIndex and feed
// one Xfer walk per channel through whichever visitor the checker drives.

using System;
using System.Collections.Generic;
using OpenSage.SimCore.Ticking;

namespace OpenSage.SimCore.Sync
{
    /// <summary>
    /// One channel's serialisable state. Implementations live where the state lives
    /// (OpenSage.Game for real channels; tests fake them directly).
    /// </summary>
    public interface ICrcChannelSource
    {
        CrcChannel Channel { get; }

        /// <summary>Living World is walked only while that subsystem is active
        /// (desync-crc-deep-dive §5.2); every other channel reports true.</summary>
        bool IsActive { get; }

        /// <summary>The channel's one canonical walk, executed by all four visitors.</summary>
        void Xfer(IXfer xfer);
    }

    public sealed class SyncChecker
    {
        /// <summary>The binary's clamp: effective interval = min(configured, 100)
        /// (crc-byteorder §3.1, cmp 0x64/jl at 0x77ed5d). Default 100 = 20 s at 5 Hz.</summary>
        public const uint MaxIntervalInFrames = 100;

        public const uint DefaultIntervalInFrames = 100;

        private readonly ICrcChannelSource?[] _sources = new ICrcChannelSource?[CrcChannels.Count];
        private readonly bool[] _excluded = new bool[CrcChannels.Count];

        public SyncChecker(IReadOnlyList<ICrcChannelSource> sources)
        {
            ArgumentNullException.ThrowIfNull(sources);
            var previous = -1;
            for (var i = 0; i < sources.Count; i++)
            {
                var source = sources[i];
                var ordinal = (int)source.Channel;
                if (ordinal < 0 || ordinal >= CrcChannels.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(sources), "Unknown CrcChannel.");
                }
                if (ordinal <= previous)
                {
                    // Walk order is the frozen F8 sequence; registration must already be in it
                    // (and duplicate channels are equally malformed).
                    throw new ArgumentException(
                        "Channel sources must be registered in the frozen CrcChannel walk order, without duplicates.",
                        nameof(sources));
                }
                _sources[ordinal] = source;
                previous = ordinal;
            }
        }

        /// <summary>
        /// The per-channel exclude switch (the original's -x...CRC analogs): debug tooling and
        /// the F11 migration mechanism. All off by default.
        /// </summary>
        public void SetExcluded(CrcChannel channel, bool excluded)
        {
            _excluded[(int)channel] = excluded;
        }

        public bool IsExcluded(CrcChannel channel) => _excluded[(int)channel];

        /// <summary>Reproduces the binary's clamp: min(configured, 100). Zero is malformed
        /// (the original's gate divides by the interval; a zero would fault).</summary>
        public static uint EffectiveInterval(uint configuredInterval)
        {
            if (configuredInterval == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(configuredInterval));
            }
            return configuredInterval < MaxIntervalInFrames ? configuredInterval : MaxIntervalInFrames;
        }

        /// <summary>The frame gate: frame % min(interval, 100) == 0, computed unsigned on the
        /// raw frame counter exactly like the binary's DIV, per the written behavioral spec.</summary>
        public static bool IsCheckpointFrame(LogicFrame frame, uint configuredInterval)
        {
            return frame.Value % EffectiveInterval(configuredInterval) == 0;
        }

        /// <summary>
        /// Runs the channel walk through plain CRC visitors and assembles the checkpoint
        /// message. Excluded, inactive, and unregistered channels contribute 0 at their
        /// vector position.
        /// </summary>
        public CrcCheckpointMessage ComputeCheckpoint(LogicFrame frame)
        {
            return Compute(frame, deepWriter: null);
        }

        /// <summary>
        /// The deep variant: streams every field record of every walked channel to
        /// <paramref name="deepWriter"/> and folds the identical bytes, so the returned
        /// message always equals <see cref="ComputeCheckpoint"/> for the same state.
        /// </summary>
        public CrcCheckpointMessage ComputeDeepCheckpoint(LogicFrame frame, DeepCrcWriter deepWriter)
        {
            ArgumentNullException.ThrowIfNull(deepWriter);
            return Compute(frame, deepWriter);
        }

        private CrcCheckpointMessage Compute(LogicFrame frame, DeepCrcWriter? deepWriter)
        {
            deepWriter?.BeginFrame(frame.Value);

            var channelCrcs = new uint[CrcChannels.Count];
            var combined = new XferCrc();
            Span<byte> word = stackalloc byte[4];

            for (var i = 0; i < CrcChannels.Count; i++)
            {
                var source = _sources[i];
                if (source is not null && source.IsActive && !_excluded[i])
                {
                    if (deepWriter is null)
                    {
                        var visitor = new XferCrcVisitor();
                        source.Xfer(visitor);
                        channelCrcs[i] = visitor.Value;
                    }
                    else
                    {
                        deepWriter.BeginChannel((CrcChannel)i);
                        var visitor = new XferDeepDump(deepWriter);
                        source.Xfer(visitor);
                        channelCrcs[i] = visitor.Value;
                        deepWriter.EndChannel((CrcChannel)i, visitor.Value);
                    }
                }

                // The combined value folds the channel vector itself, in order, one word per
                // channel - self-consistent by construction, cheap, and independent of which
                // channels were excluded beyond their zero placeholder.
                XferPrimitives.WriteUInt32(word, channelCrcs[i]);
                combined.Fold(word);
            }

            return new CrcCheckpointMessage(
                frame.Value,
                CrcCheckpointMessage.AlgorithmRotlAdd,
                channelCrcs,
                combined.Value);
        }
    }
}
