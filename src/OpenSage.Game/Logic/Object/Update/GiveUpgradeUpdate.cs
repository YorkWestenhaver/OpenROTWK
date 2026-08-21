// GiveUpgradeUpdate - R13 port (spec: bfme2-workbench/research/modules-r13/specs/
// GiveUpgradeUpdateModuleData.md).
//
// Behavioral reference: generals-gpl carries no GiveUpgradeUpdate at all (this is a
// BFME2-only class); the closest GPL relative is the generic SpecialAbilityUpdate.cpp base,
// whose PackingState machine (NONE -> PACKING -> UNPACKING -> PACKED -> UNPACKED) and
// prep/persistence fields (m_prepFrames, isPersistentAbility/resetPreparation) are the shape
// this port's state machine below is modeled after; this repo's own in-tree translation of
// exactly that timer subset, ToggleHiddenSpecialAbilityUpdate, is the direct template this
// file follows field-for-field for the parts the two classes share.
//
// STATE MACHINE: Packed -> Unpacking (UnpackTime) -> Prepared (PreparationTime, extended once
// by PersistentPrepTime if unused) -> Packing (PackTime) -> Packed. Unlike
// ToggleHiddenSpecialAbilityUpdate there is no Active phase and no Trigger() seam: this class
// has no EffectDuration field, and the one thing that would fire on completion (the upgrade
// delivery) is a held gap (see below) - the Prepared window always ends by lapsing straight
// into Packing. Zero duration on any timed stage skips it immediately (the same "zero means
// immediate" convention ToggleHiddenSpecialAbilityUpdate uses).
//
// InitiateIntentToDoSpecialPower(templateName, triggeringObject) is the seam that starts the
// Packed -> Unpacking sequence: only this module's own SpecialPowerTemplate may fire it, only
// from Packed, and only when triggeringObject is within StartAbilityRange (gate skipped when
// unconfigured or the triggering object is unknown) - same idiom, same Fix64-partition-query
// mechanism as ToggleHiddenSpecialAbilityUpdate's identical field. Driven input (no landed
// special-power/command system calls this yet), same posture as that sibling; the AotR-side
// driver is the paired Behavior = SpecialPowerModule block on every corpus placement.
//
// PARSED, NOT MODELED (audited gaps, not invented):
//   - DeliverUpgrade: the actual upgrade transfer. The class carries no upgrade-name field at
//     all, so which upgrade(s) transfer, to whom, and whether the carrier self-destructs are
//     all underivable from the data; GPL's SpecialAbilityUpdate has no upgrade vocabulary
//     anywhere either. Parsed and held, and exposed read-only (<see cref="DeliversUpgrade"/>)
//     for the future upgrade-transfer task to wire at the documented Prepared -> Packing
//     hand-off without re-touching this state machine.
//   - ApproachRequiresLOS: GPL parses the identical field but consumes it inside
//     approach-AI/targeting machinery this port does not translate. IPartitionQuery offers
//     range queries only - no line-of-sight/facing predicate exists on the frozen
//     ISimContext.
//   - SpawnOutFX: client presentation (S8). An FXList asset ref; ISimContext is deliberately,
//     permanently rendering/UI-absent, so no sim-side FX-spawn seam exists to call.
//   - FadeOutSpeed: client presentation (S8). A per-frame visual fade rate with no sim
//     consumer; kept float (never converted to Fix64) precisely because no sim code reads it.
//
// Every mutable sim field appears in Xfer exactly once (§3 of the spec); tolerances are the
// field's conformance class at its declaration site (§4).

using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;
using System.Linq;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class GiveUpgradeUpdate : UpdateModule
{
    private readonly GiveUpgradeUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private GiveUpgradePhase _phase;
    private LogicFrame _phaseEndFrame;

    /// <summary>
    /// Whether the one-shot PersistentPrepTime extension has already been consumed for the
    /// current Prepared window.
    /// </summary>
    private bool _prepExtended;

    /// <summary>
    /// Who initiated the current cycle; captured at initiate, cleared at Packed. Held for the
    /// future upgrade-transfer task - this port never resolves it, so no GetObjectById call is
    /// written here.
    /// </summary>
    private ObjectId _triggeringObjectId;

    public GiveUpgradeUpdate(GameObject gameObject, ISimContext context, GiveUpgradeUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _phase = GiveUpgradePhase.Packed;
        _triggeringObjectId = ObjectId.Invalid;

        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// Parsed and held; not currently modeled - the actual upgrade transfer is an ungrounded
    /// gap (see the file-header note). Exposed for the future upgrade-transfer task.
    /// </summary>
    public bool DeliversUpgrade => _data.DeliverUpgrade;

    /// <summary>
    /// Starts the Packed -> Unpacking -> Prepared sequence. Only this module's own special
    /// power (matched by template name) may fire it, only while Packed (no interrupting or
    /// re-triggering an in-flight cycle), and only when <paramref name="triggeringObject"/> is
    /// within <see cref="GiveUpgradeUpdateModuleData.StartAbilityRange"/> (gate skipped when
    /// unconfigured or the triggering object is unknown).
    /// </summary>
    public bool InitiateIntentToDoSpecialPower(string specialPowerTemplateName, GameObject triggeringObject)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        if (_phase != GiveUpgradePhase.Packed)
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

        _prepExtended = false;
        _triggeringObjectId = triggeringObject?.Id ?? ObjectId.Invalid;

        EnterUnpackingOrLater();
        return true;
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        switch (_phase)
        {
            case GiveUpgradePhase.Unpacking:
                if (now >= _phaseEndFrame)
                {
                    EnterPreparedOrLater();
                }
                break;

            case GiveUpgradePhase.Prepared:
                if (now >= _phaseEndFrame)
                {
                    if (!_prepExtended && _data.PersistentPrepTime.Value > 0)
                    {
                        _prepExtended = true;
                        _phaseEndFrame = now + _data.PersistentPrepTime;
                    }
                    else
                    {
                        // The window closed: skip straight to packing (no Active phase, no
                        // Trigger seam on this class - see the file-header note).
                        EnterPackingOrLater();
                    }
                }
                break;

            case GiveUpgradePhase.Packing:
                if (now >= _phaseEndFrame)
                {
                    GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
                    _phase = GiveUpgradePhase.Packed;
                    _triggeringObjectId = ObjectId.Invalid;
                }
                break;
        }

        return UpdateSleepTime.None;
    }

    private void EnterUnpackingOrLater()
    {
        if (_data.UnpackTime.Value > 0)
        {
            _phase = GiveUpgradePhase.Unpacking;
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
            _phase = GiveUpgradePhase.Prepared;
            _phaseEndFrame = Context.CurrentFrame + _data.PreparationTime;
            _prepExtended = false;
        }
        else
        {
            EnterPackingOrLater();
        }
    }

    private void EnterPackingOrLater()
    {
        if (_data.PackTime.Value > 0)
        {
            _phase = GiveUpgradePhase.Packing;
            _phaseEndFrame = Context.CurrentFrame + _data.PackTime;
            GameObject.SetModelConditionState(ModelConditionFlag.Packing);
        }
        else
        {
            GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
            _phase = GiveUpgradePhase.Packed;
            _triggeringObjectId = ObjectId.Invalid;
        }
    }

    private enum GiveUpgradePhase
    {
        Packed,
        Unpacking,
        Prepared,
        Packing,
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Tolerances (ruling A3): the phase enum, the extension flag, and the triggering object id
    // are lifecycle facts, so Exact. The phase-end frame is a timer, so Quantum (ch.2),
    // matching XferFrame's own default.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("PhaseEndFrame", ref _phaseEndFrame);
        xfer.XferBool("PrepExtended", ref _prepExtended);
        xfer.XferObjectId("TriggeringObject", ref _triggeringObjectId);
    }
}
