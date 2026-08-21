// ToggleMountedSpecialAbilityUpdate - R14 port off the R13 spec
// (bfme2-workbench/research/modules-r13/specs/ToggleMountedSpecialAbilityUpdateModuleData.md).
//
// generals-gpl carries no ToggleMounted*/MountedTemplate class at all (grep confirms; the spec's
// own §0 records the zero-hit search) - this is a BFME2-only class, same posture as
// ToggleHiddenSpecialAbilityUpdate and SpecialDisguiseUpdate. Its field set nevertheless splits
// cleanly across two sources, per the spec:
//   - The timer/gate vocabulary (SpecialPowerTemplate/UnpackTime/PreparationTime/
//     PersistentPrepTime/PackTime/AwardXPForTriggering/StartAbilityRange) is the exact
//     SpecialAbilityUpdateModuleData field set (generals-gpl SpecialAbilityUpdate.h), already
//     translated for this same phase-machine shape by the landed
//     ToggleHiddenSpecialAbilityUpdate sibling this file's Packed/Unpacking/Prepared/Packing
//     states are modeled on directly.
//   - The swap action itself - "replace myself with a fresh instance of another template, in
//     place" - is generals-gpl ReplaceObjectUpgrade::upgradeImplementation, already landed and
//     audited as ReplaceObjectUpdate.PerformReplace in this same directory. PerformMountSwap
//     below is that same sequence (donor-matrix CreateObjectAt, destroy-before-create ordering,
//     OnBuildComplete pass, PathfindQueueForPath, AwardXPForTriggering at the swap) with no
//     Scatter/ReplaceRadius/ReplaceObject-filter machinery, because this class's vocabulary has
//     none of those fields.
//
// STATE MACHINE: Packed -> Unpacking (UnpackTime) -> Prepared (PreparationTime, extended once
// by PersistentPrepTime if unused - F-TMS-1) -> [manual Trigger] -> Packing (PackTime) ->
// [SWAP: destroy self, create MountedTemplate, award XP] (terminal). A Prepared window that
// times out without a Trigger call auto-packs with no swap and re-arms Packed (the family's own
// cycle, ToggleHiddenSpecialAbilityUpdate's own "auto-packs" behavior). Zero duration on any
// timed stage skips it immediately (the ordinary SAGE "zero means immediate" convention this
// family already uses).
//
// F-TMS-2 (judgment call, spec §1.2): PackTime sits BEFORE the swap (Trigger -> Packing ->
// swap), not after - the object ceases to exist at the swap, so a post-swap Packing phase is
// unreachable and would make PackTime dead data on a live corpus field. PackTime == 0 reduces to
// "swap immediately" either way, so both readings coincide on every corpus object that sets it.
//
// There is no reverse/dismount path in this module (spec §0.1 point 2, §1.4 closing paragraph):
// mount and dismount are a PAIR of objects, each carrying its own
// ToggleMountedSpecialAbilityUpdate naming the OTHER template. Each instance is one-way. Do not
// add a remount branch, a saved previous template, or a two-way toggle.
//
// PerformMountSwap creates a FRESH instance and carries nothing over (spec §0.1 point 3, §1.4
// step 10): no HP, no veterancy, no passengers, no upgrades - the same "fresh instance, no
// carry-over" convention ReplaceObjectUpdate already uses (CreateObjectDie carries health only
// because it has its own explicit TransferPreviousHealth field; this vocabulary has no such
// field).
//
// TriggerInstantlyOnCreate (spec §1.5): implemented via ICreateModule, landed precedent
// RubbleRiseUpdate (one UpdateModule class serving both roles). OnCreate() only arms a flag;
// Update() consumes it one frame later. F-TMS-3 (deliberate, spec §1.5): a synchronous
// swap-inside-OnCreate would re-enter PerformMountSwap from inside its own CreateObjectAt call
// (OnCreate runs synchronously inside CreateObjectAt - RubbleRiseUpdate's own OnCreate doc
// explains this). Deferring by one Update() keeps the swap on the single, already Xfer-safe,
// phase-guarded code path. Residual data-authoring hazard, not guarded by the port: a pair of
// objects that BOTH set TriggerInstantlyOnCreate = Yes would ping-pong forever; no corpus pair
// does this.
//
// PARSED, NOT MODELED (audited gaps, spec §2 - each held field carries its own reason at its
// declaration site below):
//   - OpacityTarget: client rendering; ISimContext is permanently UI-absent.
//   - SynchronizeTimerOnSpecialPower: no landed special-power recharge/timer registry to
//     synchronize against.
//   - CancelDisguiseWhenDismounting: SpecialDisguiseUpdate exposes no cancel/abort seam.
//   - IgnoreFacingCheck: no facing gate exists anywhere in the landed special-ability family.
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
public sealed class ToggleMountedSpecialAbilityUpdate : UpdateModule, ICreateModule
{
    private readonly ToggleMountedSpecialAbilityUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private ToggleMountedPhase _phase;
    private LogicFrame _phaseEndFrame;

    /// <summary>
    /// Whether the one-shot PersistentPrepTime extension has already been consumed for the
    /// current Prepared window (spec §1.2, F-TMS-1 - same one-shot convention as
    /// ToggleHiddenSpecialAbilityUpdate's own _prepExtended).
    /// </summary>
    private bool _prepExtended;

    /// <summary>
    /// The object that initiated the trigger (for AwardXPForTriggering), recorded at
    /// InitiateIntentToDoSpecialPower time and overwritten by a non-null Trigger caller.
    /// Invalid when never triggered, or triggered with no source.
    /// </summary>
    private ObjectId _triggeringObjectId;

    /// <summary>
    /// Set by <see cref="OnCreate"/> when <see cref="ToggleMountedSpecialAbilityUpdateModuleData.TriggerInstantlyOnCreate"/>
    /// is configured; consumed one Update() later (spec §1.5, F-TMS-3 - defers off OnCreate's
    /// synchronous-construction context to avoid re-entering PerformMountSwap from inside its
    /// own CreateObjectAt call).
    /// </summary>
    private bool _autoTriggerArmed;

    public ToggleMountedSpecialAbilityUpdate(GameObject gameObject, ISimContext context, ToggleMountedSpecialAbilityUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _phase = ToggleMountedPhase.Packed;

        // Ticks every frame like the rest of this SpecialPowerTemplate-gated family
        // (ReplaceObjectUpdate, ToggleHiddenSpecialAbilityUpdate): the phase machine is cheap
        // and this keeps the wake-scheduling shape identical to those landed exemplars.
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// Spec §1.5: arms the auto-trigger flag when configured. Nothing else - the sequence
    /// itself is driven from Update() one frame later (F-TMS-3).
    /// </summary>
    public void OnCreate()
    {
        if (_data.TriggerInstantlyOnCreate)
        {
            _autoTriggerArmed = true;
        }
    }

    public void OnBuildComplete() { }

    /// <summary>Test/diagnostic visibility only (internal, per <c>InternalsVisibleTo</c> -
    /// no production caller reads this): the spec's own contract-test plan (§4) asserts
    /// directly on the phase machine rather than only its observable side effects.</summary>
    internal ToggleMountedPhase Phase => _phase;

    /// <summary>
    /// Starts the Packed -> Unpacking -> Prepared sequence. Only this module's own special
    /// power (matched by template name) may fire it, only while Packed (no interrupting or
    /// re-triggering an in-flight cycle), and only when <paramref name="triggeringObject"/> is
    /// within <see cref="ToggleMountedSpecialAbilityUpdateModuleData.StartAbilityRange"/> (gate
    /// skipped when unconfigured or the triggering object is unknown - same shape as
    /// ReplaceObjectUpdate's and ToggleHiddenSpecialAbilityUpdate's identical field).
    /// </summary>
    public bool InitiateIntentToDoSpecialPower(string specialPowerTemplateName, GameObject triggeringObject)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        if (_phase != ToggleMountedPhase.Packed)
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

        _triggeringObjectId = triggeringObject?.Id ?? ObjectId.Invalid;

        EnterUnpackingOrLater();
        return true;
    }

    /// <summary>
    /// Manually fires the swap while Prepared: records the triggering object (overwriting the
    /// initiate-time id if non-null - the credit at swap time uses whichever is freshest) and
    /// begins the pack-then-swap sequence. False (no-op) outside the Prepared phase.
    /// </summary>
    public bool Trigger(GameObject triggeringObject)
    {
        if (_phase != ToggleMountedPhase.Prepared)
        {
            return false;
        }

        if (triggeringObject != null)
        {
            _triggeringObjectId = triggeringObject.Id;
        }

        EnterPackingToSwapOrNow();
        return true;
    }

    public override UpdateSleepTime Update()
    {
        // Spec §1.5: consumed one Update() after arming (F-TMS-3), never inside OnCreate
        // itself. Each branch runs at most once per tick and always leaves _phase on a
        // phaseEndFrame strictly ahead of `now` (or terminal), so falling through into the
        // switch below on the same tick never double-processes a transition.
        if (_autoTriggerArmed)
        {
            if (_phase == ToggleMountedPhase.Packed)
            {
                InitiateIntentToDoSpecialPower(_data.SpecialPowerTemplate, null);
            }
            else if (_phase == ToggleMountedPhase.Prepared)
            {
                _autoTriggerArmed = false;
                Trigger(null);
            }
        }

        var now = Context.CurrentFrame;

        switch (_phase)
        {
            case ToggleMountedPhase.Unpacking:
                if (now >= _phaseEndFrame)
                {
                    EnterPreparedOrLater();
                }
                break;

            case ToggleMountedPhase.Prepared:
                if (now >= _phaseEndFrame)
                {
                    if (!_prepExtended && _data.PersistentPrepTime.Value > 0)
                    {
                        _prepExtended = true;
                        _phaseEndFrame = now + _data.PersistentPrepTime;
                    }
                    else
                    {
                        // The window closed with no Trigger call: auto-pack, no swap, no XP -
                        // the family's own auto-pack cycle (ToggleHiddenSpecialAbilityUpdate's
                        // own EnterPackingOrLater, spec §1.2).
                        EnterPackingDownOrLater();
                    }
                }
                break;

            case ToggleMountedPhase.PackingDown:
                if (now >= _phaseEndFrame)
                {
                    GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
                    _phase = ToggleMountedPhase.Packed;
                }
                break;

            case ToggleMountedPhase.PackingToSwap:
                if (now >= _phaseEndFrame)
                {
                    PerformMountSwap();
                }
                break;
        }

        return UpdateSleepTime.None;
    }

    private void EnterUnpackingOrLater()
    {
        if (_data.UnpackTime.Value > 0)
        {
            _phase = ToggleMountedPhase.Unpacking;
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
            _phase = ToggleMountedPhase.Prepared;
            _phaseEndFrame = Context.CurrentFrame + _data.PreparationTime;
            _prepExtended = false;
        }
        else
        {
            // Nothing to prepare, so there is no window in which Trigger() could ever be
            // called: fall through to the pack/Packed path with no swap (spec §1.2, the
            // "EnterPreparedOrLater" zero-duration reasoning cited verbatim from
            // ToggleHiddenSpecialAbilityUpdate).
            EnterPackingDownOrLater();
        }
    }

    /// <summary>
    /// The NO-SWAP pack-down path: reached only when the Prepared window closes (or is never
    /// entered at all) without a real <see cref="Trigger"/> call. Spends
    /// <see cref="ToggleMountedSpecialAbilityUpdateModuleData.PackTime"/> exactly like
    /// <see cref="EnterPackingToSwapOrNow"/> - same field, same convention - but always lands
    /// back on <see cref="ToggleMountedPhase.Packed"/>, never on a swap.
    /// </summary>
    private void EnterPackingDownOrLater()
    {
        if (_data.PackTime.Value > 0)
        {
            _phase = ToggleMountedPhase.PackingDown;
            _phaseEndFrame = Context.CurrentFrame + _data.PackTime;
            GameObject.SetModelConditionState(ModelConditionFlag.Packing);
        }
        else
        {
            GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
            _phase = ToggleMountedPhase.Packed;
        }
    }

    /// <summary>
    /// The SWAP-BOUND pack path: reached only from a real <see cref="Trigger"/> call (manual
    /// or auto). F-TMS-2 (spec §1.2): PackTime sits before the swap - the object ceases to
    /// exist at the swap, so a post-swap Packing phase is unreachable and would make PackTime
    /// dead data on the live corpus field that authors PackTime as "time spent before the hop
    /// completes". PackTime == 0 reduces to "swap immediately".
    /// </summary>
    private void EnterPackingToSwapOrNow()
    {
        if (_data.PackTime.Value > 0)
        {
            _phase = ToggleMountedPhase.PackingToSwap;
            _phaseEndFrame = Context.CurrentFrame + _data.PackTime;
            GameObject.SetModelConditionState(ModelConditionFlag.Packing);
        }
        else
        {
            PerformMountSwap();
        }
    }

    /// <summary>
    /// GPL ReplaceObjectUpgrade::upgradeImplementation, via the landed
    /// ReplaceObjectUpdate.PerformReplace translation (spec §1.4): destroy self, create the
    /// MountedTemplate at the same position/team, run its OnBuildComplete pass, queue it for
    /// pathfinding, and award any triggering XP - all at the moment of the swap, not before.
    /// </summary>
    private void PerformMountSwap()
    {
        _phase = ToggleMountedPhase.Swapped;
        GameObject.ClearModelConditionState(ModelConditionFlag.Packing);

        var replacementDefinition = _data.MountedTemplate?.Value;
        if (replacementDefinition == null)
        {
            // spec §1.4 step 2 - GPL's own findTemplate-returned-NULL guard. Live in the
            // corpus (Eowyn's on-foot half carries no MountedTemplate at all): a no-op, not a
            // crash.
            return;
        }

        var me = GameObject;
        var owner = me.Owner;
        var team = me.Team;

        // GPL order: destroy the original FIRST, then create the replacement - legal because
        // IGameLogic.DestroyObject documents same-frame visibility, so `me` stays a valid
        // position/team donor for the CreateObjectAt call below.
        Context.GameLogic.DestroyObject(me);

        // Donor-matrix overload: exact position AND rotation copy (GPL setTransformMatrix),
        // team stamped before the replacement's ICreateModule.OnCreate() pass runs.
        var replacement = Context.GameLogic.CreateObjectAt(replacementDefinition, owner, team, me);
        if (replacement == null)
        {
            return;
        }

        foreach (var createModule in replacement.FindBehaviors<ICreateModule>())
        {
            createModule.OnBuildComplete();
        }

        Context.GameLogic.PathfindQueueForPath(replacement.Id);

        if (_data.AwardXPForTriggering != 0 && _triggeringObjectId.IsValid)
        {
            var triggeringObject = Context.GameLogic.GetObjectById(_triggeringObjectId);
            triggeringObject?.ExperienceTracker.AddExperiencePoints(_data.AwardXPForTriggering);
        }
    }

    internal enum ToggleMountedPhase
    {
        Packed,
        Unpacking,
        Prepared,

        /// <summary>Reached only from the Prepared window closing with no <see cref="Trigger"/>
        /// call: PackTime elapses, then Packed with no swap (<see cref="EnterPackingDownOrLater"/>).</summary>
        PackingDown,

        /// <summary>Reached only from a real <see cref="Trigger"/> call: PackTime elapses, then
        /// <see cref="PerformMountSwap"/> (<see cref="EnterPackingToSwapOrNow"/>).</summary>
        PackingToSwap,

        /// <summary>Terminal: the swap has run (or ran into a no-op MountedTemplate). A second
        /// swap can never happen from this state.</summary>
        Swapped,
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Tolerances (ruling A3): the phase enum, the extension flag, the triggering-object
    // identity, and the auto-trigger arm flag are lifecycle/identity facts, so Exact. The
    // phase-end frame is a timer, so Quantum (ch.2), matching XferFrame's own default.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("PhaseEndFrame", ref _phaseEndFrame);
        xfer.XferBool("PrepExtended", ref _prepExtended);
        xfer.XferObjectId("TriggeringObjectId", ref _triggeringObjectId);
        xfer.XferBool("AutoTriggerArmed", ref _autoTriggerArmed);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class ToggleMountedSpecialAbilityUpdateModuleData : UpdateModuleData
{
    internal static ToggleMountedSpecialAbilityUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ToggleMountedSpecialAbilityUpdateModuleData> FieldParseTable = new IniParseTable<ToggleMountedSpecialAbilityUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        { "UnpackTime", (parser, x) => x.UnpackTime = parser.ParseDurationLogicFrames() },
        { "PreparationTime", (parser, x) => x.PreparationTime = parser.ParseDurationLogicFrames() },
        { "PersistentPrepTime", (parser, x) => x.PersistentPrepTime = parser.ParseDurationLogicFrames() },
        { "PackTime", (parser, x) => x.PackTime = parser.ParseDurationLogicFrames() },
        { "AwardXPForTriggering", (parser, x) => x.AwardXPForTriggering = parser.ParseInteger() },
        { "OpacityTarget", (parser, x) => x.OpacityTarget = parser.ParseFix64() },
        { "TriggerInstantlyOnCreate", (parser, x) => x.TriggerInstantlyOnCreate = parser.ParseBoolean() },
        { "CancelDisguiseWhenDismounting", (parser, x) => x.CancelDisguiseWhenDismounting = parser.ParseBoolean() },
        { "StartAbilityRange", (parser, x) => x.StartAbilityRange = parser.ParseFix64() },
        { "MountedTemplate", (parser, x) => x.MountedTemplate = parser.ParseObjectReference() },
        { "SynchronizeTimerOnSpecialPower", (parser, x) => x.SynchronizeTimerOnSpecialPower = parser.ParseAssetReferenceArray() },
        { "IgnoreFacingCheck", (parser, x) => x.IgnoreFacingCheck = parser.ParseBoolean() },
    };

    public string SpecialPowerTemplate { get; private set; }
    public LogicFrameSpan UnpackTime { get; private set; }
    public LogicFrameSpan PreparationTime { get; private set; }
    public LogicFrameSpan PersistentPrepTime { get; private set; }
    public LogicFrameSpan PackTime { get; private set; }
    public int AwardXPForTriggering { get; private set; }

    /// <summary>held: fade-during-transition is client rendering; ISimContext is permanently
    /// UI-absent. Parsed as Fix64 (not consumed) so it can never trip SIMCORE001.</summary>
    public Fix64 OpacityTarget { get; private set; }

    public bool TriggerInstantlyOnCreate { get; private set; }

    /// <summary>held: SpecialDisguiseUpdate exposes no cancel/abort seam; wiring one is a
    /// cross-module design decision, not a translation.</summary>
    public bool CancelDisguiseWhenDismounting { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public Fix64 StartAbilityRange { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public LazyAssetReference<ObjectDefinition> MountedTemplate { get; private set; }

    /// <summary>held: no landed special-power recharge/timer registry exists to synchronize
    /// against.</summary>
    [AddedIn(SageGame.Bfme2)]
    public string[] SynchronizeTimerOnSpecialPower { get; private set; }

    /// <summary>held: no facing gate exists anywhere in the landed special-ability family, so
    /// there is nothing to ignore.</summary>
    [AddedIn(SageGame.Bfme2)]
    public bool IgnoreFacingCheck { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ToggleMountedSpecialAbilityUpdate(gameObject, gameEngine.SimContext, this);
    }
}
