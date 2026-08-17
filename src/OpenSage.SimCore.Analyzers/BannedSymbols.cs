using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace OpenSage.SimCore.Analyzers
{
    internal readonly struct BannedEntry
    {
        public BannedEntry(DiagnosticDescriptor descriptor, string message)
        {
            Descriptor = descriptor;
            Message = message;
        }

        public DiagnosticDescriptor Descriptor { get; }

        public string Message { get; }
    }

    /// <summary>
    /// The API side of the quarantine (SIMCORE002/003/005/007). design-simcore-scaffolding §2.2
    /// calls for a BannedSymbols.txt in the BannedApiAnalyzers format, kept as "the easily-extended
    /// list ... where the OPEN-10 nondeterminism inventory lands as it grows". We read that format
    /// ourselves rather than taking a dependency on BannedApiAnalyzers, so that every violation is
    /// reported under an auditable SIMCORE id instead of RS0030, and so the whole wall ships in one
    /// analyzer assembly as the doc requires.
    ///
    /// Line format (a superset of the upstream one):
    /// <code>SymbolId ; SIMCOREnnn ; free-text reason</code>
    /// SymbolId is <c>N:Namespace</c>, <c>T:Namespace.Type</c> or
    /// <c>M:Namespace.Type.Member</c> (M also covers properties and fields). The rule id and the
    /// reason are optional; the id defaults to SIMCORE002.
    /// </summary>
    internal sealed class BannedSymbols
    {
        private readonly ImmutableDictionary<string, BannedEntry> _entries;

        private BannedSymbols(ImmutableDictionary<string, BannedEntry> entries) => _entries = entries;

        public int Count => _entries.Count;

        public static BannedSymbols Create(AnalyzerOptions options)
        {
            var builder = ImmutableDictionary.CreateBuilder<string, BannedEntry>(StringComparer.Ordinal);

            foreach (var line in Seed)
            {
                Add(builder, line);
            }

            foreach (var line in SimCoreScope.ReadListFile(options, SimCoreScope.BannedSymbolsFileName))
            {
                Add(builder, line);
            }

            return new BannedSymbols(builder.ToImmutable());
        }

        private static void Add(ImmutableDictionary<string, BannedEntry>.Builder builder, string line)
        {
            var parts = line.Split(';');
            var key = parts[0].Trim();
            if (key.Length < 3 || key[1] != ':')
            {
                return;
            }

            var descriptor = Descriptors.BannedMathApi;
            var message = "banned by the SimCore quarantine";

            if (parts.Length > 1 && parts[1].Trim().Length != 0)
            {
                descriptor = DescriptorFor(parts[1].Trim());
            }

            if (parts.Length > 2 && parts[2].Trim().Length != 0)
            {
                message = parts[2].Trim();
            }

            builder[key] = new BannedEntry(descriptor, message);
        }

        private static DiagnosticDescriptor DescriptorFor(string id) => id switch
        {
            "SIMCORE003" => Descriptors.NondeterministicSource,
            "SIMCORE005" => Descriptors.UnstableHash,
            "SIMCORE007" => Descriptors.Concurrency,
            _ => Descriptors.BannedMathApi
        };

        /// <summary>
        /// Looks the symbol up by, in order: its own member id, its own type id, the ids of its
        /// containing types, and every enclosing namespace. First hit wins, so a namespace ban
        /// ("N:System.Numerics") covers everything below it while a narrower entry can still name
        /// a specific member.
        /// </summary>
        public bool TryMatch(ISymbol symbol, out BannedEntry entry)
        {
            foreach (var key in Keys(symbol))
            {
                if (_entries.TryGetValue(key, out entry))
                {
                    return true;
                }
            }

            entry = default;
            return false;
        }

        private static IEnumerable<string> Keys(ISymbol symbol)
        {
            if (symbol is IMethodSymbol method && method.MethodKind == MethodKind.Constructor)
            {
                symbol = method.ContainingType;
            }

            if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
            {
                symbol = reduced;
            }

            if (symbol is IMethodSymbol { OriginalDefinition: { } originalMethod })
            {
                symbol = originalMethod;
            }

            if (symbol is INamedTypeSymbol named)
            {
                symbol = named.OriginalDefinition;
            }

            if (symbol is IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol)
            {
                var containingType = symbol.ContainingType;
                if (containingType is not null)
                {
                    yield return "M:" + FullName(containingType) + "." + symbol.Name;
                }
            }

            var type = symbol as ITypeSymbol ?? symbol.ContainingType;
            while (type is not null)
            {
                var full = FullName(type);
                if (full.Length != 0)
                {
                    yield return "T:" + full;
                }

                type = type.ContainingType;
            }

            var ns = (symbol as ITypeSymbol ?? (ISymbol?)symbol.ContainingType)?.ContainingNamespace
                ?? symbol.ContainingNamespace;
            while (ns is { IsGlobalNamespace: false })
            {
                yield return "N:" + ns.ToDisplayString();
                ns = ns.ContainingNamespace;
            }
        }

        internal static string FullName(ITypeSymbol type)
        {
            if (type is IArrayTypeSymbol array)
            {
                return FullName(array.ElementType);
            }

            var ns = type.ContainingNamespace;
            var prefix = ns is { IsGlobalNamespace: false } ? ns.ToDisplayString() + "." : string.Empty;

            var outer = type.ContainingType;
            if (outer is not null)
            {
                return FullName(outer) + "." + type.Name;
            }

            return prefix + type.Name;
        }

        /// <summary>
        /// Built into the assembly so the wall stands even if a project forgets to wire up the
        /// AdditionalFile. BannedSymbols.txt is purely additive on top of this.
        /// </summary>
        private static readonly string[] Seed =
        {
            // SIMCORE002 - float maths surfaces. System.Math is banned wholesale (zero-exception
            // rule): the integer helpers have FixMath equivalents.
            "T:System.Math;SIMCORE002;use OpenSage.SimCore.Numerics.FixMath",
            "T:System.MathF;SIMCORE002;use OpenSage.SimCore.Numerics.FixMath",
            "N:System.Numerics;SIMCORE002;use the FixVector/FixMatrix types",
            // System.Single/Double/Half are deliberately absent: SIMCORE001 owns the float types
            // in every syntax position, and listing them here would double-report.

            // SIMCORE003 - ambient nondeterministic sources.
            "T:System.Random;SIMCORE003;use the SimCore LogicRandom via ISimRandom",
            "M:System.Guid.NewGuid;SIMCORE003;derive ids from sim state",
            "M:System.DateTime.Now;SIMCORE003;use the logic frame counter",
            "M:System.DateTime.UtcNow;SIMCORE003;use the logic frame counter",
            "M:System.DateTime.Today;SIMCORE003;use the logic frame counter",
            "M:System.DateTimeOffset.Now;SIMCORE003;use the logic frame counter",
            "M:System.DateTimeOffset.UtcNow;SIMCORE003;use the logic frame counter",
            "M:System.Environment.TickCount;SIMCORE003;use the logic frame counter",
            "M:System.Environment.TickCount64;SIMCORE003;use the logic frame counter",
            "T:System.Diagnostics.Stopwatch;SIMCORE003;wall-clock time cannot drive sim state",

            // SIMCORE005 - hashes that are seeded per process.
            "T:System.HashCode;SIMCORE005;System.HashCode is seeded per process; combine raw values explicitly",
            "M:System.Enum.GetValues;SIMCORE005;enum member order is reflection-defined; use an explicit table",
            "M:System.Enum.GetNames;SIMCORE005;enum member order is reflection-defined; use an explicit table",
            "M:System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode;SIMCORE005;identity hashes vary per run",

            // SIMCORE007 - asynchrony and threading.
            "T:System.Threading.Tasks.Task;SIMCORE007;the tick loop is single-threaded",
            "T:System.Threading.Tasks.ValueTask;SIMCORE007;the tick loop is single-threaded",
            "T:System.Threading.Tasks.Parallel;SIMCORE007;the tick loop is single-threaded",
            "T:System.Threading.Thread;SIMCORE007;the tick loop is single-threaded",
            "T:System.Threading.ThreadPool;SIMCORE007;the tick loop is single-threaded",
        };
    }
}
