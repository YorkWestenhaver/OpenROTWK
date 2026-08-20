![OpenSAGE](/art/opensage-logo.png)
============================================================

[![Build Status](https://github.com/OpenSage/OpenSage/workflows/CI/badge.svg)](https://github.com/OpenSAGE/OpenSAGE/actions)
[![Discord Chat](https://img.shields.io/discord/398393968234332161.svg?logo=discord)](https://discord.gg/G2FhZUT)
[![codecov](https://codecov.io/gh/OpenSAGE/OpenSAGE/branch/master/graph/badge.svg)](https://codecov.io/gh/OpenSAGE/OpenSAGE)

**OpenSAGE**: a free, open source re-implementation of [SAGE](https://en.wikipedia.org/wiki/SAGE_(game_engine)), the 3D 
real time strategy (RTS) engine used in Command & Conquer™: Generals and other 
RTS titles from EA Pacific.

This project is being developed with an initial focus on Command & Conquer:
Generals and Command & Conquer: Generals Zero Hour. Support for other SAGE-based
games may come later. The primary development target is Windows, with support
planned for macOS at a later date.

## This fork: deterministic BFME2 sim core (`simcore-scaffolding`)

This fork builds a **deterministic, fixed-point simulation core** on top of OpenSAGE,
targeting bit-conformant *The Battle for Middle-earth II: Rise of the Witch-king*
gameplay and desync-free cross-architecture multiplayer (Apple Silicon ↔ x86). Engine
behavior is graded against the retail engine via an external conformance oracle.

### Deterministic foundation

* [x] Fix64 fixed-point numeric layer (custom div/sqrt/trig)
* [x] Float-quarantine Roslyn analyzer (no floats in sim code)
* [x] SAGE-compatible deterministic RNG (`LogicRandom`)
* [x] Deterministic tick loop + order pipeline (`SimLoop`)
* [x] `XferCrc` checksum / sync framework
* [x] Run-twice determinism test suites

### Core sim systems

* [x] S1 — Weapon / damage / armor pipeline
* [x] S2 — Locomotor / movement physics
* [x] S3 — Partition / vision / line-of-sight / shroud
* [x] S4 — Economy / production / veterancy
* [x] S5 — Pathfinding (deterministic A*; retail-timing conformance oracle-gated)
* [x] S6 — Hordes (BFME formation system, clean-room spec)
* [x] S7 — Castles / build plots (clean-room spec)
* [x] S8 — Script-engine runtime subset
* [ ] S9 — Skirmish AI (design doc complete, implementation pending)
* [ ] S10 — Object lifecycle hardening (partial, ongoing)
* [ ] S11 — Netcode / lockstep completion (M3 critical path)

### Behavior modules

* [x] 74 / 169 runtime behavior modules implemented (was 12 at fork baseline)
* [ ] 95 remaining (69 gated on clean-room behavioral specs)

### Conformance oracle

* [x] Target-A self-diff harness
* [x] Retail memory-probe state capture (v2/v3) + canonical ddump schema
* [x] First full-battle pointwise diff vs. retail executed (divergence fix list active)
* [ ] Full-battle conformance within tolerance (M1)
* [ ] Cross-architecture replay conformance (M3)

## Work in progress (upstream roadmap)

This project is in the *very* early stages. There is still a long way to go before there's anything playable. 
The initial focus is on understanding the data formats used in Command & Conquer: Generals and Zero Hour.

Here's a rough roadmap:

### Parse data files

* [x] `.map`
* [x] `.w3d`
* [x] `.wak`
* [x] `.wnd`
* [x] `.csf`
* [x] `.ini`
* [x] `.ani`
* [x] `.dds`
* [x] `.tga`
* [x] `.const`
* [x] `.apt`
* [x] `.wav`
* [x] `.mp3`

> **Note:** the checkboxes below reflect the state of *this fork's* branch
> (`simcore-scaffolding`), whose sim-layer items are deterministic fixed-point
> implementations as described in the fork section above — not upstream `master`.

### Rendering

* [x] Render 3D models loaded from `.w3d`, including animations
* [x] Render maps loaded from `.map` (terrain, roads, water)
* [x] Render particle systems
* [ ] Render GUI loaded from `.wnd` (Generals-era UI; BFME2 uses APT)
* [ ] W3D skinned-mesh completeness for BFME2-era assets
* [ ] BFME2 in-game HUD
* [ ] APT menu screens (18 of 40 AotR screens currently fail to load)

### Game logic

* [x] Scripting engine (deterministic runtime subset)
* [x] Physics / movement (locomotors)
* [x] Weapons, damage, and armor
* [x] Pathfinding (retail-timing conformance pending oracle validation)
* [x] Economy, production, and veterancy
* [x] Partition, vision, and shroud
* [x] Hordes and formations
* [x] Castles and build plots
* [ ] Remaining behavior modules (95 of 169; see fork section)
* [ ] APT virtual machine completeness (menus/HUD scripting)
* [ ] Skirmish AI (base building, fighting)
* [ ] Network play (lockstep core exists; lobby, match flow, desync recovery pending)
* [ ] Audio wiring (music, SFX, voice hookup to sim events)
* [x] Input (keyboard, mouse)
* [ ] Campaign and living world
* [ ] War of the Ring meta-game
* [ ] Create-a-Hero

### Platforms

* [x] Windows
  * OpenGL 4.3
  * Direct3D 11
* [x] Mac
  * Metal 2 (requires macOS High Sierra)
* [x] Linux
  * OpenGL 4.3

## Legal disclaimers

* This project is not affiliated with or endorsed by Electronic Arts, Warner Bros. Discovery, Middle-earth Enterprises, or the Tolkien Estate in any way. Command & Conquer™ is a trademark of Electronic Arts. *The Lord of the Rings: The Battle for Middle-earth II* and *The Rise of the Witch-king* are trademarks of Electronic Arts, published under license from the rights holders to *The Lord of the Rings*.
* This project is non-commercial. The source code is free and always will be, licensed under GPL v3.
* **This fork targets *The Battle for Middle-earth II: The Rise of the Witch-king***, a later BFME2-era revision of the SAGE engine, rather than upstream OpenSAGE's focus on Command & Conquer: Generals and Zero Hour. To play anything with it you will need a legally acquired installation of the original game. These titles are no longer commercially sold, but a legal copy is still required: this project uses the original game's data files at runtime and will never distribute them.
* **How this fork was written.** The code in this fork comes from four sources, kept deliberately separate:
  1. **Licensed source code**: portions are translated from the Command & Conquer: Generals / Zero Hour source code that Electronic Arts released under GPL v3 (see `LICENSE-EA.md`) — the closest published ancestor of the BFME2-era engine. This is licensed use, not reverse engineering.
  2. **Reading data files** (INI, map, model, and archive formats) and **observing the retail game running**, as in upstream OpenSAGE.
  3. **Behavioral specifications**: for gameplay systems that exist only in BFME2-era SAGE and have no released source (hordes, castle build-plots, and similar), behavior was studied from the retail game and documented as written specifications describing *what the game does*; implementation code was then written from those specifications and the game's own data files. Executable code is never copied, decompiled into, or derived from the retail binary.
  4. **Conformance testing**: the retail game, running from a legally owned installation, is used as a test oracle — this fork's output is *compared against* the original game's observed behavior to measure accuracy. Observations are used to grade the reimplementation, not as source material for it.
* No assets, data files, or other content from the original games — or from any third-party mod — are included in this repository. Third-party mods (such as *Age of the Ring*) remain the property of their respective authors, and running them requires obtaining them from their authors.
* If any rights holder has concerns about this project, please open an issue and we will engage in good faith.

A note on the name: while Command & Conquer is a trademark of EA, SAGE is not (as far as I can tell, based on a US trademark search). "OpenSAGE" seems like a good way to make it clear what the project is about, without infringing on trademarks.

## About

OpenSAGE is being created by me, Tim Jones. I was at university in February 2003 when C&C Generals was first released. I spent far too much time playing it and the sequel, Zero Hour (and as a consequence, not enough time studying). In my opinion, as a near-real-world RTS game, it is still unmatched even 14 years later.

I later bought The First Decade on DVD, and it was this copy I wanted to install in Windows 10. I even bought an external DVD drive in order to do so. It wasn't straightforward, and it made me worry that one day I won't be able to play what is still my favourite RTS game.

One thing led to another, and I found myself opening `.map` files in a hex viewer. I had the idea of recreating the game, using the original assets. This appealed to me on many levels: it's a preservation of history, it satisfies my nostalgia, and it's an extreme programming challenge.

It's a vast project, and who knows how far I'll get. Hopefully we'll all have some fun along the way.

## Community

We have a growing [OpenSAGE Discord](https://discord.gg/G2FhZUT) community. If you have questions about the project or can't get it working,
there's usually someone there who can help out.

## Acknowledgements

First, I would like to thank Stephan Vedder ([feliwir](https://github.com/feliwir)) for his efforts, over several years, to understand
several of the key SAGE data formats, including `.w3d`. Without his prior work, I would have had a much harder time getting started.

DeeZire's [module list](http://www.redsys.su/mkportal/files/ModuleList.txt) has been extremely helpful in understanding all the `Object` parameters in `.ini` files.

The font used in the OpenSAGE logo was created by Dexistor371 and is available from [DeviantArt](https://dexistor371.deviantart.com/art/Command-and-Conquer-logo-font-396527879).

The sage / leaf icon used in the OpenSAGE logo was created by Monjin Friends and is licenced under [Creative Commons CC BY 3.0 US](https://creativecommons.org/licenses/by/3.0/us/). It is available [from the Noun Project](https://thenounproject.com/term/leaf/1052490/).

Finally, I want to thank the original team who built Generals and Zero Hour, because without their work, I wouldn't be doing any of this.
(As I get further into the project, I'm gaining ever greater admiration for what those people were able to achieve 14 years ago, both technically
and artistically.)

## Similar projects

These projects have similar goals:

* [Arda](https://github.com/feliwir/arda)
* [smx-smx/openSage](https://github.com/smx-smx/openSage)

[OpenRA](https://www.openra.net/) already does for the Westwood RTS games what I hope to do for the EA-era RTS games with OpenSAGE.
