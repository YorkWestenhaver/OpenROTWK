# The M3 cross-arch CI gate

`ci.yml`'s `crossarch` + `crossarch-compare` jobs are the M3 cross-arch sign-off gate
(S11 N14a / dr-0005 O4-amended): the same scripted map corpus, run through the real
`OpenSage.SimCore.ScenarioDriver` pipeline on three different OS/architecture combinations,
must produce byte-identical per-frame per-channel CRC streams. A pass here is the evidence
that Mac (arm64) and PC (x64) do not desync on the corpus it covers.

This doc is the operator runbook: what the gate actually checks, how to read a failure, and
how to reproduce one locally. It is written against `OpenSage.SimCore.DumpDiff`'s own report
contract (see the header comment in `src/tools/OpenSage.SimCore.DumpDiff/Program.cs`) - if that
contract changes, this doc needs a matching edit.

## What runs

`crossarch` is a 3-leg matrix (`macos-latest` = arm64, `ubuntu-latest` = x64,
`windows-latest` = x64). Each leg runs:

```
dotnet run --project src/tools/OpenSage.SimCore.ScenarioDriver --configuration Release -- \
  --scenario map-v1 \
  --map src/OpenSage.Game.Tests/Logic/Script/Assets/job009_creep_fight.map \
  --ini src/OpenSage.Game.Tests/Logic/Script/Assets/job009_creep_fight_subset.ini \
  --checkpoint-interval 1 --stream-only --arch-stamp --until-frame 501 \
  --out stream-<leg>.txt
```

and then a second corpus entry, the R14 respawn seam:

```
dotnet run --project src/tools/OpenSage.SimCore.ScenarioDriver --configuration Release -- \
  --scenario respawn-v1 \
  --checkpoint-interval 1 --stream-only --arch-stamp --until-frame 40 \
  --out respawn-<leg>.txt
```

`respawn-v1` is self-stimulating (`src/tools/OpenSage.SimCore.ScenarioDriver/RespawnSeamScenario.cs`):
its kill/purchase/second-death schedule is compiled into the scenario, so it needs neither
`--map` nor `--schedule`. It exists because putting a respawn-carrying hero into the job-009 run
would mean editing a binary `.map`, and because the seam's arms - a dead-but-un-reaped hero
sitting in the Objects walk for many frames, a revive priced through the anchor's float
`CostMultiplier`, and a permanent second death that resolves to the corpse path - are exactly
the shapes an arm64/x64 split would show up in. It is uploaded and compared as its OWN stream,
so a divergence report names which corpus entry broke.

job-009's map and INI subset are checked into this repo (`OpenSage.Game.Tests`'s assets) - no
retail assets and no self-hosted runner are needed. `--until-frame 501` pins job-009's own known
script-exit frame; `--ignore-map-exit` is deliberately not passed (see the ci.yml comment).
Once N14c's corpus registry lands, the frame bound and map list should come from there instead
of being hardcoded in this job.

Each leg is run via `dotnet run` (framework-dependent), never `dotnet publish --runtime <RID>`.
`--arch-stamp` writes `RuntimeInformation.ProcessArchitecture` as it is observed at run time by
the actually-executing process - that is the only way this gate can prove it ran cross-arch
rather than trusting a publish-time RID label. GitHub's `macos-latest` runners are Apple
Silicon; `ubuntu-latest`/`windows-latest` are x64.

`crossarch-compare` downloads all six streams (three legs x two corpus entries) and runs `dumpdiff`
(`src/tools/OpenSage.SimCore.DumpDiff`) pairwise: macos-vs-ubuntu and macos-vs-windows (both
with `--require-cross-arch`, since those pairs are expected to mix architectures) and
ubuntu-vs-windows (without the flag - both legs are X64, so requiring cross-arch there would
always fail for a reason unrelated to a real divergence). All three comparisons are full,
untrimmed, per-channel comparisons - no `--exclude-a`/`--exclude-b`, no tolerance loosening.
Per-channel equality is the point; do not add a "compare only Combined" shortcut here even
under CI flakiness pressure - fix the flake instead.

## The metadata convention (why arch/exclude are recoverable from the dump itself)

`DeepCrcWriter.Comment` writes a `# <text>` line. `DumpParser.TryHarvestMetadata` harvests any
comment shaped exactly `# key=value` (no spaces around `=`). ScenarioDriver's `--arch-stamp`
writes `# arch=<Arm64|X64|...>` and `# rid=<RID>` as two separate lines (never packed onto one
line together - that was the shape mismatch this packet reconciled: the pre-reconcile emitter
wrote `# arch <Arch> <Rid>` on one line, which the parser's `# key=value` harvester silently
ignored, so every leg's arch read back as "unspecified" and `--require-cross-arch` could never
actually pass). `--exclude` writes one `# exclude=<comma-separated names>` line (or nothing, if
no channels are excluded - the comparator treats a missing key as "unspecified", never as an
error). `ScenarioDriverCliTests.ArchAndExcludeMetadata_RoundTripsThroughDumpDiffParser` is the
cross-tool test that keeps the two projects' independent documentation of this shape from
drifting apart again silently.

## Reading a failure

**A `crossarch` leg fails (exit 2, before comparison even runs):** the driver itself errored on
that OS - a build/run failure, not a desync. Check that leg's job log first; `crossarch-compare`
will fail too (missing artifact) but that's a downstream symptom, not the cause.

**`crossarch-compare` exits 2 (`CrossArchRequirementUnmet`):** the combined `arch` stamps for
that pair didn't cover both `Arm64` and `X64`. If this fires on the macos-vs-ubuntu or
macos-vs-windows step specifically, suspect the macOS runner image (did GitHub switch it to an
x64 image?) before suspecting the code - this gate has never been proven green on a real runner
as of the wave that added it, so a first-run surprise here is plausible.

**`crossarch-compare` exits 1 (a real divergence):** `dumpdiff`'s human-readable report (printed
to the step's stdout) states, in order: the two leg labels, the last frame both streams still
agreed on, the frame the divergence was detected at, the channel (ordinal + name) in effect, and
then either the full R-record (object id, module index, tag, class, field, tolerance, type, both
sides' hex bytes) or the full per-channel V-line vector from both sides, plus the exclusion set
each leg ran with. A single-line machine-readable JSON form with the same facts follows on the
next stdout line. Start from the channel name and field name - that is a pointer directly at the
module whose `[SimState]`-tagged field is arch-sensitive (an unguarded `float`/hash/iteration-
order dependency the SIMCORE analyzer didn't catch because it lives in a tool assembly, or a
genuine platform difference in an integer path that should not exist).

**A dump has zero `V` lines, or the two dumps have different lengths where the shorter is a
clean prefix of the longer:** `dumpdiff` treats both as real failures (exit 2 for zero
checkpoints, exit 1 for the prefix case) - never a silent pass. If you see a green gate on a run
that produced no checkpoints, that is a `dumpdiff` bug, not a working gate; file it as one.

## Reproducing a divergence locally

Download the two (or three) `crossarch-stream-<os>` artifacts from the failed run, then:

```
dotnet run --project src/tools/OpenSage.SimCore.DumpDiff --configuration Release -- \
  stream-macos-latest.txt stream-ubuntu-latest.txt \
  --label-a macos-latest --label-b ubuntu-latest --require-cross-arch
```

This is the exact command the CI job runs (module the artifact directory prefix), so a local
repro should reproduce byte-for-byte.

## Status as of this gate's introduction (R14 wave 1)

This is the first wave in which the 6-leg `determinism` matrix, the `osx-x64` -> `osx-arm64`
artifact rename in the `build` job, and this `crossarch`/`crossarch-compare` pair all reach a
real GitHub Actions runner together. None of the three had been proven green on an actual
runner before this push. Do not treat a clean run of any of them as a settled fact until you
have seen it happen - watch the first Actions run on this branch/PR directly rather than
trusting this document's description of what "should" happen.

`OpenSage.SimCore.DumpDiff.Tests` (17 tests) is deliberately kept out of `src/OpenSage.sln` -
its sibling `OpenSage.SimCore.ScenarioDriver.Tests` is registered in the sln instead. The two
wave-0 packets that created them made opposite sln-visibility choices; `ci.yml`'s `build` job
invokes `OpenSage.SimCore.DumpDiff.Tests` by explicit project path for exactly this reason. If
you add a new out-of-sln test project anywhere in `src/tools`, it needs the same explicit
`dotnet test <project-path>` step or it silently never runs - `dotnet test src` (or `dotnet test
src/OpenSage.sln`) only walks the sln's registered projects.
