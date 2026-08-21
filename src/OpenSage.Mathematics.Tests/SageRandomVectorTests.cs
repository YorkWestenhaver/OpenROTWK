using System.Collections.Generic;
using OpenSage.TestVectors;
using Xunit;

namespace OpenSage.Mathematics.Tests;

/// <summary>
/// The other half of the api-freeze-v1 F5/S3 pin: the client/audio stream port must stay the same
/// generator as OpenSage.SimCore's LogicRandom. Both projects read
/// <c>src/TestVectors/SageRandomVectors.txt</c>, so a change to either implementation that is not
/// mirrored in the other turns one of these two suites red.
/// </summary>
public class SageRandomVectorTests
{
    private static readonly SageRandomVectorFile.VectorSet Vectors = SageRandomVectorFile.Load();

    [Fact]
    public void RawStreamMatchesSharedVectors()
    {
        var streams = new Dictionary<uint, SageRandom>();

        foreach (var vector in Vectors.Raw)
        {
            if (!streams.TryGetValue(vector.Seed, out var random))
            {
                random = new SageRandom(vector.Seed);
                streams.Add(vector.Seed, random);
            }

            Assert.Equal(vector.Value, random.NextRawValue());
        }
    }

    [Fact]
    public void NextMatchesSharedVectors()
    {
        foreach (var vector in Vectors.Next)
        {
            var random = new SageRandom(vector.Seed);

            for (var i = 0; i < vector.Values.Length; i++)
            {
                Assert.Equal(vector.Values[i], random.Next(vector.Lo, vector.Hi));
            }
        }
    }

    [Fact]
    public void SeededConstructorAgreesWithInitialize()
    {
        var constructed = new SageRandom(0xDEADBEEF);

        var initialized = new SageRandom();
        initialized.Initialize(0xDEADBEEF);

        Assert.Equal(0xDEADBEEFu, constructed.Seed);
        Assert.Equal(constructed.Seed, initialized.Seed);

        for (var i = 0; i < 64; i++)
        {
            Assert.Equal(constructed.NextRawValue(), initialized.NextRawValue());
        }
    }
}
