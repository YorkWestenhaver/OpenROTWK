// ToggleHiddenSpecialAbilityUpdate - R12 port (api-freeze-v1 §6 / template v1.1).
//
// Behavioral reference: generals-gpl carries no ToggleHiddenSpecialAbilityUpdate at all (grep
// confirms - it is a BFME2-only class); the closest GPL relative is the generic
// SpecialAbilityUpdate.cpp base (Generals/GeneralsMD), whose PackingState machine (NONE ->
// PACKING -> UNPACKING -> PACKED -> UNPACKED) and prep/trigger/persistence fields
// (m_prepFrames, isPersistentAbility/resetPreparation, m_packingState) are the shape this
// port's own state machine below is modeled after. The base class's targeting, special-object
// spawning, facing, approach-AI, capture-FX and LOS machinery are NOT ported: this module's
// own INI vocabulary (SpecialPowerTemplate/UnpackingVariation/StartAbilityRange/UnpackTime/
// PreparationTime/PersistentPrepTime/PackTime/AwardXPForTriggering/EffectDuration/
// ShowPalantirTimer) carries none of those fields, so none of that machinery is invented here
// - this is fresh code against the frozen contract (same posture as ReplaceObjectUpdate and
// MissileLauncherBuildingUpdate, the two nearest-in-time R12 siblings this file's shape
// otherwise follows almost field-for-field).
//
// STATE MACHINE (engineering choice, not a GPL translation - see above): Packed -> Unpacking
// (UnpackTime) -> Prepared (PreparationTime, extended once by PersistentPrepTime if the
// ability goes unused - see PersistentPrepTime below) -> [manual Trigger] -> Active
// (EffectDuration, GameObject held ObjectStatus.Stealthed) -> Packing (PackTime) -> Packed.
// A Prepared window that times out without a Trigger call skips Active entirely and packs
// straight from Prepared (the task packet's "auto-packs" cycle - no effect, no XP). Zero
// duration on any timed stage skips it immediately (the ordinary SAGE "zero means immediate"
// timer convention this update-module family already uses - see ReplaceObjectUpdate's own
// PreparationTime/UnpackTime zero-skip).
//
// InitiateIntentToDoSpecialPower(templateName, triggeringObject) is the seam that starts the
// Packed -> Unpacking sequence: only this module's own SpecialPowerTemplate may fire it, only
// from Packed, and (mirroring ReplaceObjectUpdate's StartAbilityRange gate exactly - same
// field, same idiom, same Fix64-partition-query mechanism) only when triggeringObject is
// within StartAbilityRange. Driven input (no landed special-power/command system calls this
// yet), same posture as MissileLauncherBuildingUpdate's own trigger seam and ReplaceObjectUpdate's
// InitiateIntentToDoSpecialPower.
//
// Trigger(triggeringObject) is the separate manual "fire now" seam, callable only while
// Prepared: it awards AwardXPForTriggering to triggeringObject's own ExperienceTracker (the
// landed veterancy surface every other XP-granting path in this codebase uses - same as
// ReplaceObjectUpdate), sets this object's own ObjectStatus.Stealthed for the "hidden" effect
// (the field this module's own name promises, and the one status bit the engine actually
// carries for it - GameObject.SetObjectStatus / ObjectStatus.Stealthed), and starts the
// EffectDuration timer. Also driven input, same posture as InitiateIntentToDoSpecialPower
// above and every other trigger seam this R12 batch ports (no landed special-power/command
// system exists yet to call either automatically).
//
// PersistentPrepTime: the task packet's own testCases name it as "prepared state extends by
// PersistentPrepTime if ability unused" - this port's literal reading of that sentence (a
// single one-shot extension of the Prepared window, tracked by _prepExtended so it applies at
// most once per Packed->Prepared cycle). This differs from GPL SpecialAbilityUpdate's own
// unrelated m_persistentPrepFrames semantics (a repeating retrigger loop after every
// triggerAbilityEffect call) - since no clean-room spec or GPL sibling exists for THIS class,
// the packet's own plain-English testCase description is the authority here, not GPL's
// differently-shaped field of the same name.
//
// PARSED, NOT MODELED (audited gaps, not invented):
//   - UnpackingVariation: no spec states which of the PACKING_TYPE_1..6 model-condition
//     variants (or animation index) it selects; parked exactly like ReplaceObjectUpdate's own
//     identically-named field. This module drives the plain Packing/Unpacking flags only.
//   - ShowPalantirTimer: ISimContext is deliberately, permanently UI-absent (its own header:
//     "Deliberately absent, forever: audio, rendering, UI, ..."), so no event exists to show or
//     hide a countdown timer. The flag is parsed and held, and exposed read-only
//     (<see cref="ShowsPalantirTimer"/>) for whatever UI layer eventually polls per-object
//     ability state - the same "parsed and held; not currently modeled" posture as
//     MissileLauncherBuildingUpdate's DoorOpenIdleAudio.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using System.Linq;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ToggleHiddenSpecialAbilityUpdate : UpdateModule
{
    private readonly ToggleHiddenSpecialAbilityUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private ToggleHiddenPhase _phase;
    private LogicFrame _phaseEndFrame;

    /// <summary>
    /// Whether the one-shot PersistentPrepTime extension has already been consumed for the
    /// current Prepared window (see the PersistentPrepTime note at the top of this file).
    /// </summary>
    private bool _prepExtended;

    public ToggleHiddenSpecialAbilityUpdate(GameObject gameObject, ISimContext context, ToggleHiddenSpecialAbilityUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _phase = ToggleHiddenPhase.Packed;

        // Ticks every frame like the rest of this SpecialPowerTemplate-gated family
        // (MissileLauncherBuildingUpdate, ReplaceObjectUpdate): the phase machine is cheap and
        // this keeps the wake-scheduling shape identical to those landed exemplars.
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>Parsed and held; not currently modeled - see the file-header UI-absent note.</summary>
    public bool ShowsPalantirTimer => _data.ShowPalantirTimer;

    /// <summary>
    /// Starts the Packed -> Unpacking -> Prepared sequence. Only this module's own special
    /// power (matched by template name) may fire it, only while Packed (no interrupting or
    /// re-triggering an in-flight cycle), and only when <paramref name="triggeringObject"/> is
    /// within <see cref="ToggleHiddenSpecialAbilityUpdateModuleData.StartAbilityRange"/> (gate
    /// skipped when unconfigured or the triggering object is unknown - same shape as
    /// ReplaceObjectUpdate's identical field).
    /// </summary>
    public bool InitiateIntentToDoSpecialPower(string specialPowerTemplateName, GameObject triggeringObject)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        if (_phase != ToggleHiddenPhase.Packed)
        {
            return false;
        }

        if (_data.StartAbilityRange > Fix64.Zero && triggeringObject != null)
        {
            var inRange = Context.Partition
                .QueryObjectsInRadius(GameObject, _data.StartAbilityRange)
                .Contains(triggeringObject);

            if (!inRange)
            {
                return false;
            }
        }

        EnterUnpackingOrLater();
        return true;
    }

    /// <summary>
    /// Manually fires the ability's effect while Prepared: awards
    /// <see cref="ToggleHiddenSpecialAbilityUpdateModuleData.AwardXPForTriggering"/> to
    /// <paramref name="triggeringObject"/>, hides this object for
    /// <see cref="ToggleHiddenSpecialAbilityUpdateModuleData.EffectDuration"/> frames, and
    /// begins the pack-down sequence. False (no-op) outside the Prepared phase.
    /// </summary>
    public bool Trigger(GameObject triggeringObject)
    {
        if (_phase != ToggleHiddenPhase.Prepared)
        {
            return false;
        }

        if (_data.AwardXPForTriggering != 0 && triggeringObject != null)
        {
            triggeringObject.ExperienceTracker.AddExperiencePoints(_data.AwardXPForTriggering);
        }

        GameObject.SetObjectStatus(ObjectStatus.Stealthed, true);

        EnterActiveOrLater();
        return true;
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        switch (_phase)
        {
            case ToggleHiddenPhase.Unpacking:
                if (now >= _phaseEndFrame)
                {
                    EnterPreparedOrLater();
                }
                break;

            case ToggleHiddenPhase.Prepared:
                if (now >= _phaseEndFrame)
                {
                    if (!_prepExtended && _data.PersistentPrepTime.Value > 0)
                    {
                        _prepExtended = true;
                        _phaseEndFrame = now + _data.PersistentPrepTime;
                    }
                    else
                    {
                        // The window closed with no Trigger call: skip Active entirely, no
                        // effect, no XP (the packet's own "auto-packs" cycle).
                        EnterPackingOrLater();
                    }
                }
                break;

            case ToggleHiddenPhase.Active:
                if (now >= _phaseEndFrame)
                {
                    GameObject.SetObjectStatus(ObjectStatus.Stealthed, false);
                    EnterPackingOrLater();
                }
                break;

            case ToggleHiddenPhase.Packing:
                if (now >= _phaseEndFrame)
                {
                    GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
                    _phase = ToggleHiddenPhase.Packed;
                }
                break;
        }

        return UpdateSleepTime.None;
    }

    private void EnterUnpackingOrLater()
    {
        if (_data.UnpackTime.Value > 0)
        {
            _phase = ToggleHiddenPhase.Unpacking;
            _phaseEndFrame = Context.CurrentFrame + _data.UnpackTime;
            GameObject.SetModelConditionState(ModelConditionFlag.Unpacking);
        }
        else
        {
            EnterPreparedOrLater();
        }
    }

    private void EnterPreparedOrLater()
    {
        GameObject.ClearModelConditionState(ModelConditionFlag.Unpacking);

        if (_data.PreparationTime.Value > 0)
        {
            _phase = ToggleHiddenPhase.Prepared;
            _phaseEndFrame = Context.CurrentFrame + _data.PreparationTime;
            _prepExtended = false;
        }
        else
        {
            // Nothing to prepare, so there is no window in which Trigger() could ever be
            // called: skip straight to packing, matching the family's zero-duration convention.
            EnterPackingOrLater();
        }
    }

    private void EnterActiveOrLater()
    {
        if (_data.EffectDuration.Value > 0)
        {
            _phase = ToggleHiddenPhase.Active;
            _phaseEndFrame = Context.CurrentFrame + _data.EffectDuration;
        }
        else
        {
            GameObject.SetObjectStatus(ObjectStatus.Stealthed, false);
            EnterPackingOrLater();
        }
    }

    private void EnterPackingOrLater()
    {
        if (_data.PackTime.Value > 0)
        {
            _phase = ToggleHiddenPhase.Packing;
            _phaseEndFrame = Context.CurrentFrame + _data.PackTime;
            GameObject.SetModelConditionState(ModelConditionFlag.Packing);
        }
        else
        {
            GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
            _phase = ToggleHiddenPhase.Packed;
        }
    }

    private enum ToggleHiddenPhase
    {
        Packed,
        Unpacking,
        Prepared,
        Active,
        Packing,
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Tolerances (ruling A3): the phase enum and the extension flag are lifecycle facts, so
    // Exact. The phase-end frame is a timer, so Quantum (ch.2), matching XferFrame's own
    // default.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("PhaseEndFrame", ref _phaseEndFrame);
        xfer.XferBool("PrepExtended", ref _prepExtended);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class ToggleHiddenSpecialAbilityUpdateModuleData : UpdateModuleData
{
    internal static ToggleHiddenSpecialAbilityUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ToggleHiddenSpecialAbilityUpdateModuleData> FieldParseTable = new IniParseTable<ToggleHiddenSpecialAbilityUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        { "UnpackingVariation", (parser, x) => x.UnpackingVariation = parser.ParseInteger() },
        { "StartAbilityRange", (parser, x) => x.StartAbilityRange = parser.ParseFix64() },
        { "UnpackTime", (parser, x) => x.UnpackTime = parser.ParseDurationLogicFrames() },
        { "PreparationTime", (parser, x) => x.PreparationTime = parser.ParseDurationLogicFrames() },
        { "PersistentPrepTime", (parser, x) => x.PersistentPrepTime = parser.ParseDurationLogicFrames() },
        { "PackTime", (parser, x) => x.PackTime = parser.ParseDurationLogicFrames() },
        { "AwardXPForTriggering", (parser, x) => x.AwardXPForTriggering = parser.ParseInteger() },
        { "EffectDuration", (parser, x) => x.EffectDuration = parser.ParseDurationLogicFrames() },
        { "ShowPalantirTimer", (parser, x) => x.ShowPalantirTimer = parser.ParseBoolean() },
    };

    public string SpecialPowerTemplate { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public int UnpackingVariation { get; private set; }

    public Fix64 StartAbilityRange { get; private set; }
    public LogicFrameSpan UnpackTime { get; private set; }
    public LogicFrameSpan PreparationTime { get; private set; }
    public LogicFrameSpan PersistentPrepTime { get; private set; }
    public LogicFrameSpan PackTime { get; private set; }
    public int AwardXPForTriggering { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public LogicFrameSpan EffectDuration { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header UI-absent note.</summary>
    [AddedIn(SageGame.Bfme2)]
    public bool ShowPalantirTimer { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ToggleHiddenSpecialAbilityUpdate(gameObject, gameEngine.SimContext, this);
    }
}
