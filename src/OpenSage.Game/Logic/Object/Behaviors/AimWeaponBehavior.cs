// AimWeaponBehavior - R15 port of the AIM_NEAR half only (spec:
// bfme2-workbench/research/modules-r13/specs/AimWeaponBehaviorModuleData.md, classification
// SPLIT). The parse side lives in its own file (AimWeaponBehaviorModuleData.cs) because
// SimCoreScope analyses a WHOLE file once it declares any [SimState] type and SIMCORE001 bans
// float there, while the two HELD threshold fields must stay float - same split, same reason,
// as GiveUpgradeUpdateModuleData.cs.
//
// No GPL sibling exists (grep across generals-gpl/generals-community for "aimweapon",
// "AIM_HIGH", "AIM_LOW", "AIM_NEAR", "AimNearDistance" is empty; BFME was never GPL'd). This
// is fresh code composed from two landed primitives: the radius-membership victim scan of
// DualWeaponBehavior, and the rising/falling-edge model-condition write of EnemyNearUpdate.
// Behavior facts used (spec §0-§1):
//   - state is exactly { aimNear }, this module's own edge-detection memory of the AIM_NEAR
//     model-condition bit it last wrote.
//   - the module is unconditionally active from spawn: the AotR census (3047 .ini files, 61
//     live AimWeaponBehavior blocks) shows ZERO instances authoring TriggeredBy/StartsActive,
//     so an upgrade-gated reading would leave every shipped instance permanently dead. The
//     runtime class therefore does not implement IUpgradeableModule; the inherited
//     UpgradeModuleData fields parse but are inert (F-AWB-3).
//   - Update() runs every frame (UPDATE_SLEEP_NONE): with no victim, or a victim outside
//     AimNearDistance, AIM_NEAR is clear; with the victim inside the landed partition seam's
//     strict-< in-range predicate, it is set. Writes are transition-only.
//   - a degenerate AimNearDistance <= 0 (the default, and the shape of 56 of the 61 shipped
//     instances, which author only the held High/Low pair) never sets AIM_NEAR and skips the
//     per-frame partition query entirely (F-AWB-4). This is the MAJORITY shape of the corpus,
//     not a corner case.
//
// FINDINGS (held fields and behavior-fact gaps, filed not invented - see the spec's §5):
//   F-AWB-1/F-AWB-2 AimHighThreshold / AimLowThreshold -> AIM_HIGH / AIM_LOW: HELD. No member
//     on the frozen ISimContext surface returns another object's position or a relative
//     height/pitch, so there is nothing to compute the comparison from without a framework
//     change (out of scope, api-freeze-v1 §6). GPL's superficially similar
//     Weapon::isWithinTargetPitch / GeometryInfo::calcPitches does NOT ground them (F-AWB-6):
//     different mechanism (fire-eligibility gate vs. continuous pose driver), different parse
//     type (ParseInteger degrees vs. ParseFloat), and the shipped data's uniform +/-0.15
//     constant across radically different object geometries argues against a
//     geometry-dependent radians reading. This module is SILENT on ModelConditionFlag.AimHigh
//     and .AimLow - it never sets them and never clears them, so it cannot race a future
//     owner of those flags. Test case HeldFields_AimHighLowThreshold_NeverSetOrClear is the
//     tripwire that fails the day someone implements a guessed reading.
//   F-AWB-5 the output mechanism is a ModelConditionFlag, NOT a WeaponSetConditions bit:
//     AIM_NEAR is ModelConditionFlag.AimNear, so the write call is
//     GameObject.SetModelConditionState / ClearModelConditionState (as EnemyNearUpdate uses),
//     not DualWeaponBehavior's SetWeaponSetCondition. Porting this module by analogy to
//     DualWeaponBehavior alone would reach for the wrong call.
//   weapon.ini's IsAimingWeapon (and ObjectStatus.IsAimingWeapon) is a property of the WEAPON
//     TEMPLATE, not of this module's field set; the two systems are data-linked by author
//     intent, not by any code dependency. Not modeled here (spec §0.3).
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AimWeaponBehavior : UpdateModule
{
    private readonly AimWeaponBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>The AIM_NEAR bit as this module last wrote it (edge-detection memory; the
    /// ModelConditionFlags bitset itself is client-side presentation state with its own
    /// lifetime, not a second copy of this field).</summary>
    private bool _aimNear;

    /// <summary>The frozen ported-module ctor (api-freeze-v1 §3 item 2). No ctor RNG draw:
    /// there is no ScanDelayTime-equivalent to stagger, so the logic RNG stream is untouched
    /// (relevant to CRC review).</summary>
    public AimWeaponBehavior(GameObject gameObject, ISimContext context, AimWeaponBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;
    }

    // UPDATE_SLEEP_NONE, no UpdateOrder override: this module must re-evaluate whenever either
    // the victim or either object's position changes and has no cadence field to gate a scan
    // on, and the default Order2 sorts after AIUpdate's Order0 within a frame so
    // CurrentVictimId is always same-frame-fresh here.
    public override UpdateSleepTime Update()
    {
        if (_data.AimNearDistance <= Fix64.Zero)
        {
            // F-AWB-4 degenerate guard: no authored distance (or an authored non-positive one)
            // means "never AIM_NEAR" and skips the per-frame partition query. This is the
            // majority shape of the shipped corpus, not an edge case.
            SetAimNear(false);
            return UpdateSleepTime.None;
        }

        var victimId = GameObject.AIUpdate?.CurrentVictimId ?? ObjectId.Invalid;
        if (!victimId.IsValid)
        {
            SetAimNear(false);
            return UpdateSleepTime.None;
        }

        var inRange = false;
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.AimNearDistance))
        {
            if (candidate.Id == victimId)
            {
                inRange = true;
                break;
            }
        }

        SetAimNear(inRange);
        return UpdateSleepTime.None;
    }

    private void SetAimNear(bool value)
    {
        if (value == _aimNear)
        {
            // Transition-only write, matching the EnemyNearUpdate rising/falling-edge shape.
            // Unlike DualWeaponBehavior's weapon-set re-resolve there is no allocation-based
            // observable for "no redundant write" here (ModelConditionFlags.Set is a plain
            // BitArray write), so this guard is verified by review, not by a dedicated test.
            return;
        }

        _aimNear = value;

        if (value)
        {
            GameObject.SetModelConditionState(ModelConditionFlag.AimNear);
        }
        else
        {
            GameObject.ClearModelConditionState(ModelConditionFlag.AimNear);
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Load-side subtlety: unlike DualWeaponBehavior's WeaponSetConditions (which GameObject
    // itself persists and restores before this module's Xfer runs), ModelConditionFlags are a
    // client-side presentation output and are not folded into the sim CRC (EnemyNearUpdate's
    // header says the same). Restoring _aimNear therefore does NOT need to re-assert the flag
    // for CRC correctness, and the next Update() re-derives it from live victim/geometry state
    // within one frame anyway - so no special-case load logic beyond the bare bool.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("AimNear", ref _aimNear); // XferBool is always exact (A3)
    }
}
