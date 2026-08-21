// The frozen lockstep checksum fold (api-freeze-v1 F7; design-simcore-scaffolding §5.1).
//
// Clean-room: this is the behavioral spec of crc-byteorder.md §4 - no engine code was copied.
// The load-bearing facts, each pinned by a test in XferCrcTests:
//
//   * init value 0; per step: hibit = crc >> 31 (logical, 0/1); crc = crc*2 + hibit + value,
//     unsigned 32-bit wrap - i.e. rotate-left-1 then add.
//   * words are read from the buffer as NATIVE LITTLE-ENDIAN dwords; there is no htonl, no
//     byteswap. Byte composition below is explicit, so the fold is byte-identical on
//     strict-alignment targets too.
//   * the word/remainder split is PER CALL, never global: each xfer call's buffer folds
//     independently, words first, then 1..3 trailing zero-extended bytes. A 2-byte field
//     ('shrt') folds as two bytes because its own buffer never reaches the word loop.
//     Concatenating fields into one stream and folding 4-at-a-time will NOT match.
//   * no final mix - the checksum is the accumulator after the ordered walk.
//   * type tags / field names are never folded; only raw value bytes reach the accumulator.
//   * null buffer + non-zero length folds nothing. Spans cannot express that state; callers
//     with raw pointers must apply the guard before constructing the span.

namespace OpenSage.SimCore.Sync
{
    /// <summary>
    /// The rotate-left-1-and-add rolling checksum used for the per-channel lockstep CRC.
    /// Weak diffusion is a known, accepted property (crc-byteorder §4.2); the checkpoint
    /// message carries an algorithm id so a stronger self-consistent hash can be swapped in
    /// without a wire redesign.
    /// </summary>
    public struct XferCrc
    {
        private uint _crc; // init 0 - the ctor of the original zeroes the accumulator.

        /// <summary>The accumulator after the walk so far. No finalization step exists.</summary>
        public readonly uint Value => _crc;

        /// <summary>
        /// Folds one xfer call's buffer: 4-byte native-little-endian words first, then 1..3
        /// trailing zero-extended bytes. PER-CALL granularity - never concatenate fields.
        /// </summary>
        public void Fold(System.ReadOnlySpan<byte> b)
        {
            var i = 0;
            for (; b.Length - i >= 4; i += 4)
            {
                var w = (uint)(b[i] | b[i + 1] << 8 | b[i + 2] << 16 | b[i + 3] << 24);
                _crc = ((_crc << 1) | (_crc >> 31)) + w;
            }
            for (; i < b.Length; i++)
            {
                _crc = ((_crc << 1) | (_crc >> 31)) + b[i];
            }
        }

        /// <summary>
        /// Folds a buffer one byte at a time, skipping the word loop entirely - the shape of
        /// the original's seed-fold helper (crc-byteorder §2.1: byte-wise by
        /// construction). Provided for channel walks that mirror that helper; byte order is
        /// irrelevant to this form by construction.
        /// </summary>
        public void FoldBytewise(System.ReadOnlySpan<byte> b)
        {
            for (var i = 0; i < b.Length; i++)
            {
                _crc = ((_crc << 1) | (_crc >> 31)) + b[i];
            }
        }
    }
}
