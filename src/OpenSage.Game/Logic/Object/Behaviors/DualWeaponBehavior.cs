// DualWeaponBehavior - R13 port through the full task packet (api-freeze-v1 §6 / template
// v1.1). Spec: bfme2-workbench/research/modules-r13/specs/DualWeaponBehaviorModuleData.md.
//
// No GPL sibling exists (grep across generals-gpl/generals-community for "DualWeapon" is
// empty); this is fresh code composed from two landed primitives: the "module raises a
// WeaponSetConditions bit and the weapon-set lookup re-resolves" mechanism already landed as
// GameObject.SetWeaponSetCondition, and the radius-membership scan idiom of
// EnemyNearUpdate/SpecialEnemySenseUpdate. Behavior facts used (spec §0-§1):
//   - state is exactly { closeRange }, the module's own edge-detection memory of the
//     CLOSE_RANGE bit it last set - not a second copy of GameObject.WeaponSetConditions
//     itself, which already has its own persist walk (GameObject.cs).
//   - the module is unconditionally active from spawn: the AotR data census (191 files) shows
//     zero DualWeaponBehavior instances authoring TriggeredBy/StartsActive, so an
//     upgrade-gated reading would leave every shipped instance permanently dead. The runtime
//     class therefore does not implement IUpgradeableModule; the inherited UpgradeModuleData
//     fields parse but are inert (F-DWB-3).
//   - Update() runs every frame (UPDATE_SLEEP_NONE): with no victim, or a victim outside
//     SwitchWeaponOnCloseRangeDistance, the CLOSE_RANGE bit is clear; with a victim within
//     range (the landed partition seam's strict-< in-range predicate), it is set. Writes are
//     transition-only (rising/falling edge), matching EnemyNearUpdate.Update()'s shape.
//   - the condition -> weapon-set lookup is EXACT-MATCH (WeaponSet.Update, WeaponSet.cs): an
//     object with no CLOSE_RANGE weapon set (e.g. ithilienpathfinder) still gets the bit set,
//     but the weapon-set re-resolve silently keeps the previous set (F-DWB-5). This is
//     pre-existing engine behavior, not introduced by this port.
//   - a degenerate SwitchWeaponOnCloseRangeDistance <= 0 (default, and the shipped
//     gondorarcher shape whose distance field is absent) never sets the bit and skips the
//     per-frame partition query entirely (F-DWB-4).
//
// FINDINGS (behavior-fact gaps and held fields, filed not invented - see the spec's §5):
//   F-DWB-1 UseRealVictimRange = Yes: HELD. The module-facing partition seam
//     (IPartitionQuery.QueryObjectsInRadius) is hardcoded to centre-to-centre distance with no
//     distance-type parameter, and the object-side bounding radius (Geometry.cs) is still
//     float, not Fix64 - so there is no equivalent seam to request a bounding-circle-adjusted
//     range without a framework change (out of scope, api-freeze-v1 §6). The field parses and
//     is stored but the runtime never branches on it. Test case 12 is the tripwire.
//   F-DWB-2 UseHordeRangeWeapon: HELD, never invent. Not in the engine parse table; no field
//     in the ported table supports a horde-aggregation reading. No behavior added for it.
//   F-DWB-5 exact-match weapon-set lookup: see above; pinned by test case 9.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class DualWeaponBehavior : UpdateModule
{
    private readonly DualWeaponBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>The CLOSE_RANGE bit as this module last set it (edge-detection memory, not a
    /// second copy of GameObject.WeaponSetConditions - that bitset has its own persist walk).</summary>
    private bool _closeRange;

    /// <summary>The frozen ported-module ctor (api-freeze-v1 §3 item 2). No ctor RNG draw:
    /// there is no ScanDelayTime-equivalent to stagger, so the logic RNG stream is untouched
    /// (relevant to CRC review).</summary>
    public DualWeaponBehavior(GameObject gameObject, ISimContext context, DualWeaponBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;
    }

    // UPDATE_SLEEP_NONE, no UpdateOrder override: this module must re-evaluate whenever either
    // the victim or either object's position changes and has no cadence field to gate a scan
    // on, and the default Order2 sorts after AIUpdate's Order0 within a frame (sleepy-queue
    // phase bits, UpdateModule.cs) so CurrentVictimId is always same-frame-fresh here.
    public override UpdateSleepTime Update()
    {
        if (_data.SwitchWeaponOnCloseRangeDistance <= Fix64.Zero)
        {
            // F-DWB-4 degenerate guard: no authored distance (or an authored non-positive one)
            // means "never close range" and skips the per-frame partition query.
            SetCloseRange(false);
            return UpdateSleepTime.None;
        }

        var victimId = GameObject.AIUpdate?.CurrentVictimId ?? ObjectId.Invalid;
        if (!victimId.IsValid)
        {
            SetCloseRange(false);
            return UpdateSleepTime.None;
        }

        var inRange = false;
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.SwitchWeaponOnCloseRangeDistance))
        {
            if (candidate.Id == victimId)
            {
                inRange = true;
                break;
            }
        }

        SetCloseRange(inRange);
        return UpdateSleepTime.None;
    }

    private void SetCloseRange(bool value)
    {
        if (value == _closeRange)
        {
            // Transition-only write: WeaponSet.Update() early-outs on an unchanged resolved
            // set anyway, but guarding here matches EnemyNearUpdate's rising/falling-edge
            // shape and keeps the write count observable in tests (case 10).
            return;
        }

        _closeRange = value;
        GameObject.SetWeaponSetCondition(WeaponSetConditions.CloseRange, value);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Load-side subtlety: GameObject's own persist walk restores WeaponSetConditions, and
    // WeaponSet.Persist restores the resolved set, before this module's Xfer runs - so the bit
    // and the resolved set are already consistent on load. _closeRange is restored to match
    // and the next Update() writes nothing unless the situation actually changed; Xfer/Load
    // must not re-assert the bit, which would double-write engine-owned state.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("CloseRange", ref _closeRange); // XferBool is always exact (A3)
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class DualWeaponBehaviorModuleData : UpgradeModuleData
{
    internal static DualWeaponBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<DualWeaponBehaviorModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<DualWeaponBehaviorModuleData>
        {
            { "SwitchWeaponOnCloseRangeDistance", (parser, x) => x.SwitchWeaponOnCloseRangeDistance = parser.ParseFix64() },
            { "UseRealVictimRange", (parser, x) => x.UseRealVictimRange = parser.ParseBoolean() }
        });

    /// <summary>
    /// World-unit melee-engagement radius fed straight to the partition seam
    /// (IPartitionQuery.QueryObjectsInRadius). ParseFix64 round-half-up at parse, S5
    /// quantization at load (design-module-api §2.2) - the shipped corpus is all-integer so
    /// this is exact. Default Fix64.Zero (was int 0) is the degenerate "never close range"
    /// case, reachable in shipped data (F-DWB-4, gondorarcher).
    /// </summary>
    public Fix64 SwitchWeaponOnCloseRangeDistance { get; private set; } = Fix64.Zero;

    /// <summary>
    /// held: F-DWB-1. Parses (unchanged ParseBoolean) and is stored, but the runtime never
    /// reads it - the module-facing partition seam is hardcoded to centre-to-centre distance
    /// (no PartitionDistanceType parameter) and the object-side bounding radius is still
    /// float, so there is no equivalent Fix64 seam to request a bounding-circle-adjusted
    /// range without a framework change (out of scope, api-freeze-v1 §6). Test case 12 is the
    /// tripwire that fails the day someone implements this.
    /// </summary>
    public bool UseRealVictimRange { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DualWeaponBehavior(gameObject, gameEngine.SimContext, this);
    }
}
