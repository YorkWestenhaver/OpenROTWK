# The SimCore quarantine analyzer

`OpenSage.SimCore.Analyzers` is the compile-time half of the determinism wall. The other half is
structural: `OpenSage.SimCore` references only `OpenSage.Core` and the BCL, so a `float` can enter
the simulation only by being typed into a SimCore file — and this analyzer catches that.

Every rule id is contractual. They appear in suppressions and in review diffs, so they are never
renumbered or reused.

| ID | Severity | What it rejects |
|---|---|---|
| SIMCORE001 | error | `float` / `double` / `System.Half` in any declaration, cast, local or literal |
| SIMCORE002 | error | `System.Math`, `System.MathF`, anything under `System.Numerics` |
| SIMCORE003 | error | `System.Random`, `Guid.NewGuid`, `DateTime.*Now`, `Environment.TickCount*`, `Stopwatch` |
| SIMCORE004 | error | `foreach` over `Dictionary`/`HashSet`/`.Keys`/`.Values`; `GroupBy`, `Distinct`, `ToDictionary`, `ToHashSet`, `ToLookup`; default-comparer `OrderBy` over an unordered source |
| SIMCORE005 | error | `System.HashCode`, `Enum.GetValues`/`GetNames`, `GetHashCode()` on a string or reference type |
| SIMCORE006 | error | mutable `static` fields (not `const`, not `readonly`) |
| SIMCORE007 | error | `async`, `Task`, `ValueTask`, `Parallel`, `Thread`, `ThreadPool` |
| SIMCORE010 | warning | a `Fix64` product of two squared magnitudes outside `FixMath` — Q31.32 saturates at ~2.1e9, so fourth powers must go through the 128-bit wide compare |

`System.Math` is banned wholesale, with no carve-out for the integer overloads: `FixMath` carries
`Min`/`Max`/`Clamp` for `int`, `long` and `Fix64`, which keeps the rule zero-exception and keeps
`using System;` from quietly re-opening the float surface.

`GetHashCode()` on a value type stays legal — `long.GetHashCode()` is a fixed xor-fold. What
SIMCORE005 removes is the per-process seed: `System.HashCode` and string hashing differ between two
runs of the same binary, so anything derived from them diverges silently. SimCore's own value types
fold their raw Q31.32 words through `DeterministicHash` instead.

## Attachment modes

**Full** — every file is simulation code. `OpenSage.SimCore` sets
`<SimCoreAnalyzerMode>full</SimCoreAnalyzerMode>`; the analyzer additionally treats the assembly
named `OpenSage.SimCore` as full mode unconditionally, so losing the property cannot silently
disarm the wall.

**Scoped** — the migration mode, used by `OpenSage.Game`, which cannot go float-free wholesale. A
file is analyzed only if its path lies under a directory listed in `SimCoreScopedDirs.txt`, or if
it declares a type marked `[SimState]` (matched by name, so a project need not reference SimCore to
opt in). **That list is the migration progress meter**: as a system moves to fixed-point state its
directory is added, and the entry is a promise that everything beneath it compiles clean.

Both modes attach the same way:

```xml
<ProjectReference Include="..\OpenSage.SimCore.Analyzers\OpenSage.SimCore.Analyzers.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

plus `<CompilerVisibleProperty Include="SimCoreAnalyzerMode" />` and the registry files as
`AdditionalFiles`.

## Extending the ban list

`BannedSymbols.txt` next to the csproj is additive on top of a seed list compiled into the analyzer.
This is where the nondeterminism inventory grows — add a line rather than editing the analyzer:

```
M:System.Environment.ProcessorCount;SIMCORE003;machine-dependent; cannot influence sim state
```

The symbol id is `N:Namespace`, `T:Namespace.Type` or `M:Namespace.Type.Member`; the rule id
(`SIMCORE002`, `003`, `005` or `007`; default `002`) and the reason are optional. A namespace ban
covers everything beneath it.

## Exemptions are a pair

A file escapes the quarantine only when **both** halves are present:

1. a `// SIMCORE-EXEMPT: <reason>` comment in the file header, and
2. a project-relative entry in `SimCoreExemptions.txt`.

Half a pair is not an exemption — the rules keep firing. This makes lifting the wall a two-place,
reviewable diff, and it is enforced by tests in `OpenSage.SimCore.Analyzers.Tests`. Keep exempt
files as small as possible: the F4 display escape lives alone in `Fix64.Display.cs` precisely so
that the integer-only parse boundaries beside it stay policed.

Current exemptions: `Fix64.Division.cs` and `Fix64.Sqrt.cs` (hardware double used only as a first
guess, with an integer fixup that makes the result guess-independent) and `Fix64.Display.cs` (the
one blessed float-typed display boundary).
