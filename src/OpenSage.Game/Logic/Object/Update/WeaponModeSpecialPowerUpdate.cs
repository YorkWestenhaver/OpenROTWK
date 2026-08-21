// WeaponModeSpecialPowerUpdate - R13 port. Data-derivable from the module's own field list
// (no generals-gpl filled-in logic to translate - see spec §0): a special-power-gated, timed,
// reversible weapon-set switch. GPL siblings (WeaponSetUpgrade.cpp's permanent
// setWeaponSetFlag mechanism, DelayedWeaponSetUpgradeUpdate.cpp's unfilled stub) are
// structural-category confirmation only, not translation sources.
//
// Base class: BehaviorModule directly (matches this ModuleData's own BehaviorModuleData
// hierarchy, not UpdateModuleData/SpecialPowerModuleData - spec §0/§2). This module therefore
// re-implements its own ready/pause lifecycle (mirroring SpecialPowerModule's, Object/
// SpecialPower.cs) rather than inheriting it, and is not an UpdateModule: it has no
// UpdateOrder/sleepy-queue registration and does not use SetWakeFrame.
//
// FINDINGS (filed, not invented around - see spec §5 for the full writeups):
//   F-WMSP-1 (LockWeaponSlot has no engine hook): WeaponSet's slot selection
//     (WeaponSet.Update) has no override point for an external module to force a specific
//     WeaponSlot - the one prior module naming a lock field, LockWeaponCreate, is itself a
//     dead-field stub. This port tracks LockWeaponSlot and exposes it read-only via
//     LockedWeaponSlot for a future WeaponSet enhancement to consume; no WeaponSet.ForceSlot(...)
//     is added here (out of scope, api-freeze-v1 §6).
//   F-WMSP-2 (dispatch): special-power activation is dispatched by concrete module type in
//     this engine (SpecialPowerApplicator's switch over SpecialPowerType), and no applicator
//     case names this module today. Activate() is a driven method with no landed caller yet -
//     same posture as every other Round-4 driven-trigger seam. Wiring the applicator case is a
//     separate, broader piece of work, not part of this task.
//   F-WMSP-3 (no SharedSyncedTimer-equivalent re-arm branch): SpecialPowerModule.
//     ResetCountdown's shared-synced-timer special case is not ported because this ModuleData
//     has no field naming that behavior.
//   F-WMSP-4 (Duration revert has no automatic per-frame caller): this module is not an
//     UpdateModule, so Activate()'s computed revert frame is only enforced when something
//     calls the driven CheckRevert() method. No such automatic caller exists today (see the
//     contract tests' case 5b, which demonstrates this as an observable fact, not just prose).
//     Resolving this (a paired UpdateModule poller, or promoting this ModuleData's own
//     hierarchy) is a framework-adjacent decision out of this task's scope.

using System;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class WeaponModeSpecialPowerUpdate : BehaviorModule
{
    private readonly WeaponModeSpecialPowerUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer, §2) ----

    /// <summary>Mirrors SpecialPowerModule's own StartsPaused-derived pause state - this
    /// module cannot inherit SpecialPowerModule's (spec §0), so it re-implements it.</summary>
    private bool _paused;

    /// <summary>The next frame this module's Activate() may succeed (own reload-cooldown
    /// gate, mirroring SpecialPowerModule.ReadyProgress's comparison).</summary>
    private LogicFrame _availableAtFrame;

    /// <summary>The frame the current Active-phase effects revert on, sentinel
    /// LogicFrame.MaxValue when not Active (spec §2's Xfer table).</summary>
    private LogicFrame _revertFrame = LogicFrame.MaxValue;

    /// <summary>Whether the WeaponSetFlags/AttributeModifier grant from the last Activate()
    /// is currently applied - needed so CheckRevert/a future Activate() know the grant state
    /// without re-deriving it from _revertFrame alone (guards a save/load exactly at
    /// _revertFrame, spec §2).</summary>
    private bool _active;

    public WeaponModeSpecialPowerUpdate(GameObject gameObject, ISimContext context, WeaponModeSpecialPowerUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // Ctor: capture now, map StartsPaused verbatim (identical field-to-state mapping as
        // SpecialPowerModule's ctor). Ready immediately when not paused - no separate
        // reload-tracking timer beyond SpecialPowerTemplate.Value.ReloadTime (spec §1). No
        // SetWakeFrame call here: this module has no continuously-evolving per-frame state,
        // it only needs to wake once, on activation, to schedule the revert.
        var now = Context.CurrentFrame;
        _paused = data.StartsPaused;
        _availableAtFrame = now;
    }

    /// <summary>F-WMSP-1: tracked+exposed, unconsumed by anything landed today - no
    /// WeaponSet override point exists to force this slot.</summary>
    public WeaponSlot? LockedWeaponSlot => Context.CurrentFrame < _revertFrame ? _data.LockWeaponSlot : (WeaponSlot?)null;

    /// <summary>Mirrors SpecialPowerModule.Unpause (Object/SpecialPower.cs) - the wiring
    /// precedent this module's own field list cites, though wiring an actual caller (e.g. an
    /// UnpauseSpecialPowerUpgrade-equivalent) is F-WMSP-2, out of this task's scope.</summary>
    public void Unpause()
    {
        _paused = false;
        _availableAtFrame = Context.CurrentFrame;
    }

    /// <summary>
    /// Driven activation (F-WMSP-2: no landed caller today). Mirrors SpecialPowerModule.
    /// Activate's gate/output shape: no-op while paused or off cooldown; on success, fires
    /// InitiateSound, raises every WeaponSetFlags bit, grants AttributeModifier, schedules the
    /// Duration revert, and re-arms the cooldown from SpecialPowerTemplate.Value.ReloadTime
    /// (spec §1; F-WMSP-3: no SharedSyncedTimer branch, this ModuleData has no such field).
    /// </summary>
    public bool Activate()
    {
        var now = Context.CurrentFrame;
        if (_paused || now < _availableAtFrame)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(_data.InitiateSound))
        {
            Context.Events.FireAudioEventAtObject(_data.InitiateSound, GameObject.Id);
        }

        foreach (var flag in Enum.GetValues<WeaponSetConditions>())
        {
            if (flag != WeaponSetConditions.None && (_data.WeaponSetFlags & flag) == flag)
            {
                GameObject.SetWeaponSetCondition(flag, true);
            }
        }

        var modifierList = _data.AttributeModifier?.Value;
        if (modifierList != null)
        {
            GameObject.AddAttributeModifier(modifierList.Name, new Logic.AttributeModifier(modifierList));
        }

        _active = true;
        _revertFrame = now + _data.Duration;

        // Re-arm the cooldown the same way SpecialPowerModule.ResetCountdown does, minus its
        // SharedSyncedTimer branch (F-WMSP-3: no such field on this ModuleData).
        _availableAtFrame = now + _data.SpecialPowerTemplate.Value.ReloadTime;

        return true;
    }

    /// <summary>
    /// F-WMSP-4: driven revert check - must be polled by something each frame for the
    /// Duration revert to actually fire on time (no automatic caller lands with this task).
    /// Symmetric with Activate(): clears exactly the WeaponSetFlags bits this module itself
    /// raised (never touching flags some other module may have set) and removes the granted
    /// AttributeModifier, once, the first CheckRevert() call at or after _revertFrame.
    /// </summary>
    public void CheckRevert()
    {
        if (!_active || Context.CurrentFrame < _revertFrame)
        {
            return;
        }

        foreach (var flag in Enum.GetValues<WeaponSetConditions>())
        {
            if (flag != WeaponSetConditions.None && (_data.WeaponSetFlags & flag) == flag)
            {
                GameObject.SetWeaponSetCondition(flag, false);
            }
        }

        var modifierList = _data.AttributeModifier?.Value;
        if (modifierList != null)
        {
            GameObject.RemoveAttributeModifier(modifierList.Name);
        }

        _active = false;
        _revertFrame = LogicFrame.MaxValue;
    }

    // ---- the single walk (§2): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Paused", ref _paused);
        xfer.XferFrame("AvailableAtFrame", ref _availableAtFrame, Tolerance.Quantum);
        xfer.XferFrame("RevertFrame", ref _revertFrame, Tolerance.Quantum);
        xfer.XferBool("Active", ref _active);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// A special-power-gated, timed, reversible weapon-set switch: on activation, raises
/// WeaponSetFlags on the owning object (so the best-fit weapon-set chooser picks a different
/// WeaponTemplateSet), grants an AttributeModifier, and locks weapon selection to
/// LockWeaponSlot (see F-WMSP-1) - all reverted symmetrically once Duration elapses.
/// </summary>
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class WeaponModeSpecialPowerUpdateModuleData : BehaviorModuleData
{
    internal static WeaponModeSpecialPowerUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<WeaponModeSpecialPowerUpdateModuleData> FieldParseTable = new IniParseTable<WeaponModeSpecialPowerUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseSpecialPowerReference() },
        { "AttributeModifier", (parser, x) => x.AttributeModifier = parser.ParseModifierListReference() },
        { "Duration", (parser, x) => x.Duration = parser.ParseDurationLogicFrames() },
        { "LockWeaponSlot", (parser, x) => x.LockWeaponSlot = parser.ParseEnum<WeaponSlot>() },
        { "WeaponSetFlags", (parser, x) => x.WeaponSetFlags = parser.ParseEnumFlags<WeaponSetConditions>() },
        { "StartsPaused", (parser, x) => x.StartsPaused = parser.ParseBoolean() },
        { "InitiateSound", (parser, x) => x.InitiateSound = parser.ParseAssetReference() }
    };

    /// <summary>Which SpecialPower asset this module answers to; its own Type/ReloadTime/
    /// RequiredSciences govern this ability's cast-eligibility (spec §1).</summary>
    public LazyAssetReference<SpecialPower> SpecialPowerTemplate { get; private set; }

    /// <summary>How long the whole activated state (flags + modifier + lock) persists before
    /// automatically reverting (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan Duration { get; private set; }

    /// <summary>Stat buff/debuff registered for the Duration (grant-registry shape identical
    /// to AttributeModifierUpgradeModuleData.AttributeModifier).</summary>
    public LazyAssetReference<ModifierList> AttributeModifier { get; private set; }

    /// <summary>F-WMSP-1: parsed and tracked, no engine hook exists to actually force weapon
    /// selection to this slot.</summary>
    public WeaponSlot LockWeaponSlot { get; private set; }

    /// <summary>WeaponSetConditions bits to raise on the owning object for the Duration (a
    /// combined flags value, not a BitArray - matches this field's pre-existing shape).</summary>
    public WeaponSetConditions WeaponSetFlags { get; private set; }

    /// <summary>Same meaning as SpecialPowerModuleData.StartsPaused - not ready to fire until
    /// something calls this module's own Unpause() (spec §0: re-declared, not inherited).</summary>
    public bool StartsPaused { get; private set; }

    /// <summary>One-shot AudioEvent name fired at activation.</summary>
    public string InitiateSound { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
        => new WeaponModeSpecialPowerUpdate(gameObject, gameEngine.SimContext, this);
}
