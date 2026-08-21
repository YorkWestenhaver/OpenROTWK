// DetachableRiderUpdate - R13 port. Data-derivable from the module's own INI comment block
// (cinematicobjects.ini:9789-9802, quoted in full below); no GPL sibling exists
// (RiderChangeContain is a different module - mount boarding on a vehicle, already ported at
// Logic/Object/Contain/RiderChangeContain.cs) - see research/modules-r13/specs/
// DetachableRiderUpdateModuleData.md §0.1 for the full citation.
//
// SPLIT VERDICT (spec verdict line): the *reaction* half - what happens once a rider is known
// to have died - ports now, behind a driven OnRiderDied() seam. The *detection* half - who
// calls it, and when - stays blocked on the deliberately unfrozen Contain rider-slot surface
// (api-freeze-v1.md:252); filed as F-DRU-1. The module must not poll, scan horde members, or
// infer death from health (spec §0.5); T9 below pins that inertness as an observable fact.
//
// INI comment block, the entire behavioral source for §2 (cinematicobjects.ini:9789-9802):
//   ;List of any number of subobject names to toggle off when rider is killed. When new rider
//   ;joins unit, the subobjects will be turned back on.
//   ...
//   ;When unit is riderless, the weapon will get locked to this slot.
//   ...
//   ;When entire horde is riderless, will they all flee off the map? (player loses control)
//   RiderlessHordeFlees = Yes ;***NOTE*** If set, requires RunOffMapBehavior module!
//   ;When the rider is killed, a random death entry will be chosen. It plays specified
//   ;animation, the animationTime must match the length of the animation, and after the
//   ;animation is finished, the specified OCL will be created to leave a dead rider behind.
//
// PARSE BUG FIXED (spec §1): DeathEntry.Parse previously read only AnimState/AnimTime and
// silently dropped the shipped RiderOCL attribute (cinematicobjects.ini:9803,21277). AnimTime
// now quantizes at the parse boundary (S5) via ParseAttributeTimeMillisecondsToLogicFrames,
// never in sim code.
//
// LANDED-SEAM MAPPING (all landed, zero new interface members - framework growth avoided per
// spec §3):
//   - Context.GameLogic.CreateFromObjectCreationList (ISimContext.cs:115) - the same OCL spawn
//     path CreateObjectDie.Die already uses (Logic/Object/Die/CreateObjectDie.cs:61-64). The
//     float-substrate crossing (position, disposition, lifetime) lives behind the adapter,
//     never in this [SimState] module (D-7).
//   - GameObject.FindBehavior<RunOffMapBehavior>()?.Trigger() closes F-ROM-5 from the caller
//     side (RunOffMapBehavior.cs:119; both modules live on the same GameObject, no
//     order-pipeline hop needed).
//   - LockedWeaponSlot mirrors the landed F-WMSP-1 idiom verbatim
//     (WeaponModeSpecialPowerUpdate.cs:87): tracked+exposed read-only, no WeaponSet override
//     point exists to force a slot, so none is added here (api-freeze-v1.md §6).
//
// NOT PORTED (§0.2-0.5, findings filed rather than invented around - see §5 for full writeups):
//   F-DRU-1: no landed caller for OnRiderDied() - rider-death detection reaches into the
//     deliberately unfrozen Contain rider-slot surface.
//   F-DRU-2: RiderlessWeaponSlot has no engine consumer (F-WMSP-1 idiom).
//   F-DRU-3: RiderSubObjects (subobject show/hide) and DeathEntry.AnimState (model-condition
//     push) are Drawable/render-side concerns under the EmpUpdate/F-EMP-5 precedent - held,
//     not driven.
//   F-DRU-4: the RiderOCL attribute reader is positional (must be DeathEntry's trailing
//     attribute, as it is in both shipped instances).
//   F-DRU-5: the shipped Rohirrim RunOffMapBehavior sibling has no RunOffMapWaypointName, so
//     triggering it lands in RunOffMapBehavior's own F-ROM-1 sleep-forever path - correct
//     observed behavior of the shipped data, no default waypoint invented.
//   F-DRU-6: "a random death entry" has no field-table support (single assignment, one
//     shipped DeathEntry per module) - no list, no RNG draw.
//   F-DRU-7: the OCL spawn's secondary is always null - the seam carries no killer argument
//     (mirrors DetachableRiderBody.OnRiderDied's zero-arg shape; ISimContext.cs:106-108
//     explicitly permits a null secondary).
//   F-DRU-8: "entire horde is riderless" is a detection-side quantifier; this port triggers
//     the flee at the moment THIS object becomes riderless, the only thing a per-object
//     module can observe.
//
// Every mutable sim field appears in Xfer exactly once (declaration order is Xfer order, F9).

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class DetachableRiderUpdate : UpdateModule
{
    private readonly DetachableRiderUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether the rider has died (idempotence gate for <see cref="OnRiderDied"/>);
    /// also gates <see cref="LockedWeaponSlot"/>.</summary>
    private bool _riderless;

    /// <summary>The frame <see cref="OnRiderDied"/> fired, sentinel <c>LogicFrame.MaxValue</c>
    /// while the rider lives (EmpUpdate's <c>_dieFrame</c> sentinel idiom). The OCL fire frame
    /// is recomputed from this persisted absolute start frame, not a countdown, so a save/load
    /// taken mid-animation resumes at the same remaining frame count (spec §2.2, test T7).</summary>
    private LogicFrame _deathAnimStartFrame = LogicFrame.MaxValue;

    /// <summary>Whether <see cref="DetachableRiderUpdateModuleData.DeathEntry"/>'s RiderOCL has
    /// already been spawned - guarantees "exactly once" across re-entry, extra wakes, and a
    /// save/load taken mid-animation.</summary>
    private bool _riderOclFired;

    public DetachableRiderUpdate(GameObject gameObject, ISimContext context, DetachableRiderUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // Inert until OnRiderDied() fires the seam: no per-frame work to do while the rider
        // lives, and (spec §0.5) this module must not poll for the rider's death.
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    /// <summary>
    /// The rider has died: the horde member this module sits on is now riderless. Driven seam -
    /// the detection half (WHO calls this, and when) reaches into the Contain rider-slot surface
    /// api-freeze-v1 §7 leaves deliberately unfrozen, so it is filed (F-DRU-1), not invented.
    /// Mirrors DetachableRiderBody.OnRiderDied's landed shape. Idempotent: a second call is a
    /// no-op, so a double-notify can never re-arm the death animation or double-spawn the OCL.
    /// </summary>
    public void OnRiderDied()
    {
        if (_riderless)
        {
            return;
        }

        _riderless = true;
        _deathAnimStartFrame = Context.CurrentFrame;

        if (_data.RiderlessHordeFlees)
        {
            GameObject.FindBehavior<RunOffMapBehavior>()?.Trigger();
        }

        if (_data.DeathEntry?.RiderOCL != null)
        {
            SetWakeFrame(UpdateSleepTime.Frames(_data.DeathEntry.AnimationTime));
        }
        // Otherwise nothing left to do - stay parked at Forever.
    }

    /// <summary>F-DRU-2: tracked+exposed, unconsumed by anything landed today - no WeaponSet
    /// override point exists to force this slot (the same gap WeaponModeSpecialPowerUpdate
    /// filed as F-WMSP-1, resolved the same way).</summary>
    public WeaponSlot? LockedWeaponSlot => _riderless ? _data.RiderlessWeaponSlot : (WeaponSlot?)null;

    public override UpdateSleepTime Update()
    {
        if (!_riderless || _riderOclFired)
        {
            return UpdateSleepTime.Forever;
        }

        var entry = _data.DeathEntry;
        if (entry?.RiderOCL == null)
        {
            return UpdateSleepTime.Forever;   // nothing to leave behind (F-DRU-3)
        }

        var fireFrame = _deathAnimStartFrame + entry.AnimationTime;
        if (Context.CurrentFrame < fireFrame)
        {
            return UpdateSleepTime.Frames(fireFrame - Context.CurrentFrame);
        }

        _riderOclFired = true;
        Context.GameLogic.CreateFromObjectCreationList(entry.RiderOCL.Value, GameObject, null);
        return UpdateSleepTime.Forever;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Declaration order is Xfer order (F9). No Legacy Load(StatePersister): the module is
    // BFME2-only with no pinned retail save layout, same posture as RunOffMapBehavior.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Riderless", ref _riderless);
        xfer.XferFrame("DeathAnimStartFrame", ref _deathAnimStartFrame);
        xfer.XferBool("RiderOclFired", ref _riderOclFired);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class DetachableRiderUpdateModuleData : UpdateModuleData
{
    internal static DetachableRiderUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<DetachableRiderUpdateModuleData> FieldParseTable = new IniParseTable<DetachableRiderUpdateModuleData>
        {
            { "RiderSubObjects", (parser, x) => x.RiderSubObjects = parser.ParseAssetReferenceArray() },
            { "RiderlessWeaponSlot", (parser, x) => x.RiderlessWeaponSlot = parser.ParseEnum<WeaponSlot>() },
            { "RiderlessHordeFlees", (parser, x) => x.RiderlessHordeFlees = parser.ParseBoolean() },
            { "DeathEntry", (parser, x) => x.DeathEntry = DeathEntry.Parse(parser) },
        };

    // held: render-only subobject show/hide (F-EMP-5 precedent); nothing establishes it as
    // sim-determining (spec §0.3/§2.5).
    public string[] RiderSubObjects { get; private set; }

    public WeaponSlot RiderlessWeaponSlot { get; private set; }
    public bool RiderlessHordeFlees { get; private set; }
    public DeathEntry DeathEntry { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DetachableRiderUpdate(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class DeathEntry
{
    internal static DeathEntry Parse(IniParser parser)
    {
        var result = new DeathEntry
        {
            AnimationState = parser.ParseAttributeIdentifier("AnimState"),
            AnimationTime = parser.ParseAttributeTimeMillisecondsToLogicFrames("AnimTime")
        };

        // RiderOCL is optional: not every DeathEntry in the wild need carry it, and the
        // attribute reader is positional (it must be the trailing attribute, as it is in both
        // shipped instances) - F-DRU-4.
        if (parser.ParseAttributeOptional("RiderOCL", parser.ParseObjectCreationListReference, out var ocl))
        {
            result.RiderOCL = ocl;
        }

        return result;
    }

    // held: model-condition push is render-side; only AnimTime drives sim timing (spec §0.3/§2.5).
    public string AnimationState { get; private set; }

    public LogicFrameSpan AnimationTime { get; private set; }
    public LazyAssetReference<ObjectCreationList>? RiderOCL { get; private set; }
}
