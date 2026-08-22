using System.Collections.Concurrent;

namespace OpenSage.Logic.Object;

/// <summary>
/// Locomotors require a <see cref="PhysicsBehavior"/> on the object they are applied to.
/// Some object templates declare a locomotor set but never declare a physics module (either
/// because the template genuinely has none, or because the module block failed to parse and
/// was skipped). Retail guards every such site with a debug-only assert followed by an early
/// return, so in a shipping build the locomotor call is simply a no-op and the simulation
/// continues; see the four <c>getPhysics() == NULL</c> guards in the EA GPL reference
/// (GeneralsMD Locomotor.cpp locoUpdate_moveTowardsAngle / setPhysicsOptions /
/// locoUpdate_moveTowardsPosition / locoUpdate_maintainCurrentPosition).
///
/// This class implements the shipping-build behaviour: degrade rather than kill the sim tick,
/// and emit exactly one diagnostic per offending object template so the content gap is visible
/// in the log without spamming it once per object per frame.
/// </summary>
internal static class LocomotorPhysicsRequirement
{
    private const string UnknownName = "<unknown>";

    /// <summary>
    /// Object-template names we have already reported. Used as a set; the value is ignored.
    /// </summary>
    private static readonly ConcurrentDictionary<string, byte> ReportedTemplates = new();

    /// <summary>
    /// Returns true exactly once per distinct object-template name, so callers can log a
    /// single line per offending template instead of one per object per frame.
    /// </summary>
    internal static bool ShouldReport(string objectTemplateName)
    {
        return ReportedTemplates.TryAdd(Normalize(objectTemplateName), 0);
    }

    /// <summary>
    /// The single log line emitted for a locomotor applied to an object without physics.
    /// Names the object template, the locomotor template, and the call site so the content
    /// gap can be traced back to a specific INI block.
    /// </summary>
    internal static string FormatMessage(
        string objectTemplateName,
        string locomotorTemplateName,
        string callSite)
    {
        return $"Locomotor '{Normalize(locomotorTemplateName)}' applied to object template " +
               $"'{Normalize(objectTemplateName)}' which has no PhysicsBehavior module; " +
               $"skipping locomotor update at {Normalize(callSite)}. " +
               "This object will not move. Its template is most likely missing a " +
               "Behavior = PhysicsBehavior block (or that block failed to parse).";
    }

    /// <summary>
    /// Clears the reported-template set. Test-only: keeps the once-per-template gate
    /// independent between test cases.
    /// </summary>
    internal static void ResetForTests()
    {
        ReportedTemplates.Clear();
    }

    private static string Normalize(string name)
    {
        return string.IsNullOrEmpty(name) ? UnknownName : name;
    }
}
