// RadiateFearUpdate - R13 SPLIT port (see
// bfme2-workbench/research/modules-r13/specs/RadiateFearUpdateModuleData.md for the full port
// spec this file implements). The activation shell + pulse engine are ported here; the entire
// emotion payload (GenerateFear/GenerateTerror/GenerateUncontrollableFear) and
// WhichSpecialPower stay parsed-and-held (posture identical to
// ToggleHiddenSpecialAbilityUpdateModuleData.UnpackingVariation/ShowPalantirTimer) - see the
// HELD section below. This is not a block: the ported half is real, CRC-relevant, deterministic
// and independently testable.
//
// Template precedents used (both landed, both read-only for this port):
// AttributeModifierAuraUpdate.cs (R12 - upgrade-gated periodic radius scan with an
// ObjectFilter gate and an Xfer'd List<ObjectId> of selected targets) and
// AutoAbilityBehavior.cs (R13 - decision-only module that parks its un-landable dispatch as a
// driven, Xfer'd seam with no caller, PendingActivationTargetId/TryConsumePendingActivation).
//
// BASE CLASS: stays UpdateModuleData (ModuleKinds.Update), NOT UpgradeModuleData - UpgradeModule
// has no per-frame Update() hook, only the one-shot OnUpgrade() callback, and this module needs
// a periodic pulse loop. Upgrade-gating is hand-composed via an owned UpgradeLogicData field,
// exactly as AutoHealBehaviorModuleData/AutoAbilityBehaviorModuleData already do.
//
// PARSE-SIDE CORRECTIONS against authored AotR data (spec section 2, section 3 table):
//   - InitiallyActive/TriggeredBy/RequiresAllTriggers are routed through the shared
//     UpgradeLogicData mux instead of module-local fields. createaheropowers.inc:258-268
//     authors a TWO-token TriggeredBy with RequiresAllTriggers = Yes, which the old
//     single-token ParseIdentifier() parse could not represent; ConflictsWith
//     (createaheropowers.inc:261) was missing from the parse table entirely and is exactly what
//     UpgradeLogicData.ConflictsWith means (an upgrade-template reference, not a token
//     collision the way AttributeModifierAuraUpdate's own ConflictsWith is).
//   - EmotionPulseInterval: ParseInteger() (raw ms) -> ParseDurationLogicFrames() (S5 wire
//     boundary, ms ceil-quantized to logic frames at parse).
//   - EmotionPulseRadius: ParseFloat() -> ParseFix64() (F1/S5: a sim-affecting distance feeding
//     Context.Partition.QueryObjectsInRadius(GameObject, Fix64) must not be a float in
//     [SimState] code - SIMCORE001).
//
// HELD, PARSED-NOT-MODELED (never invent - spec section 5):
//   - GenerateFear/GenerateTerror/GenerateUncontrollableFear (the emotion payload): no primitive
//     anywhere in the engine lets one object push an emotion state onto another.
//     EmotionWeaponNugget.cs has no Execute override; EmotionTrackerUpdate.cs is strictly
//     self-directed with a read-only IsAfraid. What an applied fear/terror state IS on a victim
//     (duration, decay, re-pulse composition, interaction with ImmuneToFearLevel) is unspecified
//     in every available clean-room source. Exposed read-only; does NOT gate the pulse.
//   - WhichSpecialPower: a bare integer index with no enum, no lookup table, no consumer
//     anywhere in the engine. Exposed read-only. Do not rename or map to a SpecialPowerTemplate.
//   - ObjectFilter's relationship rule bits (Allies/Enemies/Neutrals/SamePlayer/...) are NOT
//     enforced by ObjectFilter.Matches as landed (KindOf bits only) - an engine-wide shared-file
//     gap this module's port must not paper over locally. Must land together with the payload.
//   - No "triggering upgrade removed" callback exists anywhere in the engine (the mux is
//     one-way) - not implemented here, same gap AttributeModifierAuraUpdate.cs files.
//   - TryConsumePulse has no landed caller by design - the wiring task that lands the
//     emotion-application primitive is also the one that picks this seam up.
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's conformance
// class at its declaration site.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RadiateFearUpdate : UpdateModule, IUpgradeableModule
{
    private readonly RadiateFearUpdateModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Earliest frame the next pulse may land; re-armed by every pulse.</summary>
    private LogicFrame _nextPulseFrame;

    /// <summary>
    /// Victims selected by the most recent unconsumed pulse, ascending ObjectId (the
    /// partition seam's frozen order - never sorted post hoc). Never cleared by Update() itself
    /// - only by TryConsumePulse - so a save/load mid-pending-pulse round-trips correctly. A
    /// new pulse replaces this wholesale rather than appending (a fear pulse is a per-pulse
    /// instantaneous selection, not an accumulating queue).
    /// </summary>
    private readonly List<ObjectId> _pulseVictims = new();

    public RadiateFearUpdate(GameObject gameObject, ISimContext context, RadiateFearUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        SetWakeFrame(UpdateSleepTime.Forever);

        // The mux fires OnUpgradeTriggered from its ctor when StartsActive.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    /// <summary>Victims selected by the most recent pulse, ascending ObjectId; empty when none.</summary>
    internal IReadOnlyList<ObjectId> LastPulseVictimIds => _pulseVictims;

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered() => SetWakeFrame(PulseCadence());

    /// <summary>
    /// Consumes the pending pulse (if any), returning its victim set. Called by a future
    /// emotion-application wiring task - no landed caller exists yet (see the file-header
    /// HELD section).
    /// </summary>
    public bool TryConsumePulse(out ObjectId[] victimIds)
    {
        if (_pulseVictims.Count == 0)
        {
            victimIds = System.Array.Empty<ObjectId>();
            return false;
        }

        victimIds = _pulseVictims.ToArray();
        _pulseVictims.Clear();
        return true;
    }

    public override UpdateSleepTime Update()
    {
        if (!_upgradeLogic.Triggered)
        {
            return UpdateSleepTime.Forever;
        }

        if (Context.CurrentFrame < _nextPulseFrame)
        {
            return UpdateSleepTime.Frames(_nextPulseFrame - Context.CurrentFrame);
        }

        Pulse();
        _nextPulseFrame = Context.CurrentFrame + _data.EmotionPulseInterval;

        return PulseCadence();
    }

    /// <summary>
    /// EmotionPulseInterval of zero (never authored in AotR) parks the module (Forever) rather
    /// than pulsing every frame - matching AttributeModifierAuraUpdate's identical zero-delay
    /// handling. Do not treat zero as "every frame"; nothing grounds that.
    /// </summary>
    private UpdateSleepTime PulseCadence()
    {
        return _data.EmotionPulseInterval.Value > 0
            ? UpdateSleepTime.Frames(_data.EmotionPulseInterval)
            : UpdateSleepTime.Forever;
    }

    /// <summary>
    /// One pulse: query the S3 partition seam within EmotionPulseRadius and select every
    /// live, non-self candidate that matches VictimFilter (a null filter accepts every live
    /// candidate - the blackrider authored shape). Replaces the prior unconsumed victim set
    /// wholesale.
    /// </summary>
    private void Pulse()
    {
        _pulseVictims.Clear();

        if (_data.EmotionPulseRadius <= Fix64.Zero)
        {
            return;
        }

        // Ascending-ObjectId order (the S3 seam's frozen contract, ISimContext.cs), so this
        // list's own order is deterministic given the same world state - never sorted post hoc.
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.EmotionPulseRadius))
        {
            if (candidate == GameObject || candidate.IsDestroyed || candidate.IsEffectivelyDead)
            {
                continue;
            }

            if (_data.VictimFilter != null && !_data.VictimFilter.Matches(candidate))
            {
                continue;
            }

            _pulseVictims.Add(candidate.Id);
        }
    }

    // ---- the single walk (declaration order = Xfer order, our own choice) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);                                                  // ch.1: Exact (shared mux)
        xfer.XferFrame("NextPulseFrame", ref _nextPulseFrame, Tolerance.Quantum);  // ch.2 timer
        xfer.XferList("PulseVictims", _pulseVictims, XferVictim);                  // identity fields: Exact
    }

    private static void XferVictim(IXfer xfer, ref ObjectId id) => xfer.XferObjectId("Victim", ref id);
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class RadiateFearUpdateModuleData : UpdateModuleData
{
    internal static RadiateFearUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<RadiateFearUpdateModuleData> FieldParseTable =
        new IniParseTableChild<RadiateFearUpdateModuleData, UpgradeLogicData>(x => x.UpgradeData, UpgradeLogicData.FieldParseTable)
        .Concat(new IniParseTable<RadiateFearUpdateModuleData>
        {
            // This module's own spelling of UpgradeLogicData.StartsActive.
            { "InitiallyActive", (parser, x) => x.UpgradeData.StartsActive = parser.ParseBoolean() },
            // Bare integer index with no enum/table/consumer anywhere in the engine - held, see
            // the file-header HELD section. Do not rename or map to a SpecialPowerTemplate.
            { "WhichSpecialPower", (parser, x) => x.WhichSpecialPower = parser.ParseInteger() },
            // held: no emotion-application primitive exists in the engine (file header).
            { "GenerateTerror", (parser, x) => x.GenerateTerror = parser.ParseBoolean() },
            // held: no emotion-application primitive exists in the engine (file header).
            { "GenerateFear", (parser, x) => x.GenerateFear = parser.ParseBoolean() },
            // Sim-affecting distance -> Fix64 (F1/S5), never float across the analyzer wall.
            { "EmotionPulseRadius", (parser, x) => x.EmotionPulseRadius = parser.ParseFix64() },
            // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary).
            { "EmotionPulseInterval", (parser, x) => x.EmotionPulseInterval = parser.ParseDurationLogicFrames() },
            { "VictimFilter", (parser, x) => x.VictimFilter = ObjectFilter.Parse(parser) },
            // held: no emotion-application primitive exists in the engine (file header).
            { "GenerateUncontrollableFear", (parser, x) => x.GenerateUncontrollableFear = parser.ParseBoolean() },
        });

    public UpgradeLogicData UpgradeData { get; } = new();

    /// <summary>Held: parsed, not modeled - see the file-header HELD section. Not currently
    /// consumed by any engine seam.</summary>
    public int WhichSpecialPower { get; private set; }

    /// <summary>Held: parsed, not modeled - see the file-header HELD section. Does not gate
    /// the pulse.</summary>
    public bool GenerateTerror { get; private set; }

    /// <summary>Held: parsed, not modeled - see the file-header HELD section. Does not gate
    /// the pulse.</summary>
    public bool GenerateFear { get; private set; }

    /// <summary>Pulse scan radius (quantized Q31.32). A radius of zero or less produces an
    /// empty victim set without querying.</summary>
    public Fix64 EmotionPulseRadius { get; private set; }

    /// <summary>Frames between pulses (ms in INI, ceil-quantized at parse, S5). Zero parks the
    /// module (Forever) rather than pulsing every frame.</summary>
    public LogicFrameSpan EmotionPulseInterval { get; private set; }

    /// <summary>KindOf-bit gate via <see cref="ObjectFilter.Matches"/>. Null accepts every live
    /// candidate (the blackrider authored shape, which authors no VictimFilter at all).
    /// Relationship-rule bits (Allies/Enemies/...) are parsed but not enforced by Matches as
    /// landed - engine-wide shared-file gap, not this module's to fix (file-header HELD
    /// section).</summary>
    public ObjectFilter VictimFilter { get; private set; }

    /// <summary>Held: parsed, not modeled - see the file-header HELD section. Does not gate
    /// the pulse.</summary>
    public bool GenerateUncontrollableFear { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RadiateFearUpdate(gameObject, gameEngine.SimContext, this);
    }
}
