using System;
using System.Runtime.CompilerServices;
using OpenSage.SimCore.Numerics;

namespace OpenSage.SimCore.Rng
{
    /// <summary>
    /// The SAGE add-with-carry generator, owned by the simulation (api-freeze-v1 F5,
    /// design-simcore-scaffolding §3). Bit-identical to <c>OpenSage.Mathematics.SageRandom</c> -
    /// the two implementations are pinned to one another by the shared vector file
    /// <c>src/TestVectors/SageRandomVectors.txt</c>, which both test projects read.
    /// <para>
    /// Pure integer arithmetic, so the stream is identical on every architecture. The float
    /// surface of the Mathematics port (<c>NextSingle</c>) is deliberately absent; its
    /// replacement is <see cref="NextFix64"/>, which scales by exact integer arithmetic.
    /// </para>
    /// <para>
    /// Reach (freeze S3): the constructor is internal to SimCore, and
    /// <see cref="CreateForSimContext"/> is the single blessed instantiation site - the sim
    /// context in OpenSage.Game. Modules never see this type; they see
    /// <see cref="ISimRandom"/> hanging off the sim context. Client and audio streams stay
    /// <c>SageRandom</c> in OpenSage.Mathematics and are never CRC'd.
    /// </para>
    /// </summary>
    public sealed class LogicRandom
    {
        /// <summary>
        /// Number of 32-bit words of generator state. 6 words = the 24 bytes the original folds
        /// into its checksum (crc-byteorder §2.1); the layout is SAGE's, word 0 first.
        /// </summary>
        public const int StateWordCount = 6;

        /// <summary>Size in bytes of the state that participates in the LogicRandom CRC channel.</summary>
        public const int StateByteCount = StateWordCount * sizeof(uint);

        private readonly uint[] _state = new uint[StateWordCount];
        private uint _baseSeed;

        internal LogicRandom(uint seed)
        {
            Initialize(seed);
        }

        /// <summary>
        /// The single blessed instantiation site (freeze S3). Called by OpenSage.Game's sim
        /// context and by SimCore's own tests; nothing else has a reason to make one, and the
        /// analyzer's SIMCORE003 rule bans every other source of randomness in sim code.
        /// </summary>
        public static LogicRandom CreateForSimContext(uint seed) => new LogicRandom(seed);

        /// <summary>The seed this stream was last initialized from.</summary>
        public uint Seed => _baseSeed;

        /// <summary>
        /// Reseeds the stream. Sim code calls this exactly once per match, from the game-start
        /// message's match seed (design-simcore-scaffolding §3.3).
        /// </summary>
        public void Initialize(uint seed)
        {
            _baseSeed = seed;

            // The original's constant ladder, expressed as running deltas so the six magic words
            // stay visible in the source.
            var ax = seed;
            ax += 0xf22d0e56;
            _state[0] = ax;
            ax += unchecked(0x883126e9 - 0xf22d0e56);
            _state[1] = ax;
            ax += 0xc624dd2f - 0x883126e9;
            _state[2] = ax;
            ax += unchecked(0x0702c49c - 0xc624dd2f);
            _state[3] = ax;
            ax += 0x9e353f7d - 0x0702c49c;
            _state[4] = ax;
            ax += unchecked(0x6fdf3b64 - 0x9e353f7d);
            _state[5] = ax;
        }

        /// <summary>
        /// The raw 32-bit draw. Every other draw method is defined in terms of this one, so the
        /// draw count and the raw stream stay in lockstep.
        /// </summary>
        public uint NextUInt32()
        {
            uint ax;
            uint c = 0;

            Adc(out ax, _state[5], _state[4], ref c);
            _state[4] = ax;

            Adc(out ax, ax, _state[3], ref c);
            _state[3] = ax;

            Adc(out ax, ax, _state[2], ref c);
            _state[2] = ax;

            Adc(out ax, ax, _state[1], ref c);
            _state[1] = ax;

            Adc(out ax, ax, _state[0], ref c);
            _state[0] = ax;

            // Increment the state array, bubbling up the carries.
            if (++_state[5] == 0
                && ++_state[4] == 0
                && ++_state[3] == 0
                && ++_state[2] == 0
                && ++_state[1] == 0)
            {
                ++_state[0];
                ++ax;
            }

            return ax;
        }

        /// <summary>
        /// SAGE integer draw over an inclusive range, semantics identical to the Mathematics port.
        /// </summary>
        /// <param name="lo">Inclusive lower bound.</param>
        /// <param name="hi">Inclusive upper bound; must be >= <paramref name="lo"/>.</param>
        public int Next(int lo, int hi)
        {
            var delta = (uint)(hi - lo + 1);

            if (delta == 0)
            {
                return hi;
            }

            return (int)((NextUInt32() % delta) + lo);
        }

        /// <summary>
        /// The Fix64 draw - the replacement for the original's
        /// <c>GetGameLogicRandomValueReal</c> and for <c>SageRandom.NextSingle</c>
        /// (design-simcore-scaffolding §3.1). Never touches a float: the 32 draw bits are read
        /// directly as 32 fraction bits, so <c>frac</c> is an exact dyadic rational in [0, 1),
        /// and the scaling is a 96-bit integer product truncated back to Q31.32.
        /// </summary>
        /// <returns>
        /// A value in <c>[lo, hi)</c>, or <paramref name="hi"/> when the range is empty or
        /// inverted - and in that degenerate case no draw is consumed, exactly as the
        /// Mathematics port's <c>NextSingle</c> guard behaves. Keeping the two ports
        /// indistinguishable is deliberate; see the step-3 findings in scaffolding-log.md.
        /// </returns>
        public Fix64 NextFix64(Fix64 lo, Fix64 hi)
        {
            if (hi <= lo)
            {
                return hi;
            }

            var frac = NextUInt32();

            // Unsigned difference: exact for every ordered pair, including ones whose signed
            // difference would not fit in a long, and it can never be negative here.
            var magnitude = unchecked((ulong)hi.RawValue - (ulong)lo.RawValue);

            return Fix64.FromRaw(unchecked(lo.RawValue + (long)MulFraction(magnitude, frac)));
        }

        /// <summary>
        /// Copies the generator state, word 0 first, for the LogicRandom CRC channel (F8) and for
        /// save/load. This is the whole of the mutable state that has to survive a checkpoint.
        /// </summary>
        /// <remarks>
        /// The typed <c>Xfer(IXfer)</c> the design doc sketches lands with the checksum framework
        /// (build-order step 5); until <c>IXfer</c> exists, step 5 folds the state through these
        /// two accessors. See the step-3 findings in scaffolding-log.md.
        /// </remarks>
        public void CopyStateTo(Span<uint> destination)
        {
            if (destination.Length < StateWordCount)
            {
                throw new ArgumentException(
                    "destination must hold at least " + StateWordCount + " words",
                    nameof(destination));
            }

            for (var i = 0; i < StateWordCount; i++)
            {
                destination[i] = _state[i];
            }
        }

        /// <summary>Restores state previously captured by <see cref="CopyStateTo"/>.</summary>
        public void RestoreState(ReadOnlySpan<uint> source, uint baseSeed)
        {
            if (source.Length < StateWordCount)
            {
                throw new ArgumentException(
                    "source must hold at least " + StateWordCount + " words",
                    nameof(source));
            }

            for (var i = 0; i < StateWordCount; i++)
            {
                _state[i] = source[i];
            }

            _baseSeed = baseSeed;
        }

        /// <summary>
        /// <c>(magnitude * frac) >> 32</c> evaluated exactly over 96 bits.
        /// <paramref name="frac"/> is a Q0.32 fraction in [0, 1).
        /// </summary>
        private static ulong MulFraction(ulong magnitude, uint frac)
        {
            var low = (magnitude & 0xFFFFFFFFUL) * frac;
            var high = (magnitude >> 32) * frac;

            // (high * 2^32 + low) >> 32 is exact: high * 2^32 has 32 zero low bits.
            return unchecked(high + (low >> 32));
        }

        /// <summary>
        /// Add with carry. <paramref name="sum"/> is replaced with <paramref name="a"/> +
        /// <paramref name="b"/> + <paramref name="c"/>. <paramref name="c"/> is replaced with
        /// 1 if there was a carry, 0 if there wasn't. A carry occurred if the sum is less
        /// than one of the inputs. This is addition, so carry can never be more than 1.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Adc(out uint sum, uint a, uint b, ref uint c)
        {
            sum = a + b + c;
            c = (sum < a || sum < b)
                ? 1u
                : 0u;
        }
    }
}
