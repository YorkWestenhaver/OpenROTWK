#nullable enable

using System.Collections.Concurrent;

namespace OpenSage.Diagnostics;

/// <summary>
/// A process-wide "report this content gap exactly once" gate.
///
/// R15 L1-11 (sweep ratchet) introduces a consistent guard shape for the residual crash
/// classes found by the 20-map AotR sweep: when the engine meets content it cannot model
/// (a dangling asset reference, a veterancy rank above the four the engine knows about,
/// a degenerate road node), it must DEGRADE — skip the work, keep simulating — and emit
/// one diagnostic naming the offending content, rather than throwing out of a module
/// constructor or an update tick and ending the match.
///
/// The once-per-key gate matters because every one of these sites sits inside either the
/// per-object load loop or the per-frame update loop: an ungated warning would produce
/// thousands of identical lines per run and drown the log the harness grades from.
///
/// Shape follows the existing <see cref="OpenSage.Logic.Object.LocomotorPhysicsRequirement"/>
/// precedent (R15 FIX-LOCO), generalised so several call sites can share one registry.
/// </summary>
internal static class DegradeLog
{
    /// <summary>
    /// Keys already reported. Used as a set; the value is ignored.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> Reported = new();

    /// <summary>
    /// Returns true exactly once per distinct <paramref name="key"/>, so a caller inside a
    /// hot loop can log a single line per offending piece of content.
    /// </summary>
    /// <param name="category">
    /// Guard-site identifier, e.g. "ObjectCreationList.CreateObject". Namespaces the key so
    /// two unrelated sites reporting the same object template both get to speak once.
    /// </param>
    /// <param name="subject">
    /// The offending content, e.g. an object-template or asset name.
    /// </param>
    internal static bool ShouldReport(string category, string? subject)
    {
        return Reported.TryAdd($"{category}|{Normalize(subject)}", 0);
    }

    /// <summary>
    /// Renders a null/empty name as a fixed placeholder so the log line is still readable
    /// and the once-gate still coalesces.
    /// </summary>
    internal static string Normalize(string? name)
    {
        return string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name!;
    }

    /// <summary>
    /// Clears the reported set. Test-only: keeps the once-per-key gate independent
    /// between test cases.
    /// </summary>
    internal static void ResetForTests()
    {
        Reported.Clear();
    }
}
