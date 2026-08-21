// ArrowStormUpdate - R14 port (api-freeze-v1 §6 / template v1.1), split verdict per
// research/modules-r13/specs/ArrowStormUpdateModuleData.md.
//
// Behavioral reference: the unpack/prepare/trigger/pack phase machine below is a direct
// translation of generals-gpl's SpecialAbilityUpdate.cpp - the doc block at :106-180 (STEP 2
// UNPACK / 3 PREPARE / 4 TRIGGER / 5 PACK / 6 FINISH), update() at :192-476, and the helpers
// handlePackingProcessing() :642-701, needToPack() :705-721, needToUnpack() :723-739,
// startPacking() :741-792, startUnpacking() :794-819, isWithinStartAbilityRange() :821-899,
// startPreparation() :987-1102, triggerAbilityEffect() :1264-1290, endPreparation()
// :1958-1992, and the inline predicates isPreparationComplete()/isPersistentAbility()/
// resetPreparation() at SpecialAbilityUpdate.h:237-241. The base SpecialAbilityUpdate.cs
// already in this tree (Update/SpecialAbilityUpdate/SpecialAbilityUpdate.cs) is NOT an
// inheritance parent - it carries no [SimState] and a do-nothing Update() - so this is a fresh
// [SimState] class translating the transitions directly, matching the shape of the landed
// sibling ToggleHiddenSpecialAbilityUpdate (identical field vocabulary, identical
// Packed -> Unpacking -> Preparing -> ... -> Packing -> Packed machine).
//
// STEP 1 APPROACH is not modeled: it drives AIUpdateInterface, and the landed AIUpdate is
// float-substrate legacy - touching it from this [SimState] class would cross the Fix64
// quarantine wall. The range gate (isWithinStartAbilityRange) is instead evaluated at the
// driven InitiateIntentToDoSpecialPower seam: a caller out of range is refused rather than
// walked into range (filed F-AS-2).
//
// ApproachRequiresLOS: GPL selects between a plain range test and one filtered by
// PartitionFilterLineOfSight (cpp:876-897 vs :894-896). IPartitionQuery exposes only
// QueryObjectsInRadius/GetVisionRange - no LOS filter or predicate exists on the frozen
// contract. Only the non-LOS branch is modeled; the field is parsed, held, and exposed
// read-only as ApproachRequiresLos (filed F-AS-3, same device as
// ToggleDeploySpecialAbilityUpdateModuleData.IgnoreFacingCheck).
//
// UnpackingVariation: GPL's m_packUnpackVariationFactor is a Real fraction consumed as a
// +/-factor multiplier on unpack/pack time (cpp:745-747, cpp:798-800). BFME's field is an Int
// whose one live value (1) would mean +/-100% under that formula - not a plausible authored
// intent - and the landed sibling with the identical field name and ParseInteger() parse
// (ToggleHiddenSpecialAbilityUpdate) already holds it unmodeled for the same reason. Held here
// too (filed F-AS-1); UnpackTime/PackTime stay deterministic.
//
// Re-initiation while active: GPL re-initialises an in-flight activation (cpp:494-534,
// resetting target/facing/special-object state this port does not model). This port instead
// refuses (_phase != Packed -> false), the landed sibling's stricter guard (filed F-AS-6).
//
// TRIGGER POINT and persistence: triggerAbilityEffect() (cpp:1264-1290) awards
// AwardXPForTriggering to the module's OWN GameObject (not the triggering object - cpp:1272-
// 1279, getObject()->getExperienceTracker(); this deliberately diverges from
// ToggleHiddenSpecialAbilityUpdate's sibling call site, which awards the triggering object -
// see the spec's own note not to "fix" this toward the sibling). GPL documents that a
// persistent ability awards XP on EVERY trigger call, not once per activation (cpp:1270-1271).
// The trigger point itself is exposed as a read-only TriggerCount (reset on each accepted
// InitiateIntentToDoSpecialPower) so it is observable by a test without a weapon effect
// existing - the ArrowStorm shot loop itself is out of scope (see below).
//
// Persistence loop (cpp:361-372; isPersistentAbility() = PersistentPrepTime > 0, h:240;
// resetPreparation() = reload from PersistentPrepTime, h:241): when persistent, the FIRST
// trigger lands PreparationTime frames after Preparing begins, and every subsequent trigger
// lands PersistentPrepTime frames after the previous one - the two spans are read from
// different fields and must not collapse into one. The loop is exited only by Abort() or
// object death; GPL's other exits (player command / target death, cpp:214-262) route through
// AI/target state this port does not model.
//
// Abort() is a second driven seam, translation of onExit(false) (cpp:603-634): no landed
// caller exists yet (same posture as ToggleDeploySpecialAbilityUpdate.Toggle), but it is named
// now because it is the exit path a future shot loop will need to distinguish "aborted" from
// "completed" for the held paralyze fields. It does not go through Packing - GPL sets
// STATE_NONE directly (cpp:623).
//
// ActiveLoopSound fires once per activation on entry to Preparing (GPL's m_prepSoundLoop,
// started in startPreparation() cpp:1099-1101, never restarted by resetPreparation()).
// Stopping it is not modeled - ISimEvents has no audio-handle/remove concept, unlike GPL's
// TheAudio->removeAudioEvent (cpp:610, cpp:1961) - filed F-AS-4.
//
// PARSED, NOT MODELED (audited gap, not invented): WeaponTemplate, TargetRadius,
// ShotsPerTarget, ShotsPerBurst, MaxShots, CanShootEmptyGround, ParalyzeDurationWhenAborted,
// ParalyzeDurationWhenCompleted (plus UnpackingVariation and ApproachRequiresLos above). The
// entire ArrowStorm-specific shot loop and its paralyze tail is BFME-original: no source
// anywhere in generals-gpl, generals-community, or the workbench research tree states how
// ShotsPerTarget relates to ShotsPerBurst, how MaxShots is spent, whether one trigger fires
// one shot or a whole burst, how CanShootEmptyGround changes acquisition, or which exit path
// counts as aborted vs completed for the paralyze split. The one live instance
// (summonedlegolas.ini:1061-1079) is suggestive (70 = 7x10) but is a single data point, not a
// spec - inferring the loop from it is invention. Each held field keeps its parse entry, its
// public getter, and a "// held: <reason>" note; no behavior for any of them may be written
// without a source. When this loop is later specced it composes against
// Logic/Object/Combat/SimWeapon.cs and GameObject.Disable(DisabledType.Paralyzed, ...) - NOT
// Weapon/WeaponTemplate.cs or ParalyzeNugget.cs (both wrong targets per the corrected audit).
//
// Every mutable sim field appears in Xfer exactly once (§3 of the spec); tolerances are the
// field's conformance class at its declaration site (§4).
//
// Frame-arithmetic note (§4 of the spec pins exact boundaries, e.g. "start+5", "start+10"):
// InitiateIntentToDoSpecialPower is a driven seam called BETWEEN logic ticks - Context.CurrentFrame
// there names the next frame Update() has not yet processed, unlike every phase-end computed
// from inside this class's own Update() (mid-tick, where CurrentFrame names the frame currently
// being processed). Using "CurrentFrame + duration" unmodified in both places makes a phase
// entered synchronously from Initiate() (directly, or via a zero-duration cascade through
// Enter*/Trigger that never reaches an Update() tick) take one MORE tick to elapse than the
// same duration entered from inside Update() - the two contexts are not interchangeable.
// PhaseEndFrame's fromInitiate flag compensates by one frame so "N frames" always means N
// Update() ticks regardless of which context started the phase.

using System.Linq;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ArrowStormUpdate : UpdateModule
{
    private readonly ArrowStormUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private ArrowStormPhase _phase;
    private LogicFrame _phaseEndFrame;

    /// <summary>Whether this activation has triggered at least once (drives the prep-vs-
    /// persistent-prep span choice on the next reset; see the file header).</summary>
    private bool _persistentTriggered;

    /// <summary>Triggers fired within the current activation. Reset to zero on each accepted
    /// <see cref="InitiateIntentToDoSpecialPower"/>. The regression fence around the held shot
    /// loop: this counts the trigger point without any weapon effect existing.</summary>
    private int _triggerCount;

    public ArrowStormUpdate(GameObject gameObject, ISimContext context, ArrowStormUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _phase = ArrowStormPhase.Packed;

        // Ticks every frame like the landed sibling family: the phase machine is cheap, and
        // GPL's own calcSleepTime() (h:257-260) returns UPDATE_SLEEP_NONE while active with an
        // acknowledged-but-never-done sleep-between-stages optimisation (cpp:194) - so
        // per-frame ticking is the GPL behavior, not a shortcut.
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public int UnpackingVariation => _data.UnpackingVariation;

    /// <summary>Parsed and held; not currently modeled - see the file-header LOS gap note.</summary>
    public bool ApproachRequiresLos => _data.ApproachRequiresLos;

    // ---- ArrowStorm-specific shot loop and paralyze tail: parsed and held, exposed
    // read-only for the same reason as the two properties above - see the file-header gap
    // note (F-AS-8). No behavior is attached to any of these. ----

    public string WeaponTemplate => _data.WeaponTemplate;
    public int TargetRadius => _data.TargetRadius;
    public int ShotsPerTarget => _data.ShotsPerTarget;
    public int ShotsPerBurst => _data.ShotsPerBurst;
    public int MaxShots => _data.MaxShots;
    public int ParalyzeDurationWhenAborted => _data.ParalyzeDurationWhenAborted;
    public int ParalyzeDurationWhenCompleted => _data.ParalyzeDurationWhenCompleted;
    public bool CanShootEmptyGround => _data.CanShootEmptyGround;

    /// <summary>Triggers fired within the current activation.</summary>
    public int TriggerCount => _triggerCount;

    /// <summary>The module's current phase.</summary>
    public bool IsPacked => _phase == ArrowStormPhase.Packed;

    /// <summary>
    /// Starts the Packed -> Unpacking -> Preparing sequence. Only this module's own special
    /// power (matched by template name) may fire it, only while Packed (no interrupting or
    /// re-triggering an in-flight activation - F-AS-6), only when every
    /// <see cref="ArrowStormUpdateModuleData.RequiredConditions"/> bit is set on this object,
    /// and only when <paramref name="triggeringObject"/> is within
    /// <see cref="ArrowStormUpdateModuleData.StartAbilityRange"/> (gate skipped when
    /// unconfigured or the triggering object is unknown, matching
    /// isWithinStartAbilityRange's "no position, so this step is useless" branch, cpp:864-868).
    /// </summary>
    public bool InitiateIntentToDoSpecialPower(string specialPowerTemplateName, GameObject triggeringObject)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        if (_phase != ArrowStormPhase.Packed)
        {
            return false;
        }

        if (_data.RequiredConditions != null)
        {
            foreach (var flag in _data.RequiredConditions.GetSetBits())
            {
                if (!GameObject.ModelConditionFlags.Get(flag))
                {
                    return false;
                }
            }
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

        _triggerCount = 0;
        _persistentTriggered = false;
        EnterUnpackingOrLater(fromInitiate: true);
        return true;
    }

    /// <summary>
    /// The "stop now" seam (translation of GPL's onExit(false), cpp:603-634): interrupts an
    /// in-flight activation and returns directly to Packed without going through Packing.
    /// Returns false when the module was already Packed (nothing to interrupt).
    /// </summary>
    public bool Abort()
    {
        if (_phase == ArrowStormPhase.Packed)
        {
            return false;
        }

        GameObject.ClearModelConditionState(ModelConditionFlag.Unpacking);
        GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
        _phase = ArrowStormPhase.Packed;
        return true;
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        switch (_phase)
        {
            case ArrowStormPhase.Unpacking:
                if (now >= _phaseEndFrame)
                {
                    EnterPreparingOrLater();
                }
                break;

            case ArrowStormPhase.Preparing:
                if (now >= _phaseEndFrame)
                {
                    Trigger();
                }
                break;

            case ArrowStormPhase.Packing:
                if (now >= _phaseEndFrame)
                {
                    GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
                    _phase = ArrowStormPhase.Packed;
                }
                break;
        }

        return UpdateSleepTime.None;
    }

    private void EnterUnpackingOrLater(bool fromInitiate = false)
    {
        if (_data.UnpackTime.Value > 0)
        {
            _phase = ArrowStormPhase.Unpacking;
            _phaseEndFrame = PhaseEndFrame(_data.UnpackTime, fromInitiate);
            GameObject.SetModelConditionState(ModelConditionFlag.Unpacking);
        }
        else
        {
            EnterPreparingOrLater(fromInitiate);
        }
    }

    private void EnterPreparingOrLater(bool fromInitiate = false)
    {
        GameObject.ClearModelConditionState(ModelConditionFlag.Unpacking);

        _phase = ArrowStormPhase.Preparing;
        _phaseEndFrame = PhaseEndFrame(_data.PreparationTime, fromInitiate);

        if (!string.IsNullOrEmpty(_data.ActiveLoopSound))
        {
            Context.Events.FireAudioEventAtObject(_data.ActiveLoopSound, GameObject.Id);
        }

        // Zero PreparationTime: isPreparationComplete() is immediately true (h:237), so GPL
        // triggers in the same update pass (cpp:468-470). Model that here rather than waiting
        // for the next Update() call.
        if (_data.PreparationTime.Value == 0)
        {
            Trigger(fromInitiate);
        }
    }

    /// <summary>The TRIGGER POINT (triggerAbilityEffect(), cpp:1264-1290), reached when the
    /// Preparing countdown completes.</summary>
    private void Trigger(bool fromInitiate = false)
    {
        _triggerCount++;

        if (_data.AwardXPForTriggering != 0)
        {
            // Awarded to this module's own GameObject, not the triggering object - cpp:1272-
            // 1279; see the file-header note on why this diverges from the landed sibling.
            GameObject.ExperienceTracker.AddExperiencePoints(_data.AwardXPForTriggering);
        }

        if (_data.PersistentPrepTime.Value > 0)
        {
            // Stay in Preparing: the first trigger used PreparationTime, every subsequent one
            // uses PersistentPrepTime (h:240-241) - the two spans must not collapse.
            _persistentTriggered = true;
            _phaseEndFrame = PhaseEndFrame(_data.PersistentPrepTime, fromInitiate);
        }
        else
        {
            EnterPackingOrLater(fromInitiate);
        }
    }

    private void EnterPackingOrLater(bool fromInitiate = false)
    {
        if (_data.PackTime.Value > 0)
        {
            _phase = ArrowStormPhase.Packing;
            _phaseEndFrame = PhaseEndFrame(_data.PackTime, fromInitiate);
            GameObject.SetModelConditionState(ModelConditionFlag.Packing);
        }
        else
        {
            GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
            _phase = ArrowStormPhase.Packed;
        }
    }

    /// <summary>See the frame-arithmetic note above the Xfer walk: compensates the one-frame
    /// gap between Initiate()'s between-ticks call context and every other phase entry's
    /// mid-tick context, so a phase's duration always costs exactly that many Update() calls
    /// regardless of which context started it.</summary>
    private LogicFrame PhaseEndFrame(LogicFrameSpan duration, bool fromInitiate)
    {
        var reference = Context.CurrentFrame;
        if (fromInitiate && reference.Value > 0)
        {
            reference -= 1;
        }

        return reference + duration;
    }

    private enum ArrowStormPhase
    {
        Packed,
        Unpacking,
        Preparing,
        Packing,
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Tolerances (ruling A3): the phase enum, the persistent-triggered flag, and the trigger
    // counter are lifecycle facts, so Exact. The phase-end frame is a timer, so Quantum
    // (ch.2), matching XferFrame's own default.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("PhaseEndFrame", ref _phaseEndFrame);
        xfer.XferBool("PersistentTriggered", ref _persistentTriggered);
        xfer.XferInt("TriggerCount", ref _triggerCount);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class ArrowStormUpdateModuleData : UpdateModuleData
{
    internal static ArrowStormUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ArrowStormUpdateModuleData> FieldParseTable = new IniParseTable<ArrowStormUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        { "StartAbilityRange", (parser, x) => x.StartAbilityRange = parser.ParseFix64() },
        { "UnpackingVariation", (parser, x) => x.UnpackingVariation = parser.ParseInteger() },
        { "UnpackTime", (parser, x) => x.UnpackTime = parser.ParseDurationLogicFrames() },
        { "PreparationTime", (parser, x) => x.PreparationTime = parser.ParseDurationLogicFrames() },
        { "PersistentPrepTime", (parser, x) => x.PersistentPrepTime = parser.ParseDurationLogicFrames() },
        { "PackTime", (parser, x) => x.PackTime = parser.ParseDurationLogicFrames() },
        { "ApproachRequiresLOS", (parser, x) => x.ApproachRequiresLos = parser.ParseBoolean() },
        { "AwardXPForTriggering", (parser, x) => x.AwardXPForTriggering = parser.ParseInteger() },
        { "ActiveLoopSound", (parser, x) => x.ActiveLoopSound = parser.ParseAssetReference() },
        { "WeaponTemplate", (parser, x) => x.WeaponTemplate = parser.ParseIdentifier() },
        { "TargetRadius", (parser, x) => x.TargetRadius = parser.ParseInteger() },
        { "ShotsPerTarget", (parser, x) => x.ShotsPerTarget = parser.ParseInteger() },
        { "ShotsPerBurst", (parser, x) => x.ShotsPerBurst = parser.ParseInteger() },
        { "MaxShots", (parser, x) => x.MaxShots = parser.ParseInteger() },
        { "ParalyzeDurationWhenAborted", (parser, x) => x.ParalyzeDurationWhenAborted = parser.ParseInteger() },
        { "ParalyzeDurationWhenCompleted", (parser, x) => x.ParalyzeDurationWhenCompleted = parser.ParseInteger() },
        { "CanShootEmptyGround", (parser, x) => x.CanShootEmptyGround = parser.ParseBoolean() },
        { "RequiredConditions", (parser, x) => x.RequiredConditions = parser.ParseEnumBitArray<ModelConditionFlag>() }
    };

    public string SpecialPowerTemplate { get; private set; }
    public Fix64 StartAbilityRange { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note (F-AS-1).</summary>
    public int UnpackingVariation { get; private set; }

    public LogicFrameSpan UnpackTime { get; private set; }
    public LogicFrameSpan PreparationTime { get; private set; }
    public LogicFrameSpan PersistentPrepTime { get; private set; }
    public LogicFrameSpan PackTime { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header LOS gap note (F-AS-3).</summary>
    public bool ApproachRequiresLos { get; private set; }

    public int AwardXPForTriggering { get; private set; }
    public string ActiveLoopSound { get; private set; }

    // ---- ArrowStorm-specific shot loop and paralyze tail: PARSED, NOT MODELED (audited gap,
    // not invented). See the file header for the full argument. ----

    /// <summary>held: no source states which weapon the shot loop fires (F-AS-8, shot loop out of scope)</summary>
    public string WeaponTemplate { get; private set; }

    /// <summary>held: no source states target-acquisition radius semantics (F-AS-8)</summary>
    public int TargetRadius { get; private set; }

    /// <summary>held: relationship to ShotsPerBurst is not stated by any source (F-AS-8)</summary>
    public int ShotsPerTarget { get; private set; }

    /// <summary>held: relationship to ShotsPerTarget/MaxShots is not stated by any source (F-AS-8)</summary>
    public int ShotsPerBurst { get; private set; }

    /// <summary>held: budget scope (per-activation/per-burst/lifetime) is not stated by any source (F-AS-8)</summary>
    public int MaxShots { get; private set; }

    /// <summary>held: no source states which exit path counts as "aborted" for this split (F-AS-8)</summary>
    public int ParalyzeDurationWhenAborted { get; private set; }

    /// <summary>held: no source states which exit path counts as "completed" for this split (F-AS-8)</summary>
    public int ParalyzeDurationWhenCompleted { get; private set; }

    /// <summary>held: no source states whether this substitutes ground shots or merely permits them (F-AS-8)</summary>
    public bool CanShootEmptyGround { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public BitArray<ModelConditionFlag> RequiredConditions { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ArrowStormUpdate(gameObject, gameEngine.SimContext, this);
    }
}
