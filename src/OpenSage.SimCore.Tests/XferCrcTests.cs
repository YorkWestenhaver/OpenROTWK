// Gate tests for scaffolding step 5, part 1 (api-freeze-v1 §6 build order): the F7 fold
// against hand-computed vectors. Every expected constant below was worked by hand from the
// behavioral spec of crc-byteorder §4 (init 0; per step crc = rotl1(crc) + value, unsigned
// wrap; native-LE words first, then zero-extended trailing bytes; PER-CALL word/remainder
// split; no final mix). The derivations are spelled out in comments so a reviewer can re-walk
// them without running anything.

using OpenSage.SimCore.Sync;
using Xunit;

namespace OpenSage.SimCore.Tests;

public class XferCrcTests
{
    [Fact]
    public void InitValueIsZero()
    {
        var crc = new XferCrc();
        Assert.Equal(0u, crc.Value);
    }

    [Fact]
    public void EmptyBufferFoldsNothing()
    {
        var crc = new XferCrc();
        crc.Fold(System.ReadOnlySpan<byte>.Empty);
        Assert.Equal(0u, crc.Value);
    }

    [Fact]
    public void SingleWordIsNativeLittleEndian()
    {
        // Bytes 01 02 03 04 compose LE as 0x04030201; rotl1(0) = 0, so crc = 0x04030201.
        // A big-endian (htonl) fold would give 0x01020304 - the exact divergence the binary
        // analysis ruled out (the ADD EAX,[ECX] at 0xa2121a with no BSWAP).
        var crc = new XferCrc();
        crc.Fold(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        Assert.Equal(0x04030201u, crc.Value);
    }

    [Fact]
    public void SecondWordRotatesThenAdds()
    {
        // After word 1: crc = 0x04030201.
        // rotl1(0x04030201) = 0x08060402 (top bit 0, so plain doubling).
        // + 0x08070605 (LE of 05 06 07 08) = 0x100D0A07, no inner carries.
        var crc = new XferCrc();
        crc.Fold(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        crc.Fold(new byte[] { 0x05, 0x06, 0x07, 0x08 });
        Assert.Equal(0x100D0A07u, crc.Value);
    }

    [Fact]
    public void TwoByteBufferFoldsAsTwoBytes()
    {
        // The 'shrt' operator passes length 2, so the word loop is skipped (cmp edi,4 / jb):
        //   byte 0x34: crc = rotl1(0) + 0x34 = 0x34
        //   byte 0x12: crc = rotl1(0x34) + 0x12 = 0x68 + 0x12 = 0x7A
        // Folding the same short zero-padded to a word would give 0x00001234 - proving the
        // per-length split is observable, not cosmetic.
        var crc = new XferCrc();
        crc.Fold(new byte[] { 0x34, 0x12 });
        Assert.Equal(0x7Au, crc.Value);

        var padded = new XferCrc();
        padded.Fold(new byte[] { 0x34, 0x12, 0x00, 0x00 });
        Assert.Equal(0x1234u, padded.Value);
        Assert.NotEqual(crc.Value, padded.Value);
    }

    [Fact]
    public void PerCallSplitNeverConcatenates()
    {
        // Two 2-byte calls each take the byte loop:
        //   0x01: 1;  0x02: rotl1(1)+2 = 4;  0x03: rotl1(4)+3 = 0xB;  0x04: rotl1(0xB)+4 = 0x1A.
        // One 4-byte call folds the word 0x04030201. An implementation that concatenated the
        // two calls into one stream and folded 4-at-a-time would produce the word answer and
        // desync against one that did not - crc-byteorder §4's warning, pinned here.
        var split = new XferCrc();
        split.Fold(new byte[] { 0x01, 0x02 });
        split.Fold(new byte[] { 0x03, 0x04 });
        Assert.Equal(0x1Au, split.Value);

        var joined = new XferCrc();
        joined.Fold(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        Assert.Equal(0x04030201u, joined.Value);
        Assert.NotEqual(split.Value, joined.Value);
    }

    [Fact]
    public void SixByteBufferFoldsWordThenTwoBytes()
    {
        // w = 0xDDCCBBAA -> crc = 0xDDCCBBAA.
        // byte 0xEE: rotl1(0xDDCCBBAA) = 0xBB997755 (carry wraps to bit 0); + 0xEE = 0xBB997843.
        // byte 0xFF: rotl1(0xBB997843) = 0x7732F087; + 0xFF = 0x7732F186.
        var crc = new XferCrc();
        crc.Fold(new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF });
        Assert.Equal(0x7732F186u, crc.Value);
    }

    [Fact]
    public void HighBitRotatesIntoBitZero()
    {
        // Word 00 00 00 80 = 0x80000000 -> crc = 0x80000000.
        // Folding one zero byte: rotl1(0x80000000) = 0x00000001 (the logical SHR 0x1f hibit,
        // not an arithmetic smear); + 0 = 1.
        var crc = new XferCrc();
        crc.Fold(new byte[] { 0x00, 0x00, 0x00, 0x80 });
        Assert.Equal(0x80000000u, crc.Value);
        crc.Fold(new byte[] { 0x00 });
        Assert.Equal(0x00000001u, crc.Value);
    }

    [Fact]
    public void AdditionWrapsUnsigned32()
    {
        // Word 0xFFFFFFFF -> crc = 0xFFFFFFFF. Second word 0xFFFFFFFF:
        // rotl1(0xFFFFFFFF) = 0xFFFFFFFF; + 0xFFFFFFFF = 0x1FFFFFFFE mod 2^32 = 0xFFFFFFFE.
        var crc = new XferCrc();
        crc.Fold(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
        Assert.Equal(0xFFFFFFFEu, crc.Value);
    }

    [Fact]
    public void UnalignedSliceFoldsIdentically()
    {
        // The original's dword load tolerates any alignment; ours composes bytes explicitly,
        // so a buffer must fold the same regardless of where it sits in memory.
        var backing = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04, 0x05 };
        var aligned = new XferCrc();
        aligned.Fold(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 });
        var sliced = new XferCrc();
        sliced.Fold(new System.ReadOnlySpan<byte>(backing, 1, 5));
        Assert.Equal(aligned.Value, sliced.Value);
    }

    [Fact]
    public void BytewiseFoldSkipsTheWordLoop()
    {
        // The seed-fold helper shape (crc-byteorder §2.1): strictly one byte at a time, so
        // [01 02 03 04] gives the byte-loop chain 1, 4, 0xB, 0x1A - never the word 0x04030201.
        var crc = new XferCrc();
        crc.FoldBytewise(new byte[] { 0x01, 0x02, 0x03, 0x04 });
        Assert.Equal(0x1Au, crc.Value);
    }
}
