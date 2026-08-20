OpenROTWK
=========

**OpenROTWK is a free, open-source engine for *The Lord of the Rings: The Battle for
Middle-earth II — The Rise of the Witch-king***, in the tradition of OpenRCT2, OpenMW,
and OpenRA: a reimplemented engine that plays the original game from your own legally
owned installation's data files. It is built on the foundation of
[OpenSAGE](https://github.com/OpenSAGE/OpenSAGE) and on the Command & Conquer:
Generals source code released by EA under GPL v3.

The goal is simple to state: **make Rise of the Witch-king work — verifiably.**
Not a remake, not a reimagining: the original game, its expansions of behavior, and
its mods (data-driven mods such as *Age of the Ring* are the primary compatibility
target), running natively on modern hardware — including Apple Silicon — with
desync-free online play between Mac and PC.

## Design decisions, and why

Like every SAGE RTS, ROTWK is a **lockstep** game: the network carries only player
orders, and every machine independently simulates the entire world, trusting that
identical inputs produce identical state. If two machines ever disagree by a single
bit — one unit's position off by the last decimal place — the simulations fork and
the match desyncs. So the whole design question is: *what makes two computers
compute exactly the same bits?*

The retail engine's answer is **"be the same binary on the same CPU family."** It
runs its simulation on the x87 floating-point unit of 32-bit x86 processors, with
the FPU explicitly pinned to a fixed precision mode (`_controlfp`) so that every
player's machine executes the identical instruction sequence with identical
rounding. That works — and it is also a trap. Floating-point results are only
reproducible when the *exact same* instructions run in the *exact same* order:
a different compiler, a different optimization pass, or a different architecture
will reorder operations and round intermediates differently, all while being
perfectly IEEE-correct. And Apple Silicon doesn't even have an x87 unit — ARM64
does its float math on NEON, with different instruction selection and none of
x87's 80-bit intermediate behavior. Even x86 emulation layers don't reproduce
x87's bit-exact quirks. The consequence is stark: **retail byte-fidelity and
cross-platform play are mutually exclusive.** You can match the original binary,
or you can run one simulation across a Mac and a PC — never both.

OpenROTWK chooses the second, and rebuilds correctness on foundations that don't
depend on any FPU at all — integer arithmetic, which is bit-identical on every
CPU ever made:

1. **Fixed-point deterministic simulation.** All game logic runs on a `Fix64`
   fixed-point numeric core (custom div/sqrt/trig, deterministic RNG, lockstep tick
   loop), enforced by a compile-time analyzer that quarantines floating point out of
   sim code. The same replay produces the same bits on every platform and
   architecture. This is what makes cross-architecture multiplayer possible at all.

2. **Conformance measured against retail, not against intuition.** "Correct" here
   means "matches the original game's observed behavior," and that is measured, not
   assumed: the retail game (running from a legally owned installation) serves as a
   test oracle, and this engine's simulation state is diffed against retail's
   frame-by-frame within explicit tolerances — per object, per field. Divergences
   are named, ranked, and fixed. Fidelity is a test suite, not a vibe.

3. **SAGE-data-native.** The engine consumes the original INI/map/model data formats
   directly, so the game's own content — and twenty years of community mods built on
   those formats — run without conversion or porting.

## Status

The deterministic foundation and all eight core simulation systems are complete;
current work is the behavior-module long tail, skirmish AI, and netcode completion.

### Deterministic foundation

* [x] Fix64 fixed-point numeric layer (custom div/sqrt/trig)
* [x] Float-quarantine Roslyn analyzer (no floats in sim code)
* [x] SAGE-compatible deterministic RNG
* [x] Deterministic tick loop + order pipeline
* [x] Checksum / sync framework
* [x] Run-twice determinism test suites

### Core sim systems

* [x] Weapon / damage / armor pipeline
* [x] Locomotor / movement physics
* [x] Partition / vision / line-of-sight / shroud
* [x] Economy / production / veterancy
* [x] Pathfinding (deterministic A*; retail-timing conformance oracle-gated)
* [x] Hordes (BFME formation system)
* [x] Castles / build plots
* [x] Script-engine runtime subset
* [ ] Skirmish AI (design complete, implementation pending)
* [ ] Object lifecycle hardening (partial, ongoing)
* [ ] Netcode / lockstep completion (lobby, match flow, desync recovery)

### Behavior modules

* [x] 74 / 169 runtime behavior modules implemented
* [ ] 95 remaining (69 gated on behavioral specifications)

### Conformance oracle

* [x] Self-diff harness
* [x] Retail state capture + canonical dump schema
* [x] First full-battle pointwise diff vs. retail executed (divergence fix list active)
* [ ] Full-battle conformance within tolerance
* [ ] Cross-architecture replay conformance

### Rendering & presentation (inherited from OpenSAGE, being completed for BFME2)

* [x] W3D models and animations
* [x] Map terrain, roads, water
* [x] Particle systems
* [ ] W3D skinned-mesh completeness for BFME2-era assets
* [ ] BFME2 in-game HUD
* [ ] APT menu screens (18 of 40 currently fail to load)
* [ ] Audio wiring (music, SFX, voice hookup to sim events)

### The long tail

* [ ] Campaign and living world
* [ ] War of the Ring meta-game
* [ ] Create-a-Hero

## Platforms

* [x] macOS (Apple Silicon and Intel) — Metal
* [x] Windows — Direct3D 11 / OpenGL 4.3
* [x] Linux — OpenGL 4.3

## Legal

* This project is not affiliated with or endorsed by Electronic Arts, Warner Bros.
  Discovery, Middle-earth Enterprises, or the Tolkien Estate in any way. *The Lord of
  the Rings: The Battle for Middle-earth II* and *The Rise of the Witch-king* are
  trademarks of Electronic Arts, published under license from the rights holders to
  *The Lord of the Rings*. Command & Conquer™ is a trademark of Electronic Arts.
* This project is non-commercial. The source code is free and always will be,
  licensed under GPL v3.
* To play anything with this engine you will need a legally acquired installation of
  the original game. These titles are no longer commercially sold, but a legal copy
  is still required: this project uses the original game's data files at runtime and
  will never distribute them.
* **How this engine was written.** The code comes from four sources, kept
  deliberately separate:
  1. **Licensed source code**: portions are translated from the Command & Conquer:
     Generals / Zero Hour source code that Electronic Arts released under GPL v3
     (see `LICENSE-EA.md`) — the closest published ancestor of the BFME2-era engine.
     This is licensed use, not reverse engineering.
  2. **Reading data files** (INI, map, model, and archive formats) and **observing
     the retail game running**, as in OpenSAGE.
  3. **Behavioral specifications**: for gameplay systems that exist only in
     BFME2-era SAGE and have no released source (hordes, castle build-plots, and
     similar), behavior was studied from the retail game and documented as written
     specifications describing *what the game does*; implementation code was then
     written from those specifications and the game's own data files. Executable
     code is never copied, decompiled into, or derived from the retail binary.
  4. **Conformance testing**: the retail game, running from a legally owned
     installation, is used as a test oracle — this engine's output is *compared
     against* the original game's observed behavior to measure accuracy.
     Observations are used to grade the reimplementation, not as source material
     for it.
* No assets, data files, or other content from the original games — or from any
  third-party mod — are included in this repository. Third-party mods (such as
  *Age of the Ring*) remain the property of their respective authors, and running
  them requires obtaining them from their authors.
* If any rights holder has concerns about this project, please open an issue and we
  will engage in good faith.

A note on the name: SAGE and the "Open-" engine-reimplementation convention are not
EA trademarks; the game titles are, and are used here descriptively to identify
which game this engine runs.

## Acknowledgements

**This project stands on [OpenSAGE](https://github.com/OpenSAGE/OpenSAGE), and the
debt is enormous.** Tim Jones and the OpenSAGE contributors spent years building the
rendering pipeline, the file-format loaders, and the SAGE research that this project
inherits — the entire presentation layer and data plumbing of this engine is their
work, continued. Massive, unreserved thanks. This repository preserves the full
OpenSAGE commit history, and their authorship, deliberately — which is why OpenSAGE
contributors appear in this repository's contributor list: their code lives here,
and they deserve the credit for it. Their presence in the history is attribution,
not affiliation — OpenROTWK is an independent project, and no endorsement by the
OpenSAGE team or its contributors is implied.

Further thanks to:

* **Electronic Arts**, for releasing the Generals / Zero Hour source under GPL v3 —
  the single most useful artifact for understanding BFME2-era SAGE — and for the
  original games themselves. Admiration for the original SAGE teams only grows with
  every system reimplemented.
* **Stephan Vedder ([feliwir](https://github.com/feliwir))** for years of pioneering
  work on SAGE data formats, foundational to OpenSAGE and therefore to this project.
* **DeeZire's** module documentation, still useful two decades on.
* The BFME community keeping these games alive: the
  [Open-BFME](https://github.com/Open-BFME) source-recovery project, the
  [Age of the Ring](https://www.moddb.com/mods/age-of-the-ring) team, and the
  patch, launcher, and tooling maintainers.

## Similar projects

* [OpenSAGE](https://github.com/OpenSAGE/OpenSAGE) — general SAGE engine
  reimplementation focused on Generals / Zero Hour; this project's parent.
* [Open-BFME-1](https://github.com/Open-BFME/Open-BFME-1) — byte-matching source
  recovery of the first Battle for Middle-earth game.
* [openbfme-godot](https://github.com/Open-BFME/openbfme-godot) — a BFME2 remake in
  Godot using converted original assets.
* [OpenRA](https://www.openra.net/) — the model for what "Open-" engines can become.
