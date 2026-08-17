; Unshipped analyzer releases
; The SIMCORE ids are frozen by api-freeze-v1 F10; they are never renumbered or reused.

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SIMCORE001 | SimCore.Quarantine | Error | Floating-point types are banned inside the simulation quarantine
SIMCORE002 | SimCore.Quarantine | Error | System.Math/System.MathF/System.Numerics are banned inside the simulation quarantine
SIMCORE003 | SimCore.Determinism | Error | Nondeterministic ambient source
SIMCORE004 | SimCore.Determinism | Error | Iteration order is not deterministic
SIMCORE005 | SimCore.Determinism | Error | Hash or enum ordering is not stable across processes
SIMCORE006 | SimCore.Determinism | Error | Mutable static state in simulation code
SIMCORE007 | SimCore.Determinism | Error | Asynchrony or threading in simulation code
SIMCORE010 | SimCore.Determinism | Warning | Squared-magnitude multiply outside FixMath
