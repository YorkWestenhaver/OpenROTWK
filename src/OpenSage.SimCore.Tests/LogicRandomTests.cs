using System;
using System.Collections.Generic;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Rng;
using OpenSage.TestVectors;
using Xunit;

namespace OpenSage.SimCore.Tests;

/// <summary>
/// Build-order step 3 gate (api-freeze-v1 §6): LogicRandom is the SAGE generator, bit-identical to
/// the OpenSage.Mathematics port, and its Fix64 draw never touches a float.
/// The same vector file drives <c>OpenSage.Mathematics.Tests.SageRandomVectorTests</c>.
/// </summary>
public class LogicRandomTests
{
    private static readonly SageRandomVectorFile.VectorSet Vectors = SageRandomVectorFile.Load();

    [Fact]
    public void RawStreamMatchesSharedVectors()
    {
        var streams = new Dictionary<uint, LogicRandom>();
        var expectedIndex = new Dictionary<uint, int>();

        foreach (var vector in Vectors.Raw)
        {
            if (!streams.TryGetValue(vector.Seed, out var random))
            {
                random = LogicRandom.CreateForSimContext(vector.Seed);
                streams.Add(vector.Seed, random);
                expectedIndex.Add(vector.Seed, 0);
            }

            // The file lists draws in order; anything else would mean the reader lost its place.
            Assert.Equal(expectedIndex[vector.Seed], vector.Index);
            expectedIndex[vector.Seed] = vector.Index + 1;

            Assert.Equal(vector.Value, random.NextUInt32());
        }
    }

    [Fact]
    public void NextMatchesSharedVectors()
    {
        foreach (var vector in Vectors.Next)
        {
            var random = LogicRandom.CreateForSimContext(vector.Seed);

            for (var i = 0; i < vector.Values.Length; i++)
            {
                Assert.Equal(vector.Values[i], random.Next(vector.Lo, vector.Hi));
            }
        }
    }

    [Fact]
    public void NextFix64MatchesSharedVectors()
    {
        foreach (var vector in Vectors.Fix)
        {
            var random = LogicRandom.CreateForSimContext(vector.Seed);
            var lo = Fix64.FromRaw(vector.LoRaw);
            var hi = Fix64.FromRaw(vector.HiRaw);

            for (var i = 0; i < vector.Values.Length; i++)
            {
                Assert.Equal(vector.Values[i], random.NextFix64(lo, hi).RawValue);
            }
        }
    }

    [Fact]
    public void NextFix64OverUnitRangeIsTheRawDrawAsAFraction()
    {
        // [0,1) with a Q31.32 layout means the 32 draw bits land exactly on the 32 fraction bits:
        // the strongest available statement that no rounding or float step sneaks in.
        var reference = LogicRandom.CreateForSimContext(0x0BADF00D);
        var random = LogicRandom.CreateForSimContext(0x0BADF00D);

        for (var i = 0; i < 256; i++)
        {
            var expected = reference.NextUInt32();
            Assert.Equal((long)expected, random.NextFix64(Fix64.Zero, Fix64.One).RawValue);
        }
    }

    [Fact]
    public void NextFix64StaysInsideTheHalfOpenRange()
    {
        var lo = Fix64.FromRaw(-(5L << 32) / 2);
        var hi = Fix64.FromRaw((29L << 32) / 4);
        var random = LogicRandom.CreateForSimContext(1);

        for (var i = 0; i < 4096; i++)
        {
            var value = random.NextFix64(lo, hi);
            Assert.True(value >= lo, $"draw {i} below lo");
            Assert.True(value < hi, $"draw {i} at or above hi");
        }
    }

    [Fact]
    public void DegenerateRangesReturnHiAndTakeNoDraw()
    {
        // Matches SageRandom.NextSingle's guard exactly, so the two ports stay indistinguishable.
        var random = LogicRandom.CreateForSimContext(7);
        var reference = LogicRandom.CreateForSimContext(7);

        Assert.Equal(Fix64.One, random.NextFix64(Fix64.One, Fix64.One));
        Assert.Equal(Fix64.Zero, random.NextFix64(Fix64.One, Fix64.Zero));
        Assert.Equal(int.MaxValue, random.Next(int.MinValue, int.MaxValue));

        Assert.Equal(reference.NextUInt32(), random.NextUInt32());
    }

    [Fact]
    public void ReinitializingRestartsTheStream()
    {
        var random = LogicRandom.CreateForSimContext(0);
        var first = new uint[8];
        for (var i = 0; i < first.Length; i++)
        {
            first[i] = random.NextUInt32();
        }

        random.Initialize(0);
        Assert.Equal(0u, random.Seed);

        for (var i = 0; i < first.Length; i++)
        {
            Assert.Equal(first[i], random.NextUInt32());
        }
    }

    [Fact]
    public void StateSnapshotRoundTripsAndIsTwentyFourBytes()
    {
        // The LogicRandom CRC channel (F8) folds exactly this state; step 5 wires it to IXfer.
        Assert.Equal(24, LogicRandom.StateByteCount);

        var random = LogicRandom.CreateForSimContext(0xDEADBEEF);
        for (var i = 0; i < 37; i++)
        {
            random.NextUInt32();
        }

        var state = new uint[LogicRandom.StateWordCount];
        random.CopyStateTo(state);

        var expected = new uint[16];
        for (var i = 0; i < expected.Length; i++)
        {
            expected[i] = random.NextUInt32();
        }

        var restored = LogicRandom.CreateForSimContext(0);
        restored.RestoreState(state, 0xDEADBEEF);

        Assert.Equal(0xDEADBEEFu, restored.Seed);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i], restored.NextUInt32());
        }
    }

    [Fact]
    public void CopyStateRejectsUndersizedBuffers()
    {
        var random = LogicRandom.CreateForSimContext(0);
        Assert.Throws<ArgumentException>(() => random.CopyStateTo(new uint[LogicRandom.StateWordCount - 1]));
    }
}
