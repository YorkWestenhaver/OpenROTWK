using OpenSage.SimCore.Numerics;
using Xunit;

namespace OpenSage.SimCore.Tests;

/// <summary>
/// The SimCore value types must hash the same way in every process. <c>System.HashCode</c>
/// does not: it mixes in a seed generated once per process, so two runs of the same binary
/// disagree. The expected values below were computed independently (FNV-1a over the eight
/// little-endian bytes of each raw word, then a xor-fold to 32 bits), so a change in the fold
/// shows up here rather than as a silent divergence between peers.
/// </summary>
public class DeterministicHashTests
{
    [Fact]
    public void CombineMatchesIndependentlyComputedValues()
    {
        Assert.Equal(350189669, DeterministicHash.Combine(1, 2));
        Assert.Equal(-2077826009, DeterministicHash.Combine(1, 2, 3));
        Assert.Equal(-388006948, DeterministicHash.Combine(0, 0));
    }

    [Fact]
    public void HashIsOrderSensitive()
    {
        Assert.NotEqual(DeterministicHash.Combine(1, 2), DeterministicHash.Combine(2, 1));
    }

    [Fact]
    public void EqualVectorsHashEqually()
    {
        var a = new FixVector2(Fix64.FromRaw(123), Fix64.FromRaw(-456));
        var b = new FixVector2(Fix64.FromRaw(123), Fix64.FromRaw(-456));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }

    /// <summary>
    /// The whole point of the fold: the hash is a pure function of the raw Q31.32 words, so it
    /// is pinnable in a test and identical on every machine and every run.
    /// </summary>
    [Fact]
    public void VectorHashesAreDerivedOnlyFromRawWords()
    {
        var vector2 = new FixVector2(Fix64.FromRaw(1), Fix64.FromRaw(2));
        Assert.Equal(DeterministicHash.Combine(1, 2), vector2.GetHashCode());

        var vector3 = new FixVector3(Fix64.FromRaw(1), Fix64.FromRaw(2), Fix64.FromRaw(3));
        Assert.Equal(DeterministicHash.Combine(1, 2, 3), vector3.GetHashCode());
    }
}
