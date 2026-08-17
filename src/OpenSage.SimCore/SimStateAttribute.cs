using System;

namespace OpenSage.SimCore
{
    /// <summary>
    /// Marks a type as simulation state. Inside <c>OpenSage.SimCore</c> this is documentation;
    /// its load-bearing use is the analyzer's scoped attachment mode
    /// (design-simcore-scaffolding §2.3): a file in <c>OpenSage.Game</c> that declares a
    /// <c>[SimState]</c> type gets the full SIMCORE001-010 rule set even before its directory
    /// enters <c>SimCoreScopedDirs.txt</c>.
    /// </summary>
    /// <remarks>
    /// The analyzer matches this attribute <i>by name</i>, so a migrating project does not have to
    /// reference SimCore just to opt a file in.
    /// </remarks>
    [AttributeUsage(
        AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface,
        AllowMultiple = false,
        Inherited = false)]
    public sealed class SimStateAttribute : Attribute
    {
    }
}
