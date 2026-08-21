// AttachUpdate - R13 port. BFME2-only, no generals-gpl sibling exists (no "AttachUpdate" match
// anywhere in generals-gpl/generals-community) - every citation below is
// crate.ini/field-name/already-ported-primitive based, not GPL translation (see
// research/modules-r13/specs/AttachUpdateModuleData.md §1.0). No GPL text is quoted here.
//
// Live usage: the One Ring pickup mechanic (AOTR crate.ini:630-640, TheOneRing crate object).
// A scan-for-carrier object with exactly two phases, selected by whether a carrier is
// currently attached (spec §1.2):
//   A. Unattached (scanning), every tick (UpdateSleepTime.None, matches EmpUpdate's
//      always-tick posture - nothing in the field list suggests a sleep interval):
//        1. Context.Partition.QueryObjectsInRadius(GameObject, ScanRange) - the same
//           already-ported seam EmpUpdate.DoDisableAttack/EnemyNearUpdate use.
//        2. Skip a candidate that ObjectFilter.Matches() (spec §1.1: despite the field's own
//           name "ObjectFilter", the live AOTR data names it AOTR_CANNOT_CARRY_RING and the
//           ObjectFilter.Matches convention used identically by dozens of already-ported
//           modules reads it as "skip anything that matches" - an eligible carrier is one
//           NOT matched by the filter). Dead/destroyed candidates are already excluded by the
//           partition query's "live objects" contract; IsDestroyed is still checked
//           defensively, the same shape EmpUpdate's own comment documents.
//        3. First eligible candidate in the partition's deterministic order becomes the new
//           carrier; attach (below) and move to phase B.
//   B. Attached (carried), every tick:
//        1. If the carrier is destroyed (carrier.IsDestroyed): fire ParentOwnerDiedEvaEvent,
//           clear ParentStatus on the carrier, drop the reference, and return to phase A -
//           F-ATU-1 below (data-derivation, not a Ghidra fact).
//        2. Otherwise, snap this object's own display transform onto the carrier's (§1.5)
//           every tick.
//
// Attach step (spec §1.3): GameObject.SetObjectStatus(ParentStatus, true) on the carrier (the
// already-ported bit-flag primitive every other status-setting module already uses;
// ObjectStatus.HoldingTheRing already exists, ObjectStatus.cs:285), record the carrier's
// ObjectId as sim state, snap this object's transform onto the carrier immediately, and fire
// ParentOwnerAttachmentEvaEvent/ParentEnemyAttachmentEvaEvent exactly once (edge-triggered, not
// repeated every attached tick - the same one-shot-per-transition shape EmpUpdate's
// DoDisableAttack "exactly once, at StartFadeTime" contract already establishes).
//
// Eva event perspective triad (spec §1.4): sim code cannot know "the local player" - an
// inherently per-client, non-deterministic notion - so ISimEvents.FireRelativeEvaEvent (new
// this port) hands the client enough to resolve which name to play at playback time, the same
// key-into-a-client-side-table shape FireUnitSoundAtObject already establishes. The
// perspective-owner id is always the carrier's: on attach, ownerEventName =
// ParentOwnerAttachmentEvaEvent, alliedEventName = null (no such field), enemyEventName =
// ParentEnemyAttachmentEvaEvent; on carrier death, ownerEventName = ParentOwnerDiedEvaEvent,
// both others null.
//
// Parent-follow transform (spec §1.5): reuses OpenSage.Logic.Object.Locomotion.
// SimTransformBridge (internal static, same assembly) rather than new float-substrate
// plumbing - PullPosition/PullGeometry read the carrier's Fix64-quantized transform,
// PullYaw its heading, and Push writes this object's own display transform. AlwaysTeleport is
// authored as an instantaneous-snap flag; AlwaysTeleport = No ("smooth follow") has no
// smoothing-rate field anywhere in this module's parse table (F-ATU-3 below) - both branches
// snap identically pending a real interpolation design.
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-ATU-1 (carrier-death re-scan): "drop and re-scan on carrier death" is a data-derivation
//     argument from the field name ParentOwnerDiedEvaEvent plus the
//     CitadelSlaughterHordeContain.cs:6 comment implying HOLDING_THE_RING is meant to be
//     re-enterable, not a hard fact from GPL or an INI comment. Confidence: medium-high, not
//     certain - flag for owner sign-off if this reading is contested.
//   F-ATU-2 (top-of-geometry height precision): AnchorToTopOfGeometry = Yes uses
//     SimTransformBridge.PullGeometry(carrier).MajorRadius as the Z offset (the nearest
//     already-exposed Fix64 geometry proxy); this is not necessarily the actual top-of-
//     bounding-box height a "sits on head" crate visual would want (that is usually a
//     per-geometry height field, not a horizontal-plane radius). No height accessor exists on
//     this engine's Fix64-safe surface today. Every peer computes the identical approximate
//     value from the identical carrier geometry, so this is an approximation, not a
//     determinism hazard.
//   F-ATU-3 (AlwaysTeleport = No smooth-follow): no smoothing-rate field exists anywhere in
//     this module's parse table (no MoveTowardTargetSpeed, no percent-per-frame constant,
//     nothing analogous to EmpUpdate.ScaleBlendFactor's literal 0.05f), and the Ring crate
//     itself has no locomotor of its own to drive an interpolation. This port implements the
//     AlwaysTeleport = No branch identically to the Yes branch (snap every tick). Load-bearing:
//     the live AOTR Ring data (crate.ini:630-640) authors AlwaysTeleport = No, so this finding
//     directly governs the One Ring's actually-authored behavior.
//
// Every mutable sim field appears in Xfer exactly once (below); position/yaw are deliberately
// NOT xfer'd - they are recomputed every attached tick from the carrier's own (already-xfer'd,
// already-authoritative) transform via SimTransformBridge, so persisting a redundant copy
// would double-count state the carrier's own module tree already owns (F9).

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AttachUpdate : UpdateModule
{
    private readonly AttachUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether a carrier is currently attached (phase A vs phase B, spec §1.2).</summary>
    private bool _attached;

    /// <summary>The current carrier's ObjectId; ObjectId.Invalid when <see cref="_attached"/>
    /// is false.</summary>
    private ObjectId _carrierId;

    public AttachUpdate(GameObject gameObject, ISimContext context, AttachUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _carrierId = ObjectId.Invalid;

        // Scan-for-pickup must notice a carrier walking within range on the very next logic
        // frame, and an attached carrier's death must be noticed just as promptly - same
        // always-tick posture EmpUpdate/EnemyNearUpdate already use for radius-scan modules.
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>Test-visible observable: whether a carrier is currently attached.</summary>
    public bool IsAttached => _attached;

    /// <summary>Test-visible observable: the current carrier's ObjectId (Invalid when unattached).</summary>
    public ObjectId CarrierId => _carrierId;

    public override UpdateSleepTime Update()
    {
        if (!_attached)
        {
            ScanForCarrier();
        }
        else
        {
            TrackCarrier();
        }

        return UpdateSleepTime.None;
    }

    /// <summary>Phase A (spec §1.2.A): scan ScanRange for the first eligible carrier.</summary>
    private void ScanForCarrier()
    {
        var self = GameObject;
        var scanRange = new Fix64(_data.ScanRange);

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(self, scanRange))
        {
            if (candidate == self || candidate.IsDestroyed)
            {
                continue;
            }

            // Spec §1.1: ObjectFilter reads as "skip anything that matches" - an eligible
            // carrier is one NOT matched by the filter. A null filter (none authored) excludes
            // nothing, matching the PickupStuffUpdate/other ObjectFilter-optional convention.
            if (_data.ObjectFilter != null && _data.ObjectFilter.Matches(candidate))
            {
                continue;
            }

            Attach(candidate);
            return;
        }
    }

    /// <summary>Spec §1.3: set the status flag, record the carrier, snap onto it immediately,
    /// and fire the attachment Eva event exactly once.</summary>
    private void Attach(GameObject carrier)
    {
        carrier.SetObjectStatus(_data.ParentStatus, true);
        _carrierId = carrier.Id;
        _attached = true;

        SnapToCarrier(carrier);

        // Edge-triggered, fired exactly once at the moment of attach (spec §1.3/§1.4) - not
        // repeated on subsequent attached ticks (TrackCarrier below never calls this).
        Context.Events.FireRelativeEvaEvent(
            carrier.Id,
            _data.ParentOwnerAttachmentEvaEvent,
            null,
            _data.ParentEnemyAttachmentEvaEvent);
    }

    /// <summary>Phase B (spec §1.2.B): follow the carrier, or drop and re-scan if it died.</summary>
    private void TrackCarrier()
    {
        var carrier = Context.GameLogic.GetObjectById(_carrierId);

        if (carrier == null || carrier.IsDestroyed)
        {
            // F-ATU-1: drop and return to scanning. carrier may be null if the object was
            // fully reaped between ticks; the status flag then has nothing left to clear.
            carrier?.SetObjectStatus(_data.ParentStatus, false);

            Context.Events.FireRelativeEvaEvent(
                _carrierId,
                _data.ParentOwnerDiedEvaEvent,
                null,
                null);

            _attached = false;
            _carrierId = ObjectId.Invalid;
            return;
        }

        // AlwaysTeleport = Yes and = No both snap every tick (F-ATU-3): no smoothing-rate
        // field exists in this module's parse table to drive a smooth follow.
        SnapToCarrier(carrier);
    }

    /// <summary>Writes this object's display transform onto the carrier's, applying the
    /// AnchorToTopOfGeometry Z offset (F-ATU-2) when authored. Reused identically by the
    /// attach-frame snap (§1.3) and every attached-phase follow tick (§1.5) - the INI comment
    /// draws no distinction between them.</summary>
    private void SnapToCarrier(GameObject carrier)
    {
        var position = SimTransformBridge.PullPosition(carrier);

        if (_data.AnchorToTopOfGeometry)
        {
            // F-ATU-2: MajorRadius is the nearest already-exposed Fix64 geometry proxy for a
            // "top of geometry" height estimate; no dedicated height accessor exists.
            var (_, majorRadius) = SimTransformBridge.PullGeometry(carrier);
            position = new FixVector3(position.X, position.Y, position.Z + majorRadius);
        }

        var yaw = SimTransformBridge.PullYaw(carrier);
        SimTransformBridge.Push(GameObject, position, yaw);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Attached", ref _attached);
        xfer.XferObjectId("CarrierId", ref _carrierId);
        // Position/yaw are NOT xfer'd here (see file header): they are recomputed every
        // attached tick from the carrier's own already-authoritative transform.
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Scans for an eligible carrier and attaches to it (following its transform and marking it
/// with a status flag) until that carrier dies, then drops and re-scans. The One Ring pickup
/// mechanic's module (AOTR crate.ini TheOneRing).
/// </summary>
[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class AttachUpdateModuleData : UpdateModuleData
{
    internal static AttachUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<AttachUpdateModuleData> FieldParseTable = new IniParseTable<AttachUpdateModuleData>
    {
        { "ObjectFilter", (parser, x) => x.ObjectFilter = ObjectFilter.Parse(parser) },
        { "ScanRange", (parser, x) => x.ScanRange = parser.ParseInteger() },
        { "ParentStatus", (parser, x) => x.ParentStatus = parser.ParseEnum<ObjectStatus>() },
        { "ParentOwnerAttachmentEvaEvent", (parser, x) => x.ParentOwnerAttachmentEvaEvent = parser.ParseAssetReference() },
        { "ParentEnemyAttachmentEvaEvent", (parser, x) => x.ParentEnemyAttachmentEvaEvent = parser.ParseAssetReference() },
        { "ParentOwnerDiedEvaEvent", (parser, x) => x.ParentOwnerDiedEvaEvent = parser.ParseAssetReference() },
        { "AlwaysTeleport", (parser, x) => x.AlwaysTeleport = parser.ParseBoolean() },
        { "AnchorToTopOfGeometry", (parser, x) => x.AnchorToTopOfGeometry = parser.ParseBoolean() },
    };

    public ObjectFilter ObjectFilter { get; private set; }
    public int ScanRange { get; private set; }
    public ObjectStatus ParentStatus { get; private set; }
    public string ParentOwnerAttachmentEvaEvent { get; private set; }
    public string ParentEnemyAttachmentEvaEvent { get; private set; }
    public string ParentOwnerDiedEvaEvent { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public bool AlwaysTeleport { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public bool AnchorToTopOfGeometry { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AttachUpdate(gameObject, gameEngine.SimContext, this);
    }
}
