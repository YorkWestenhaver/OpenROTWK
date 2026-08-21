// ModelConditionSpecialAbilityUpdate - R13 port (research/modules-r13/specs/
// ModelConditionSpecialAbilityUpdateModuleData.md).
//
// Behavioral reference: generals-gpl GeneralsMD SpecialAbilityUpdate.cpp/.h. Unlike this
// directory's sibling ToggleHiddenSpecialAbilityUpdate (which has no GPL sibling and invented
// its own state machine), this class's field set is a near-exact subset of GPL's own
// SpecialAbilityUpdateModuleData fields, so this port follows GPL's actual, documented state
// machine rather than the sibling's engineering choices (spec §0/§1):
//
//   Packed --[InitiateIntentToDoSpecialPower]--> Unpacking(UnpackTime)
//          --> Prepared(PreparationTime, counts down automatically every Update - NOT a
//              manual-trigger window, GPL update() lines 343-410 / isPreparationComplete())
//          --[PreparationTime elapses]--> effect fires automatically (triggerAbilityEffect)
//          --[PersistentPrepTime > 0]--> loop back to Prepared(PersistentPrepTime), repeat
//              forever (GPL isPersistentAbility()/resetPreparation(), a REPEATING re-trigger
//              loop - not the one-shot window extension ToggleHidden's own header documents
//              for its differently-shaped field of the same name, spec §1.1)
//          --[PersistentPrepTime == 0]--> Packing(PackTime) --> Packed
//
// Zero-skip convention (GPL needToUnpack()/needToPack(), spec §1.1): a zero duration on
// UnpackTime/PackTime skips that phase entirely and moves straight through, same "zero means
// immediate" convention this update-module family already uses elsewhere.
//
// Trigger effect payload (GPL triggerAbilityEffect(), spec §1.1 point "Trigger effect
// payload"): awards AwardXPForTriggering to the triggering object's ExperienceTracker and
// plays TriggerSound at this object's position. The Generals-specific per-SpecialPowerType
// switch body (capture building, steal cash, ...) is not ported - none of those fields exist
// on this BFME class (same "don't invent machinery the field list doesn't support" posture as
// ToggleDeploySpecialAbilityUpdateModuleData's own spec).
//
// LoseStealthOnTrigger / PreTriggerUnstealthTime (GPL handlePackingProcessing(), spec §1.2):
// while Unpacking (the only phase GPL drives m_animFrames for outside Packing) and the
// remaining Unpacking time drops below PreTriggerUnstealthTime, the object's StealthUpdate
// sibling module (if any) is told MarkAsDetected(). No-op if the object carries no
// StealthUpdate module.
//
// Idle sleep (GPL calcSleepTime(), spec §1.1): this class has no AlwaysValidateSpecialObjects
// field, so the module sleeps forever while Packed and wakes to UpdateSleepTime.None for the
// duration of an active cycle - unlike ToggleHidden's own engineering choice to tick every
// frame unconditionally.
//
// PARSED, NOT MODELED (audited gaps, not invented - spec §1.3-§1.7/§5, same "parsed and held"
// posture as every sibling in this batch):
//   - GenerateTerror / EmotionPulseRadius / ObjectFilter / GenerateUncontrollableFear: no
//     EmotionPulseInterval field exists on this class (unlike sibling
//     RadiateFearUpdateModuleData, which has both), so no periodic-pulse cadence can be
//     derived without inventing one. Tracked for whichever task composes
//     RadiateFearUpdateModuleData's own mechanism (spec §1.3/§4).
//   - WhichSpecialPower: no landed ordinal-indexed special-power selection surface exists on
//     GameObject/ISimContext to model it against (spec §1.4).
//   - DisableWhenWearingTheRing: no landed "wearing the Ring" state exists anywhere in the
//     engine to gate against (spec §1.5).
//   - UnpackingVariation: identical, already-accepted gap on the landed sibling
//     ToggleHiddenSpecialAbilityUpdateModuleData - this port drives the plain
//     Unpacking/Packing flags only (spec §1.6).
//   - MustFinishAbility: no landed order-interruption/command seam exists on this module
//     family to gate against (spec §1.7).
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4). NOTE (R13 finding, not in the spec's own
// Xfer table): the triggering object must be remembered from InitiateIntentToDoSpecialPower
// until the automatic trigger fires (which can be many frames later, and can repeat under
// PersistentPrepTime) so AwardXPForTriggering is credited to the right object each time - the
// spec's §2 Xfer table lists only _phase/_phaseEndFrame, but a third field
// (_triggeringObjectId) is required for this to be correct and SIMCORE011-clean; it follows
// the exact _triggeringObjectId idiom already landed on ReplaceObjectUpdate.cs.

using System.Linq;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ModelConditionSpecialAbilityUpdate : UpdateModule
{
    private readonly ModelConditionSpecialAbilityUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private Phase _phase;
    private LogicFrame _phaseEndFrame;

    /// <summary>
    /// The object that initiated the trigger (for AwardXPForTriggering), as reported to
    /// <see cref="InitiateIntentToDoSpecialPower"/>. Invalid when never triggered, or
    /// triggered with no source. Persists across PersistentPrepTime repeats (see file header).
    /// </summary>
    private ObjectId _triggeringObjectId;

    public ModelConditionSpecialAbilityUpdate(GameObject gameObject, ISimContext context, ModelConditionSpecialAbilityUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _phase = Phase.Packed;

        // GPL calcSleepTime(): UPDATE_SLEEP_FOREVER while Packed, no AlwaysValidateSpecialObjects
        // field on this class to except from that (spec §1.1/§2).
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note (spec §1.4).</summary>
    public int WhichSpecialPower => _data.WhichSpecialPower;

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note (spec §1.5).</summary>
    public bool DisableWhenWearingTheRing => _data.DisableWhenWearingTheRing;

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note (spec §1.6).</summary>
    public int UnpackingVariation => _data.UnpackingVariation;

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note (spec §1.7).</summary>
    public bool MustFinishAbility => _data.MustFinishAbility;

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note (spec §1.3).</summary>
    public bool GenerateTerror => _data.GenerateTerror;

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note (spec §1.3).</summary>
    public Fix64 EmotionPulseRadius => _data.EmotionPulseRadius;

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note (spec §1.3).</summary>
    public bool GenerateUncontrollableFear => _data.GenerateUncontrollableFear;

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note (spec §1.3).</summary>
    public ObjectFilter ObjectFilter => _data.ObjectFilter;

    /// <summary>
    /// Starts the Packed -> Unpacking -> Prepared sequence. Only this module's own special
    /// power (matched by template name) may fire it, and only while Packed (no re-triggering
    /// an in-flight cycle, spec §1.8). This class has no StartAbilityRange field, so no range
    /// gate is applied; <paramref name="triggeringObject"/> is kept for call-site uniformity
    /// with the sibling family and is only consumed for the XP award at trigger time.
    ///
    /// Settles synchronously through any run of zero-duration phases before returning (GPL's
    /// own zero-skip convention, spec §1.1) - a fully zero-duration configuration completes the
    /// whole Packed -> ... -> Packed cycle, effect included, inside this one call with no
    /// engine tick required (contract-test case 4).
    /// </summary>
    public bool InitiateIntentToDoSpecialPower(string specialPowerTemplateName, GameObject triggeringObject)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        if (_phase != Phase.Packed)
        {
            return false;
        }

        _triggeringObjectId = triggeringObject?.Id ?? ObjectId.Invalid;

        var now = Context.CurrentFrame;

        if (_data.UnpackTime.Value > 0)
        {
            _phase = Phase.Unpacking;
            _phaseEndFrame = now + _data.UnpackTime;
            GameObject.SetModelConditionState(ModelConditionFlag.Unpacking);
        }
        else
        {
            EnterPreparedPhase(now);
        }

        Settle(now);

        SetWakeFrame(_phase == Phase.Packed ? UpdateSleepTime.Forever : UpdateSleepTime.None);
        return true;
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        if (_phase == Phase.Unpacking)
        {
            CheckPreTriggerUnstealth(now);
        }

        Settle(now);

        return _phase == Phase.Packed ? UpdateSleepTime.Forever : UpdateSleepTime.None;
    }

    /// <summary>
    /// Advances the phase machine past every boundary already reached as of
    /// <paramref name="now"/>, in a loop - the shared engine for both a normal per-tick
    /// <see cref="Update"/> call and the synchronous zero-duration cascade triggered from
    /// <see cref="InitiateIntentToDoSpecialPower"/>. Terminates because a repeat of Prepared
    /// (the only phase that can re-enter itself) only happens when PersistentPrepTime is
    /// nonzero, which pushes _phaseEndFrame strictly past `now`.
    /// </summary>
    private void Settle(LogicFrame now)
    {
        while (true)
        {
            switch (_phase)
            {
                case Phase.Unpacking:
                    if (now >= _phaseEndFrame)
                    {
                        EnterPreparedPhase(now);
                        continue;
                    }
                    return;

                case Phase.Prepared:
                    if (now >= _phaseEndFrame)
                    {
                        TriggerAbilityEffect();

                        if (_data.PersistentPrepTime.Value > 0)
                        {
                            // GPL isPersistentAbility()/resetPreparation(): a repeating
                            // re-entry into Prepared, not a one-shot extension (spec §1.1).
                            _phaseEndFrame = now + _data.PersistentPrepTime;
                        }
                        else
                        {
                            EnterPackingPhase(now);
                        }
                        continue;
                    }
                    return;

                case Phase.Packing:
                    if (now >= _phaseEndFrame)
                    {
                        GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
                        _phase = Phase.Packed;
                        continue;
                    }
                    return;

                default: // Phase.Packed
                    return;
            }
        }
    }

    /// <summary>
    /// GPL handlePackingProcessing() lines 684-693 (spec §1.2): while Unpacking and the
    /// remaining animation time drops below PreTriggerUnstealthTime, mark the object detected
    /// via its StealthUpdate sibling (no-op if the object carries none).
    /// </summary>
    private void CheckPreTriggerUnstealth(LogicFrame now)
    {
        if (!_data.LoseStealthOnTrigger)
        {
            return;
        }

        var remaining = _phaseEndFrame - now;
        if (remaining < _data.PreTriggerUnstealthTime)
        {
            var stealth = GameObject.BehaviorModules.OfType<StealthUpdate>().FirstOrDefault();
            stealth?.MarkAsDetected();
        }
    }

    /// <summary>
    /// GPL triggerAbilityEffect() (spec §1.1/§2): the two GPL-generic effects this BFME class's
    /// own field list supports - award AwardXPForTriggering to the triggering object, and play
    /// TriggerSound at this object's position. The Generals-specific per-SpecialPowerType
    /// switch body is not ported (see file header).
    /// </summary>
    private void TriggerAbilityEffect()
    {
        if (_data.AwardXPForTriggering != 0 && _triggeringObjectId.IsValid)
        {
            var triggeringObject = Context.GameLogic.GetObjectById(_triggeringObjectId);
            triggeringObject?.ExperienceTracker.AddExperiencePoints(_data.AwardXPForTriggering);
        }

        if (!string.IsNullOrEmpty(_data.TriggerSound))
        {
            Context.Events.FireAudioEventAtObject(_data.TriggerSound, GameObject.Id);
        }
    }

    /// <summary>
    /// Enters Prepared, counting down from <paramref name="now"/> (even a zero-length window
    /// enters "already complete", picked up by the very next <see cref="Settle"/> loop
    /// iteration - GPL's own isPreparationComplete() is simply "!m_prepFrames", so a zero
    /// PreparationTime is immediately-complete, not skipped).
    /// </summary>
    private void EnterPreparedPhase(LogicFrame now)
    {
        GameObject.ClearModelConditionState(ModelConditionFlag.Unpacking);
        _phase = Phase.Prepared;
        _phaseEndFrame = now + _data.PreparationTime;
    }

    private void EnterPackingPhase(LogicFrame now)
    {
        if (_data.PackTime.Value > 0)
        {
            _phase = Phase.Packing;
            _phaseEndFrame = now + _data.PackTime;
            GameObject.SetModelConditionState(ModelConditionFlag.Packing);
        }
        else
        {
            GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
            _phase = Phase.Packed;
        }
    }

    private enum Phase
    {
        Packed,
        Unpacking,
        Prepared,
        Packing,
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Tolerances (ruling A3): the phase enum and the triggering-object identity are
    // lifecycle/identity facts, so Exact. The phase-end frame is a timer, so Quantum (ch.2),
    // matching XferFrame's own default.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("PhaseEndFrame", ref _phaseEndFrame);
        xfer.XferObjectId("TriggeringObjectId", ref _triggeringObjectId);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class ModelConditionSpecialAbilityUpdateModuleData : UpdateModuleData
{
    internal static ModelConditionSpecialAbilityUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<ModelConditionSpecialAbilityUpdateModuleData> FieldParseTable = new IniParseTable<ModelConditionSpecialAbilityUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        { "UnpackingVariation", (parser, x) => x.UnpackingVariation = parser.ParseInteger() },
        { "UnpackTime", (parser, x) => x.UnpackTime = parser.ParseDurationLogicFrames() },
        { "PreparationTime", (parser, x) => x.PreparationTime = parser.ParseDurationLogicFrames() },
        { "PersistentPrepTime", (parser, x) => x.PersistentPrepTime = parser.ParseDurationLogicFrames() },
        { "PackTime", (parser, x) => x.PackTime = parser.ParseDurationLogicFrames() },
        { "AwardXPForTriggering", (parser, x) => x.AwardXPForTriggering = parser.ParseInteger() },
        { "GenerateTerror", (parser, x) => x.GenerateTerror = parser.ParseBoolean() },
        { "EmotionPulseRadius", (parser, x) => x.EmotionPulseRadius = parser.ParseFix64() },
        { "DisableWhenWearingTheRing", (parser, x) => x.DisableWhenWearingTheRing = parser.ParseBoolean() },
        { "WhichSpecialPower", (parser, x) => x.WhichSpecialPower = parser.ParseInteger() },
        { "ObjectFilter", (parser, x) => x.ObjectFilter = ObjectFilter.Parse(parser) },
        { "TriggerSound", (parser, x) => x.TriggerSound = parser.ParseAssetReference() },
        { "MustFinishAbility", (parser, x) => x.MustFinishAbility = parser.ParseBoolean() },
        { "LoseStealthOnTrigger", (parser, x) => x.LoseStealthOnTrigger = parser.ParseBoolean() },
        { "PreTriggerUnstealthTime", (parser, x) => x.PreTriggerUnstealthTime = parser.ParseDurationLogicFrames() },
        { "GenerateUncontrollableFear", (parser, x) => x.GenerateUncontrollableFear = parser.ParseBoolean() }
    };

    public string SpecialPowerTemplate { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public int UnpackingVariation { get; private set; }

    public LogicFrameSpan UnpackTime { get; private set; }
    public LogicFrameSpan PreparationTime { get; private set; }
    public LogicFrameSpan PersistentPrepTime { get; private set; }
    public LogicFrameSpan PackTime { get; private set; }
    public int AwardXPForTriggering { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public bool GenerateTerror { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public Fix64 EmotionPulseRadius { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public bool DisableWhenWearingTheRing { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public int WhichSpecialPower { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    [AddedIn(SageGame.Bfme2)]
    public ObjectFilter ObjectFilter { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public string TriggerSound { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    [AddedIn(SageGame.Bfme2Rotwk)]
    public bool MustFinishAbility { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public bool LoseStealthOnTrigger { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public LogicFrameSpan PreTriggerUnstealthTime { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    [AddedIn(SageGame.Bfme2Rotwk)]
    public bool GenerateUncontrollableFear { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ModelConditionSpecialAbilityUpdate(gameObject, gameEngine.SimContext, this);
    }
}
