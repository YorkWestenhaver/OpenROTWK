using Microsoft.CodeAnalysis;

namespace OpenSage.SimCore.Analyzers
{
    /// <summary>
    /// The frozen SIMCORE diagnostic set (api-freeze-v1 F10, design-simcore-scaffolding §2.2).
    /// IDs are contractual: they appear in suppressions and in review diffs, so they are never
    /// renumbered. Severities are the frozen ones - 001..007 error, 010 warning.
    /// </summary>
    internal static class Descriptors
    {
        private const string Quarantine = "SimCore.Quarantine";
        private const string Determinism = "SimCore.Determinism";

        private const string HelpUri =
            "https://github.com/YorkWestenhaver/OpenSAGE/blob/simcore-scaffolding/docs/simcore-analyzer.md";

        private static DiagnosticDescriptor Error(string id, string category, string title, string format) =>
            new DiagnosticDescriptor(id, title, format, category, DiagnosticSeverity.Error, true, null, HelpUri);

        private static DiagnosticDescriptor Warning(string id, string category, string title, string format) =>
            new DiagnosticDescriptor(id, title, format, category, DiagnosticSeverity.Warning, true, null, HelpUri);

        /// <summary>float/double/System.Half in any syntax position.</summary>
        public static readonly DiagnosticDescriptor FloatingPointType = Error(
            "SIMCORE001", Quarantine,
            "Floating-point types are banned inside the simulation quarantine",
            "'{0}' is a floating-point type and cannot appear in simulation code; use Fix64 (exempt a file only via the SIMCORE-EXEMPT header plus a SimCoreExemptions.txt entry)");

        /// <summary>Banned API, driven by BannedSymbols.txt plus the built-in seed list.</summary>
        public static readonly DiagnosticDescriptor BannedMathApi = Error(
            "SIMCORE002", Quarantine,
            "System.Math/System.MathF/System.Numerics are banned inside the simulation quarantine",
            "'{0}' is banned in simulation code: {1}");

        public static readonly DiagnosticDescriptor NondeterministicSource = Error(
            "SIMCORE003", Determinism,
            "Nondeterministic ambient source",
            "'{0}' is a nondeterministic source: {1}");

        public static readonly DiagnosticDescriptor UnorderedIteration = Error(
            "SIMCORE004", Determinism,
            "Iteration order is not deterministic",
            "{0} has an implementation-defined iteration order; use SortedList/SortedDictionary, a dense-ID array, or an explicit OrderBy on a total key");

        public static readonly DiagnosticDescriptor UnstableHash = Error(
            "SIMCORE005", Determinism,
            "Hash or enum ordering is not stable across processes",
            "{0} is not stable across processes: {1}");

        public static readonly DiagnosticDescriptor MutableStaticState = Error(
            "SIMCORE006", Determinism,
            "Mutable static state in simulation code",
            "static field '{0}' is mutable; simulation state must live in the game-state graph (make it const or readonly)");

        public static readonly DiagnosticDescriptor Concurrency = Error(
            "SIMCORE007", Determinism,
            "Asynchrony or threading in simulation code",
            "{0} is not allowed in simulation code: {1}");

        /// <summary>The §1.2-R2 overflow guard: squared-length products belong in FixMath.</summary>
        public static readonly DiagnosticDescriptor SquaredMultiply = Warning(
            "SIMCORE010", Determinism,
            "Squared-magnitude multiply outside FixMath",
            "multiplying '{0}' by '{1}' looks like a squared-magnitude product; Fix64 saturates, so route this through FixMath's 128-bit wide-compare helpers");
    }
}
