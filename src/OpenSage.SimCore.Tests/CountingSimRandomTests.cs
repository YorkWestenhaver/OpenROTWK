using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Rng;
using Xunit;

namespace OpenSage.SimCore.Tests;

/// <summary>
/// Build-order step 3 gate (api-freeze-v1 §6, seam S3): the module-facing <see cref="ISimRandom"/>
/// wrapper counts draws (conformance channel 5) without perturbing the stream by so much as one
/// bit.
/// </summary>
public class CountingSimRandomTests
{
    [Fact]
    public void WrappingDoesNotPerturbTheStream()
    {
        var bare = LogicRandom.CreateForSimContext(0xDEADBEEF);
        ISimRandom wrapped = new CountingSimRandom(LogicRandom.CreateForSimContext(0xDEADBEEF));

        for (var i = 0; i < 512; i++)
        {
            switch (i % 3)
            {
                case 0:
                    Assert.Equal(bare.NextUInt32(), wrapped.NextUInt32());
                    break;
                case 1:
                    Assert.Equal(bare.Next(-10, 5), wrapped.Next(-10, 5));
                    break;
                default:
                    Assert.Equal(
                        bare.NextFix64(Fix64.Zero, Fix64.Two).RawValue,
                        wrapped.NextFix64(Fix64.Zero, Fix64.Two).RawValue);
                    break;
            }
        }

        Assert.Equal(512ul, wrapped.DrawCount);
    }

    [Fact]
    public void DrawCountEqualsRawDrawsTaken()
    {
        var wrapped = new CountingSimRandom(LogicRandom.CreateForSimContext(3));

        Assert.Equal(0ul, wrapped.DrawCount);

        wrapped.NextUInt32();
        Assert.Equal(1ul, wrapped.DrawCount);

        wrapped.Next(0, 5);
        Assert.Equal(2ul, wrapped.DrawCount);

        wrapped.NextFix64(Fix64.Zero, Fix64.One);
        Assert.Equal(3ul, wrapped.DrawCount);
    }

    [Fact]
    public void DegenerateRangesTakeNoDrawAndAreNotCounted()
    {
        var wrapped = new CountingSimRandom(LogicRandom.CreateForSimContext(3));
        var reference = LogicRandom.CreateForSimContext(3);

        wrapped.Next(int.MinValue, int.MaxValue);
        wrapped.NextFix64(Fix64.One, Fix64.One);
        wrapped.NextFix64(Fix64.One, Fix64.Zero);

        Assert.Equal(0ul, wrapped.DrawCount);
        Assert.Equal(reference.NextUInt32(), wrapped.NextUInt32());
        Assert.Equal(1ul, wrapped.DrawCount);
    }

    [Fact]
    public void ResetDrawCountRestoresACheckpointedCount()
    {
        var wrapped = new CountingSimRandom(LogicRandom.CreateForSimContext(0));

        for (var i = 0; i < 10; i++)
        {
            wrapped.NextUInt32();
        }

        Assert.Equal(10ul, wrapped.DrawCount);

        wrapped.ResetDrawCount(1234);
        Assert.Equal(1234ul, wrapped.DrawCount);

        wrapped.ResetDrawCount();
        Assert.Equal(0ul, wrapped.DrawCount);
    }
}
