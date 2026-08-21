// AutoPickUpUpdate - R13 port. No exact generals-gpl sibling (confirmed by the audit's grep
// over generals-gpl + generals-community for "PickUp"/"EatObject": crate-collide/supply-truck/
// AI-guard hits only). Composed from two landed idioms instead of a single GPL translation
// target (bfme2-workbench/research/modules-r13/specs/AutoPickUpUpdateModuleData.md §0):
// PickupStuffUpdate's scan/consume shape (periodic radius scan, first-match-in-ascending-
// ObjectId-order, immediate DestroyObject) and EmpUpdate's multi-gate structure (several
// independent boolean/filter gates AND'ed together before acting on a candidate).
//
// Derived behavior (data-derivable composition, no invented mechanic - see spec §1):
//   1. Hunger gate: self is only eligible to scan/eat when its current health fraction is at or
//      below EatObjectEntry.MaxHealth (the "MyHealth" attribute). Not an edge-triggered input
//      (no OnDamage/OnHeal hook on UpdateModule, api-freeze-v1 §3), so this is a poll-per-scan-
//      tick check, same shape as BaseRegenerateUpdate's poll-based health check. While not
//      hungry, the module still re-arms on the normal ScanDelayTime cadence rather than sleeping
//      forever.
//   2. Scan: when hungry, scan Context.Partition.QueryObjectsInRadius(self, ScanDistance)
//      (ascending ObjectId, the frozen S3 partition-order convention) for the first candidate
//      passing all of: not self/not destroyed; PickUpKindOf gate (passes if unset, or
//      intersects candidate.Definition.KindOf); PickUpFilter gate (passes if unset, or
//      PickUpFilter.Matches); EatObjectEntry.Filter gate (passes if unset, or Filter.Matches).
//      These three are AND'ed, not OR'ed (finding F-APU-1, a composition choice by analogy to
//      EmpUpdate's own independent-gate layering, filed for review - not GPL-sourced).
//   3. Consume-and-heal: destroy the first qualifying candidate (Context.GameLogic.
//      DestroyObject, exactly PickupStuffUpdate's own call - no move-to-target/collision step
//      is modeled, same documented gap PickupStuffUpdate's own header carries, filed as
//      F-APU-2), then heal self toward EatObjectEntry.TargetHealth via
//      GameObject.AttemptHealing(healAmount, self), where healAmount = max(0, TargetHealth *
//      MaxHealth - CurrentHealth) - never overheals past TargetHealth.
//   4. Re-arm: always returns UpdateSleepTime.Frames(ScanDelayTime) regardless of outcome
//      (matches PickupStuffUpdate's own unconditional re-arm).
//
// FINDINGS (filed, not invented):
//   F-APU-1 (PickUpKindOf/PickUpFilter/EatObjectEntry.Filter AND composition): see above.
//   F-APU-2 (no move-to-target step): same documented gap as PickupStuffUpdate - retail likely
//     issues a MOVE order and collects on arrival; S5 move orders are AIUpdate-side and
//     unported. This port models immediate consumption of an in-range match.
//   F-APU-3 (Bored / BoredFilter - parsed, not wired): AutoPickUpUpdateModuleData has no
//     SpecialPowerTemplate field, unlike its sibling BoredUpdateModuleData, so Bored cannot
//     drive a "fire a power when idle" action here. The only other plausible reading (gate the
//     eat-scan on an idle/not-recently-in-combat state) has no engine facade to test today
//     (GameObject exposes no IsIdle/IsBored/current-AI-action accessor). Parsed for round-trip
//     fidelity ([SimDataAudited]), not wired into the scan/eat gate. Revisit once
//     BoredUpdateModuleData lands and either exposes a reusable idle-state facade or its own
//     audit resolves the field pair's semantics.
//   F-APU-4 (RunFromButton / RunFromButtonNumber - UI hook, not wired): a player command-button
//     hook to interrupt/flee, thinly specified by name alone. No CommandButton/UI dispatch
//     exists on the [SimState] side of this engine for an Update module to hook into. Parsed,
//     ships as a no-op field.
//   F-APU-5 (GameObject.HealthFix64/MaxHealthFix64 - additive facade): see GameObject.cs; the
//     same D-7 additive-facade pattern already used for VisionRange/CollisionMinorRadius/
//     MaxHeightAbovePosition, added here because this is the first module needing a Fix64-safe
//     health reader.
//
// S5 parser-type fixes (same class as DestroyEnvironmentUpdate's R13 fix and
// SupplyWarehouseCripplingBehavior's R10 fix): ScanDelayTime -> ParseDurationLogicFrames
// (LogicFrameSpan), ScanDistance -> ParseFix64 (deterministic S3-query radius, exactly
// PickupStuffUpdate.ScanRange's own S5 fix), EatObjectEntry.MaxHealth/TargetHealth quantized to
// Fix64 once at parse time via the same wire-boundary idiom GameObject.VisionRange/
// CollisionMinorRadius/MaxHeightAbovePosition already use (a [SimState]-scoped module may not
// carry a Percentage, float-backed, field into its runtime class).

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AutoPickUpUpdate : UpdateModule
{
    private readonly AutoPickUpUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>How many objects this module has eaten (F-APU-2 parity counter, same shape
    /// as PickupStuffUpdate.NumPickedUp).</summary>
    private int _numEaten;

    public AutoPickUpUpdate(GameObject gameObject, ISimContext context, AutoPickUpUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        if (_data.ScanDelayTime.Value > 0 && _data.ScanDistance > Fix64.Zero)
        {
            SetWakeFrame(UpdateSleepTime.Frames(_data.ScanDelayTime));
        }
        else
        {
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    public int NumEaten => _numEaten;

    public override UpdateSleepTime Update()
    {
        if (!IsHungry())
        {
            return UpdateSleepTime.Frames(_data.ScanDelayTime);
        }

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.ScanDistance))
        {
            if (candidate == GameObject || candidate.IsDestroyed)
            {
                continue;
            }

            if (_data.PickUpKindOf.AnyBitSet && !_data.PickUpKindOf.Intersects(candidate.Definition.KindOf))
            {
                continue;
            }

            if (_data.PickUpFilter != null && !_data.PickUpFilter.Matches(candidate))
            {
                continue;
            }

            if (_data.EatObjectEntry?.Filter != null && !_data.EatObjectEntry.Filter.Matches(candidate))
            {
                continue;
            }

            EatObject(candidate);
            break;
        }

        return UpdateSleepTime.Frames(_data.ScanDelayTime);
    }

    /// <summary>F-APU: self must be at or below EatObjectEntry.MaxHealth ("MyHealth") to
    /// scan/eat (§1.1). Health is not an edge-triggered input, so this is a poll-per-scan-tick
    /// check, only evaluated on the module's own cadence.</summary>
    private bool IsHungry()
    {
        if (_data.EatObjectEntry == null || GameObject.MaxHealthFix64 <= Fix64.Zero)
        {
            return false;
        }

        var healthFraction = GameObject.HealthFix64 / GameObject.MaxHealthFix64;
        return healthFraction <= _data.EatObjectEntry.MaxHealth;
    }

    /// <summary>§1.3: destroy the candidate, then heal self toward TargetHealth (never past
    /// it - AttemptHealing's own clamp-at-max-health is a backstop only if TargetHealth is
    /// authored above 100%).</summary>
    private void EatObject(GameObject candidate)
    {
        Context.GameLogic.DestroyObject(candidate);
        _numEaten++;

        var targetHealth = _data.EatObjectEntry.TargetHealth * GameObject.MaxHealthFix64;
        var healAmount = targetHealth - GameObject.HealthFix64;
        if (healAmount > Fix64.Zero)
        {
            GameObject.AttemptHealing(healAmount, GameObject);
        }
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("NumEaten", ref _numEaten);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

[SimDataAudited]
[AddedIn(SageGame.Bfme)]
public sealed class AutoPickUpUpdateModuleData : UpdateModuleData
{
    internal static AutoPickUpUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<AutoPickUpUpdateModuleData> FieldParseTable = new IniParseTable<AutoPickUpUpdateModuleData>
    {
        // S5: ms -> logic frames (same duration-field convention as every other *Time/*Delay
        // field in this engine, e.g. EmpUpdate.StartFadeTime, PickupStuffUpdate.ScanInterval).
        { "ScanDelayTime", (parser, x) => x.ScanDelayTime = parser.ParseDurationLogicFrames() },
        { "PickUpKindOf", (parser, x) => x.PickUpKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
        // S5: deterministic S3-query radius -> Fix64, exactly PickupStuffUpdate.ScanRange's own
        // S5 fix.
        { "ScanDistance", (parser, x) => x.ScanDistance = parser.ParseFix64() },
        { "EatObjectEntry", (parser, x) => x.EatObjectEntry = EatObjectEntry.Parse(parser) },
        { "Bored", (parser, x) => x.Bored = parser.ParseBoolean() },               // F-APU-3: parsed, unwired
        { "BoredFilter", (parser, x) => x.BoredFilter = ObjectFilter.Parse(parser) }, // F-APU-3: parsed, unwired
        { "RunFromButton", (parser, x) => x.RunFromButton = parser.ParseBoolean() },       // F-APU-4: parsed, unwired
        { "RunFromButtonNumber", (parser, x) => x.RunFromButtonNumber = parser.ParseInteger() }, // F-APU-4: parsed, unwired
        { "PickUpFilter", (parser, x) => x.PickUpFilter = ObjectFilter.Parse(parser) }
    };

    /// <summary>Frames between scans (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan ScanDelayTime { get; private set; }
    public BitArray<ObjectKinds> PickUpKindOf { get; private set; } = new();

    /// <summary>Deterministic S3-query radius (S5).</summary>
    public Fix64 ScanDistance { get; private set; }

    public EatObjectEntry EatObjectEntry { get; private set; }

    /// <summary>F-APU-3: parsed for authoring round-trip fidelity; not wired into the scan/eat
    /// gate (no SpecialPowerTemplate field on this module, no idle-state facade on
    /// GameObject).</summary>
    public bool Bored { get; private set; }

    /// <summary>F-APU-3: parsed, unwired (see <see cref="Bored"/>).</summary>
    public ObjectFilter BoredFilter { get; private set; }

    /// <summary>F-APU-4: parsed for authoring round-trip fidelity; no CommandButton/UI dispatch
    /// exists on the [SimState] side of this engine for an Update module to hook into.</summary>
    public bool RunFromButton { get; private set; }

    /// <summary>F-APU-4: parsed, unwired (see <see cref="RunFromButton"/>).</summary>
    public int RunFromButtonNumber { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public ObjectFilter PickUpFilter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AutoPickUpUpdate(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class EatObjectEntry
{
    internal static EatObjectEntry Parse(IniParser parser)
    {
        return new EatObjectEntry
        {
            MaxHealth = parser.ParseAttribute("MyHealth", parser.ParseFix64Percentage),
            TargetHealth = parser.ParseAttribute("TargetHealth", parser.ParseFix64Percentage),
            Filter = parser.ParseAttribute("Filter", ObjectFilter.Parse)
        };
    }

    // S5: Percentage is float-backed with no Fix64 conversion path (OpenSage.Mathematics/
    // Percentage.cs: readonly float _value throughout), and a [SimState]-scoped file may not
    // name float at all (SIMCORE001 - scope is per-file, so the parse helper is policed too).
    // The percentage text therefore goes straight to Fix64 through the parser's exact decimal
    // path (IniParser.ScanFix64Percentage / Fix64.FromDecimalLiteral), never via float.

    /// <summary>"MyHealth": self's health-fraction threshold to be hungry (§1.1).</summary>
    public Fix64 MaxHealth { get; private set; }

    /// <summary>"TargetHealth": the health fraction eating heals self toward (§1.3).</summary>
    public Fix64 TargetHealth { get; private set; }

    public ObjectFilter Filter { get; private set; }
}
