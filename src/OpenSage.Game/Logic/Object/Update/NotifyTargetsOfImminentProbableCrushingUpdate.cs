// NotifyTargetsOfImminentProbableCrushingUpdate - R12 port. BFME-only (no generals-gpl
// sibling); no clean-room behavioral spec of this exact class exists in bfme2-workbench/research/
// (spec-hordes.md documents the sibling HordeNotifyTargetsOfImminentProbableCrushingUpdate,
// which adds one field, ScanWidth, and is a SEPARATE registered module - ported alongside
// this one in R12, see HordeNotifyTargetsOfImminentProbableCrushingUpdate.cs).
// Census over the full AotR INI corpus confirms this class is genuinely field-less: every
// authored use (`grep -rn "Behavior = NotifyTargetsOfImminentProbableCrushingUpdate"`)
// is a bare `ModuleTag_NotifyCrushScan` block with an immediate `End` - mumakil, corsair
// ships, trolls, chariots, and - importantly - actual cavalry hordes (VanguardHorde,
// MorgulVanguard, MordorHordes all carry it directly on the horde object). So this is the
// base crush-warning behavior; the Horde-prefixed sibling is a later variant that exposes
// ScanWidth as an authorable override. The shape (periodic scan -> grant/revoke a boolean
// condition on newly (in)eligible targets) matches the landed AttributeModifierAuraUpdate
// (R12) and EnemyNearUpdate (R9) ports; this is fresh code against the frozen contract, not
// a decompiled transplant.
//
// Behavior modeled (spec-hordes.md §8, "Crushing"): cavalry/heavy crush resolves per member
// (CrushableLevel on members); this update warns nearby crushable targets so they can react
// (in retail: trigger the BraceForBeingCrushed emotion so members brace/dodge). Per spec,
// hordes are two-layer (spec §1): members are real, individually-positioned GameObjects, so a
// plain radius scan over live objects finds horde members directly - no SimHordeContain
// coupling is needed to reach "the horde's members".
//
// TODO-spec (unverified/unmodeled, filed not invented):
//   - ScanWidth is not an authorable field on this class (confirmed by corpus census above),
//     so a scan radius constant is required for the module to function at all. No behavioral spec
//     probe of this class's C++ default exists in the workbench. DefaultScanWidth below is
//     set from the observed range of the sibling class's OWN authored ScanWidth values across
//     the corpus (40..70, always described as "a little less than the horde width") - a
//     data-anchored midpoint, not an invented magic number, but still unconfirmed against this
//     class's actual retail default. Flagged for a future spec pass (F-NTIPC-1).
//   - the exact crush-PROBABILITY computation (path prediction, timing window implied by
//     "Probable" in the class name) is not modeled: this port uses a static eligibility gate
//     (in scan radius, enemy, CrusherLevel(self) > CrushableLevel(candidate)) rather than a
//     forward path/collision prediction. "Probability drops to zero" (a task test-case
//     phrasing) is modeled as "candidate leaves the eligibility set" (F-NTIPC-2).
//   - EmotionCheerForAboutToCrush (the crusher-side onlooker-cheer condition) is a distinct
//     model condition in ModelConditionFlag; spec-hordes.md ties only
//     EmotionBraceForBeingCrushed to this behavior, so the cheer condition is left unmodeled
//     here (F-NTIPC-3).
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's
// conformance class at its declaration site.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class NotifyTargetsOfImminentProbableCrushingUpdate : UpdateModule
{
    /// <summary>
    /// F-NTIPC-1: not an authored field on this class (corpus census confirms zero fields);
    /// anchored to the sibling HordeNotifyTargetsOfImminentProbableCrushingUpdate's own
    /// observed ScanWidth range (40..70) rather than an arbitrary constant.
    /// </summary>
    internal static readonly Fix64 DefaultScanWidth = new Fix64(40);

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>ObjectIds currently warned (braced) by this crusher, in the order they were
    /// warned (a function of the ascending-ObjectId partition scan, so deterministic across
    /// peers given identical prior events - our own order, never sorted post hoc).</summary>
    private readonly List<ObjectId> _warnedTargets = new();

    public NotifyTargetsOfImminentProbableCrushingUpdate(GameObject gameObject, ISimContext context)
        : base(gameObject, context)
    {
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>Test/inspection view of the currently-warned target set.</summary>
    internal IReadOnlyList<ObjectId> WarnedTargets => _warnedTargets;

    public override UpdateSleepTime Update()
    {
        RefreshWarnings();

        return UpdateSleepTime.None;
    }

    /// <summary>
    /// One scan/warn/clear pass: query the S3 partition seam within DefaultScanWidth, set
    /// EmotionBraceForBeingCrushed on every still-eligible candidate not already warned, and
    /// clear it from any previously-warned target that fell out of eligibility (moved beyond
    /// scan width, died, or is no longer crushable by this object - F-NTIPC-2).
    /// </summary>
    private void RefreshWarnings()
    {
        // A crusher level of 0 crushes nothing (GameObject.CrusherLevel default): skip the
        // scan entirely rather than warn targets that can never actually be crushed.
        if (GameObject.CrusherLevel == 0)
        {
            ClearAll();
            return;
        }

        var owner = GameObject.Owner;

        var eligible = new List<ObjectId>();
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, DefaultScanWidth))
        {
            if (IsEligible(candidate, owner))
            {
                eligible.Add(candidate.Id);
            }
        }

        // Clear targets that fell out of eligibility.
        for (var i = _warnedTargets.Count - 1; i >= 0; i--)
        {
            var id = _warnedTargets[i];
            if (eligible.Contains(id))
            {
                continue;
            }

            ClearWarning(id);
            _warnedTargets.RemoveAt(i);
        }

        // Warn every eligible target, re-asserting the flag each tick rather than only on
        // first sight. EmotionBraceForBeingCrushed is shared, un-refcounted state on the
        // TARGET: this module and its sibling (Horde/base
        // NotifyTargetsOfImminentProbableCrushingUpdate) both set and clear it, and any
        // number of crushers can be bracing the same target at once. With a "skip if already
        // in _warnedTargets" guard, one crusher's ClearWarning would wipe the flag while
        // another crusher still bears down, and that second crusher would never re-set it
        // (its own list still lists the target as warned) - the target would stay
        // permanently un-braced. Re-asserting costs one redundant flag write and self-heals.
        foreach (var id in eligible)
        {
            var target = Context.GameLogic.GetObjectById(id);
            if (target == null)
            {
                continue;
            }

            target.SetModelConditionState(ModelConditionFlag.EmotionBraceForBeingCrushed);
            if (!_warnedTargets.Contains(id))
            {
                _warnedTargets.Add(id);
            }
        }
    }

    private void ClearAll()
    {
        foreach (var id in _warnedTargets)
        {
            ClearWarning(id);
        }
        _warnedTargets.Clear();
    }

    private void ClearWarning(ObjectId id)
    {
        var target = Context.GameLogic.GetObjectById(id);
        target?.ClearModelConditionState(ModelConditionFlag.EmotionBraceForBeingCrushed);
    }

    /// <summary>
    /// The per-candidate gate: liveness, enemy relationship, non-structure, and
    /// CrusherLevel(self) &gt; CrushableLevel(candidate) - "this crusher could actually crush
    /// this target" (spec-hordes.md §8's CrushableLevel gate), the static proxy for "probable
    /// crushing" (F-NTIPC-2).
    /// </summary>
    private bool IsEligible(GameObject candidate, Player owner)
    {
        if (candidate == GameObject)
        {
            return false;
        }

        if (candidate.IsDestroyed || candidate.IsEffectivelyDead || candidate.IsOffMap)
        {
            return false;
        }

        if (owner == null || candidate.Owner == null || !owner.Enemies.Contains(candidate.Owner))
        {
            return false;
        }

        if (candidate.Definition.KindOf is not null &&
            candidate.Definition.KindOf.Get(ObjectKinds.Structure))
        {
            return false;
        }

        return candidate.CrushableLevel < GameObject.CrusherLevel;
    }

    // ---- the single walk (F8 Objects channel; declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferList("WarnedTargets", _warnedTargets, XferWarnedTarget);
    }

    private static void XferWarnedTarget(IXfer xfer, ref ObjectId id)
    {
        xfer.XferObjectId("Target", ref id);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class NotifyTargetsOfImminentProbableCrushingUpdateModuleData : UpdateModuleData
{
    internal static NotifyTargetsOfImminentProbableCrushingUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    // Corpus-confirmed field-less (see file header): every authored block is bare.
    private static readonly IniParseTable<NotifyTargetsOfImminentProbableCrushingUpdateModuleData> FieldParseTable = new IniParseTable<NotifyTargetsOfImminentProbableCrushingUpdateModuleData>
    {
    };

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new NotifyTargetsOfImminentProbableCrushingUpdate(gameObject, gameEngine.SimContext);
    }
}
