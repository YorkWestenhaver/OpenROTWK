Contributing to OpenROTWK
=========================

Contributions are welcome. This document says what we care about; the
[Developer Guide](docs/developer-guide.md) covers how to build and run.

## AI-assisted contributions are 100% welcome

This project was built by a human directing AI agents at scale, and contributions
produced the same way are explicitly welcome — most of this codebase came into
existence that way. We don't care how the code was written. We care whether it is
correct, verified, and honestly presented. What that means in practice:

* You (the human submitting) are responsible for the contribution. You should be
  able to answer questions about it. "The AI wrote it and I didn't look" is not a
  contribution; "I directed this, verified it against the gates below, and stand
  behind it" is — regardless of who or what typed the code.
* Don't disclose or apologize for AI use; it's normal here. Do disclose what
  verification you ran.

## Proposing a major change: show your alternatives

For any significant change — a new system, an architectural shift, a dependency, a
rework of something that already works — the proposal (issue or PR description)
must include:

1. **Purpose** — what problem this solves, and why it needs solving. "For the sake
   of it" changes (rewrites for taste, style crusades, modernization without a
   concrete payoff) will be declined.
2. **The alternatives you considered** — at least the other viable options, and
   **why you rejected them**. This is not a formality: if you haven't genuinely
   weighed multiple paths, the proposal isn't ready. A design chosen without
   alternatives isn't a decision, it's a default.
3. **How it will be verified** — which existing gates cover it, and what new tests
   or conformance evidence it brings.

Small fixes don't need this ceremony — a clear description and passing gates are
enough.

## Hard rules (these are the project)

* **No floating point in simulation code.** All sim math is `Fix64` fixed-point;
  the float-quarantine analyzer enforces this at build time and its failures are
  never suppressed. This is the cross-platform determinism guarantee — see the
  README's design section for why.
* **Determinism is tested, not assumed.** The run-twice CRC gates in CI must stay
  green. If your change makes the same inputs produce different bits, it is wrong
  even if it looks right.
* **Conformance beats intuition.** Where retail behavior is known (GPL source,
  behavioral specs, oracle captures), match it. "The original game does X" wins
  arguments; "I think it should do Y" belongs in a mod.
* **Provenance: four sources only.** Code may come from translating EA's GPL
  Generals/Zero Hour release, from reading data files, from observing the retail
  game, or from written behavioral specifications. **Never** from decompiled or
  binary-derived code — not the retail binary, and not other projects' decompiled
  output (e.g. byte-matching recovery projects). If your contribution's lineage
  can't be stated in those terms, it can't be merged.
* **No game assets, ever.** Nothing from the retail games or third-party mods
  enters this repository — not even "just for testing."

## Practical points

* Keep PRs focused: one change per PR. Separate refactors from behavior changes.
* Every commit should build; the full test suite must pass.
* When touching shared registries or widely-included files, keep additions
  minimal and uniquely named — most integration pain in this project's history
  has come from two changes colliding in a shared file.
* Style: match the surrounding code. Don't send style-only PRs.
