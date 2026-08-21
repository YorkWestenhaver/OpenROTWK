using Xunit;

namespace OpenSage.Tests;

/// <summary>
/// Serializes every test class that either opens a <see cref="OpenSage.Diagnostics.GameTrace"/>
/// session or drives a sim loop that can emit into one.
/// </summary>
/// <remarks>
/// GameTrace is process-wide static state with a single output writer. xUnit runs distinct test
/// classes in parallel, so a class that advances a sim loop will emit its periodic heartbeat
/// instant-events into whatever trace session another class happens to have open at that moment -
/// interleaving writes into that session's file and breaking the JSON it is about to parse back.
/// The classes are correct individually; only their overlap in time is wrong, which is exactly
/// what a shared collection removes.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class GameTraceCollection
{
    public const string Name = "GameTrace";
}
