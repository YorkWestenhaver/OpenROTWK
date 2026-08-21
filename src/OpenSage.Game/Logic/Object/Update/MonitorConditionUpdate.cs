// MonitorConditionUpdate - R13 port. Data-derivable (no generals-gpl/generals-community
// sibling; see modules-r13/specs/MonitorConditionUpdateModuleData.md for the full audit
// trail). No Ghidra material used or needed.
//
// Behavior (spec §1, from the AotR corpus and the author's own INI comment on
// harondorraiderhorde.ini: "Toggle CommandSet Based on Weaponset condition flags"): the
// module watches up to two independent (condition-BitArray, toggle-CommandSet) pairs -
// ModelConditionFlags/ModelConditionCommandSet and WeaponSetFlags/WeaponToggleCommandSet -
// and forces the object's Definition.CommandSet to a pair's toggle target while that pair's
// condition bits intersect the object's live condition state. A pair with no authored flags
// or CommandSet is inert (never evaluated). When neither authored condition holds, the
// object's Definition.CommandSet is restored to the object's original (pre-module)
// CommandSet, captured lazily on the module's first real Update (see below) as
// _baselineCommandSet - not simply "whatever was last forced", since siegemumak.ini's
// ATTACKING_POSITION -> ...CommandSetStopBombard pairing only makes sense as a temporary
// override (a mumak that could never bombard again after one attack-in-position tick would
// be an incoherent unit design). Re-asserted every tick (not edge-triggered) per the same
// self-healing idiom as NotifyTargetsOfImminentProbableCrushingUpdate.RefreshWarnings - see
// spec §1 for the full multi-peer-desync argument for why an edge-triggered write is unsafe
// here and a per-tick re-assert is not.
//
// Evaluation order when both pairs are authored and both conditions are simultaneously true
// (cavetroll.ini authors both pairs on one object): ModelConditionFlags pair first, then
// WeaponSetFlags pair (INI declaration order, F9 convention). No corpus example distinguishes
// an order for the both-true case; filed as F-MCU-1 (spec §1), not blocking.
//
// TODO-spec (filed, not invented):
//   - F-MCU-1: simultaneous-true-both-pairs resolution order is declaration-order convention
//     only, not corpus-proven. Revisit if an oracle capture ever exercises cavetroll.ini with
//     both conditions true at once.
//   - F-MCU-2 (baseline CommandSet Xfer, resolved below): _baselineCommandSet is a
//     LazyAssetReference<CommandSet> identity captured at runtime. IXfer's frozen primitive
//     set (api-freeze-v1 S4; IXfer.cs/XferPrimitives.cs/XferLoad.cs/XferSave.cs/
//     XferCrcVisitor.cs/XferDeepDump.cs) has no string/asset-reference primitive anywhere in
//     the codebase - every LazyAssetReference<T> field census'd under Logic/Object is
//     immutable ModuleData/config, never persisted mutable module state. The spec's presumed
//     exemplar (CommandSetUpgrade) turned out not to help either: it persists nothing for its
//     own CommandSet field (legacy StatePersister path, base.Load() only, no explicit
//     save/load of _moduleData.CommandSet) - there is no existing primitive to copy.
//     Extending the frozen IXfer surface across five files for one module's sake is out of
//     this task's reserved scope (spec §4 name reservations) and a shared-file change with
//     real cross-lane collision risk (project hard rules on concurrent-lane shared-file
//     edits). Resolution: _baselineCommandSet/_baselineCaptured are deliberately NOT walked
//     by Xfer. A freshly-deserialized module instance starts with _baselineCaptured == false
//     (ctor default, untouched by the walk below), so the next real Update after any Load
//     performs a fresh lazy capture from whatever GameObject.Definition.CommandSet holds at
//     that moment - self-healing and crash-free in every case, and behaviorally exact in the
//     overwhelmingly common case (baseline capture happens on the object's second logic
//     frame, long before any save/load in practice). The one residual risk - a save/load
//     landing while a toggle condition is actively true, where recapture would wrongly adopt
//     the live toggle CommandSet as the new baseline - is the same class of data-
//     underdetermined edge case as F-MCU-1: filed, not guessed at. See task report.
//
// Ctor note: the spec's illustrative ctor block omits a ModuleData parameter (copied from
// the field-less NotifyTargetsOfImminentProbableCrushingUpdate exemplar, which has no config
// to carry). This module needs its authored fields at Update time, so the ctor instead
// follows the established landed idiom for a config-bearing [SimState] Update module
// (AttributeModifierAuraUpdate.cs: `(GameObject, ISimContext, TModuleData)`, storing it in a
// private readonly field) - trusting the code idiom over the spec's literal (simpler-case)
// text, per the spec's own §2 "trust the code" correction rule.
//
// Every mutable sim field that IS safely representable in the frozen Xfer primitive set
// appears in Xfer exactly once (api-freeze-v1 §3 item 1); the one field that is not
// representable (see F-MCU-2 above) is deliberately excluded rather than corrupted. The two
// BitArray<...> condition sets and the two toggle-target references on ModuleData are
// immutable flyweight config, not module state - never xfered (UpdateModule's "modules keep
// a reference to their data and never copy config into mutable fields" rule).

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Gui.ControlBar;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class MonitorConditionUpdate : UpdateModule
{
    // Immutable flyweight config (never xfered - see file header).
    private readonly MonitorConditionUpdateModuleData _data;

    // ---- mutable sim state ----

    /// <summary>The object's Definition.CommandSet as it stood before this module ever
    /// touched it, captured lazily on the first real Update (see file header, F-MCU-2, for
    /// why this is deliberately not Xfer'd).</summary>
    private LazyAssetReference<CommandSet> _baselineCommandSet;

    private bool _baselineCaptured;

    public MonitorConditionUpdate(GameObject gameObject, ISimContext context, MonitorConditionUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        var data = _data;

        if (!_baselineCaptured)
        {
            _baselineCommandSet = GameObject.Definition.CommandSet;
            _baselineCaptured = true;
        }

        // Declaration order (F9): ModelConditionFlags pair first, then WeaponSetFlags pair.
        // See file header / spec F-MCU-1 for why this order and not the reverse.
        if (data.ModelConditionFlags is { AnyBitSet: true } modelFlags &&
            data.ModelConditionCommandSet != null &&
            modelFlags.Intersects(GameObject.ModelConditionFlags))
        {
            GameObject.Definition.CommandSet = data.ModelConditionCommandSet;
        }
        else if (data.WeaponSetFlags is { AnyBitSet: true } weaponFlags &&
                 data.WeaponToggleCommandSet != null &&
                 weaponFlags.Intersects(GameObject.WeaponSetConditions))
        {
            GameObject.Definition.CommandSet = data.WeaponToggleCommandSet;
        }
        else
        {
            GameObject.Definition.CommandSet = _baselineCommandSet;
        }

        return UpdateSleepTime.None;
    }

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        // See file header, F-MCU-2: _baselineCommandSet/_baselineCaptured are deliberately
        // not walked here - no frozen IXfer primitive can carry an asset-reference identity,
        // and the module self-heals by recapturing on the first real Update after any Load.
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class MonitorConditionUpdateModuleData : UpdateModuleData
{
    internal static MonitorConditionUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<MonitorConditionUpdateModuleData> FieldParseTable = new IniParseTable<MonitorConditionUpdateModuleData>
    {
        { "WeaponSetFlags", (parser, x) => x.WeaponSetFlags = parser.ParseEnumBitArray<WeaponSetConditions>() },
        { "WeaponToggleCommandSet", (parser, x) => x.WeaponToggleCommandSet = parser.ParseCommandSetReference() },
        { "ModelConditionFlags", (parser, x) => x.ModelConditionFlags = parser.ParseEnumBitArray<ModelConditionFlag>() },
        { "ModelConditionCommandSet", (parser, x) => x.ModelConditionCommandSet = parser.ParseCommandSetReference() }
    };

    public BitArray<WeaponSetConditions> WeaponSetFlags { get; private set; }
    public LazyAssetReference<CommandSet> WeaponToggleCommandSet { get; private set; }
    public BitArray<ModelConditionFlag> ModelConditionFlags { get; private set; }
    public LazyAssetReference<CommandSet> ModelConditionCommandSet { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new MonitorConditionUpdate(gameObject, gameEngine.SimContext, this);
    }
}
