// HordeNotifyTargetsOfImminentProbableCrushingUpdate - R12 port. Cavalry-horde variant of the
// landed base NotifyTargetsOfImminentProbableCrushingUpdate (R12, see that file's header for
// the shared behavior model and its TODO-spec notes, which apply here unchanged). This sibling
// is authored with a single field - ScanWidth - letting cavalry hordes override the generic
// scan radius with a value "a little less than the horde width" (per corpus authoring pattern
// documented on the base class; the two classes are registered separately in BehaviorModule.cs
// and both are genuinely field-shape-distinct in the corpus - this one always carries
// ScanWidth, the base variant never does).
//
// Behavior modeled (spec-hordes.md §8, "Crushing"): identical eligibility gate and warn/clear
// mechanics to the base variant (enemy, non-structure, CrushableLevel(candidate) <
// CrusherLevel(self)), scanning at this module's own authored ScanWidth instead of the base's
// DefaultScanWidth constant.
//
// TODO-spec (unverified/unmodeled, filed not invented, carried from the base port):
//   - the exact crush-PROBABILITY computation (path prediction / timing window implied by
//     "Probable" in the class name) is not modeled; static eligibility gate is the proxy
//     (F-NTIPC-2, same as base).
//   - EmotionCheerForAboutToCrush (crusher-side onlooker cheer) is left unmodeled; spec-hordes.md
//     ties only EmotionBraceForBeingCrushed to this behavior (F-NTIPC-3, same as base).
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's conformance
// class at its declaration site.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Orders;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class HordeNotifyTargetsOfImminentProbableCrushingUpdate : UpdateModule
{
    private readonly HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData _moduleData;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>ObjectIds currently warned (braced) by this crusher, in the order they were
    /// warned (a function of the ascending-ObjectId partition scan, so deterministic across
    /// peers given identical prior events - our own order, never sorted post hoc).</summary>
    private readonly List<ObjectId> _warnedTargets = new();

    public HordeNotifyTargetsOfImminentProbableCrushingUpdate(
        GameObject gameObject,
        ISimContext context,
        HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData moduleData)
        : base(gameObject, context)
    {
        _moduleData = moduleData;
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
    /// One scan/warn/clear pass: query the S3 partition seam within this module's authored
    /// ScanWidth, set EmotionBraceForBeingCrushed on every still-eligible candidate not already
    /// warned, and clear it from any previously-warned target that fell out of eligibility
    /// (moved beyond scan width, died, or is no longer crushable by this object - F-NTIPC-2).
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
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _moduleData.ScanWidth))
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

        // Warn newly-eligible targets.
        foreach (var id in eligible)
        {
            if (_warnedTargets.Contains(id))
            {
                continue;
            }

            var target = Context.GameLogic.GetObjectById(id);
            if (target == null)
            {
                continue;
            }

            target.SetModelConditionState(ModelConditionFlag.EmotionBraceForBeingCrushed);
            _warnedTargets.Add(id);
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
public sealed class HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData : UpdateModuleData
{
    internal static HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData> FieldParseTable =
        new IniParseTable<HordeNotifyTargetsOfImminentProbableCrushingUpdateModuleData>
    {
        { "ScanWidth", (parser, x) => x.ScanWidth = parser.ParseFix64() },
    };

    /// <summary>Authorable scan radius (quantized at load, design-module-api §2.2); cavalry
    /// hordes override the base class's DefaultScanWidth with this value.</summary>
    public Fix64 ScanWidth { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new HordeNotifyTargetsOfImminentProbableCrushingUpdate(gameObject, gameEngine.SimContext, this);
    }
}
