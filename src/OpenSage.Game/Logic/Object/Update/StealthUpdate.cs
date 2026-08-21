// StealthUpdate - Round-9 port (experiment-round-4 §4.1, template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD StealthUpdate.cpp/.h (GPL semantics only;
// this is fresh code against the frozen contract). The mapped state machine:
//   - not enabled  => sleep forever (GPL calcSleepTime()).
//   - allowedToStealth() gates on the CAN_STEALTH status bit plus the "forbidden condition"
//     set. GPL keys the forbidden set off m_stealthLevel status bits (firing / moving /
//     using-ability / taking-damage). The BFME2/OpenSAGE data model expresses the same gate
//     as StealthForbiddenConditions, a set of ModelConditionFlags that S1 maintains
//     (IsFiringWeapon, Moving, ...). We consume that set - the landed S1 status - rather than
//     re-deriving it from float velocity or weapon internals.
//   - become-stealthed timer: once allowed, wait until stealthAllowedFrame, then set the
//     STEALTHED status. Whenever disallowed, re-arm stealthAllowedFrame = now + StealthDelay
//     and clear STEALTHED (GPL update() lines 741-776).
//   - detection: STEALTHED-but-DETECTED is a separate timer (m_detectionExpiresFrame). While
//     it is in the future the DETECTED status is set; when it lapses the status clears. The
//     timer is armed externally through MarkAsDetected - the seam a ported StealthDetectorUpdate
//     (the natural pair) drives off the S3 partition/vision scan (GPL update() lines 778-808 +
//     markAsDetected()).
//   - temporary grant: GrantedBySpecialPower units start asleep and are switched on by
//     ReceiveGrant (GPL receiveGrant()); a nonzero grant counts down and self-disables.
//
// Scoped out, recorded as findings in research/modules-r9/StealthUpdate.md (no determinism
// impact - all are client output or need a not-yet-ported seam):
//   - SoundStealthOn / SoundStealthOff and the MESSAGE:StealthNeutralized EVA event are client
//     outputs; ISimContext has no seam for a top-level ObjectDefinition sound or an EVA
//     message, and adding one touches shared context files (merge-hygiene). Deferred (SU-1).
//   - the whole disguise subsystem (DisguisesAsTeam, DisguiseFX, transition frames, model /
//     template swap, pulse-phase opacity) is client drawable work; the retail runtime class
//     never modelled it here either. Deferred (SU-2).
//   - RevealDistanceFromTarget / DetectedByAnyoneRange need an AI-current-victim seam and a
//     Fix64 distance query; MoveThresholdSpeed is a float velocity threshold subsumed by the
//     Moving forbidden condition. Parsed, not acted on (SU-3).
//
// Every mutable sim field appears in Xfer exactly once (api-freeze-v1 §3); tolerances are the
// field's conformance class at its declaration site (§4 / amendment A3).

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class StealthUpdate : UpdateModule
{
    private readonly StealthUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Earliest frame the object may become stealthed; re-armed whenever a forbidden
    /// condition holds (GPL m_stealthAllowedFrame).</summary>
    private LogicFrame _stealthAllowedFrame;

    /// <summary>Frame until which the object is detected-while-stealthed (GPL
    /// m_detectionExpiresFrame). Zero = never detected.</summary>
    private LogicFrame _detectionExpiresFrame;

    /// <summary>Whether the update runs at all (GPL m_enabled). GrantedBySpecialPower units
    /// start disabled and are enabled by <see cref="ReceiveGrant"/>.</summary>
    private bool _enabled;

    /// <summary>Frames remaining on a temporary stealth grant; zero = no active countdown
    /// (GPL m_framesGranted).</summary>
    private LogicFrameSpan _framesGranted;

    public StealthUpdate(GameObject gameObject, ISimContext context, StealthUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        _stealthAllowedFrame = Context.CurrentFrame + _data.StealthDelay;

        // GPL: enabled unless this is a disguise unit (whose enable is manual).
        _enabled = !_data.DisguisesAsTeam;

        // GPL ctor: innate-stealth units carry the CAN_STEALTH status so other code can test
        // one bit instead of reaching into this module.
        if (_data.InnateStealth)
        {
            GameObject.SetObjectStatus(ObjectStatus.CanStealth, true);
        }

        // GPL ctor: special-power grants start asleep (switched on by ReceiveGrant); everything
        // else starts awake.
        SetWakeFrame(_data.GrantedBySpecialPower ? UpdateSleepTime.Forever : UpdateSleepTime.None);
    }

    /// <summary>
    /// GPL receiveGrant(): externally switch a temporary/special-power stealth on or off. This
    /// is the seam a special-power module drives; <paramref name="frames"/> is the grant
    /// duration (zero = until revoked). Kept public so the special-power port consumes this
    /// verb rather than this module's internals.
    /// </summary>
    public void ReceiveGrant(bool active, LogicFrameSpan frames = default)
    {
        _enabled = active;

        if (active)
        {
            GameObject.SetObjectStatus(ObjectStatus.CanStealth, true);
            GameObject.SetObjectStatus(ObjectStatus.Stealthed, true);
            _stealthAllowedFrame = Context.CurrentFrame;
            _framesGranted = frames;
            SetWakeFrame(UpdateSleepTime.None);
        }
        else
        {
            GameObject.SetObjectStatus(ObjectStatus.CanStealth, false);
            GameObject.SetObjectStatus(ObjectStatus.Stealthed, false);
            _stealthAllowedFrame = new LogicFrame(UpdateSleepTime.SleepForever);
            _framesGranted = LogicFrameSpan.Zero;
        }
    }

    /// <summary>
    /// GPL markAsDetected(): arm the detected-while-stealthed timer. Called by a detector (the
    /// natural pair StealthDetectorUpdate, once ported) and by reveal rules. A zero
    /// <paramref name="numFrames"/> uses the module's StealthDelay (GPL default); a nonzero
    /// value only ever extends the timer, never shortens it.
    /// </summary>
    public void MarkAsDetected(LogicFrameSpan numFrames = default)
    {
        var now = Context.CurrentFrame;

        if (numFrames == LogicFrameSpan.Zero)
        {
            _detectionExpiresFrame = now + _data.StealthDelay;
        }
        else if (_detectionExpiresFrame < now + numFrames)
        {
            _detectionExpiresFrame = now + numFrames;
        }

        // Make sure we wake to process the detection this frame.
        if (_enabled)
        {
            SetWakeFrame(UpdateSleepTime.None);
        }
    }

    /// <summary>
    /// GPL allowedToStealth(): may the object be stealthed right now? We consume the S1 status
    /// (CAN_STEALTH) and the S1 model-condition set (StealthForbiddenConditions) rather than
    /// re-deriving firing/moving from weapon or physics internals.
    /// </summary>
    private bool AllowedToStealth()
    {
        if (!GameObject.TestStatus(ObjectStatus.CanStealth))
        {
            return false;
        }

        // Any forbidden model condition (firing / moving / using-ability / ...) reveals us.
        if (_data.StealthForbiddenConditions != null &&
            _data.StealthForbiddenConditions.Intersects(GameObject.ModelConditionFlags))
        {
            return false;
        }

        return true;
    }

    /// <summary>GPL calcSleepTime(): every frame while enabled, forever while not.</summary>
    private UpdateSleepTime CalcSleepTime() =>
        _enabled ? UpdateSleepTime.None : UpdateSleepTime.Forever;

    public override UpdateSleepTime Update()
    {
        if (!_enabled)
        {
            return CalcSleepTime();
        }

        var now = Context.CurrentFrame;

        // GPL: a temporary grant counts down and self-disables when it expires. (The GPL
        // "revoke when the player issues an order" exploit-guard needs an AI last-command-source
        // seam that does not exist yet - SU-3.)
        if (_framesGranted > LogicFrameSpan.Zero)
        {
            _framesGranted--;
            if (_framesGranted == LogicFrameSpan.Zero)
            {
                ReceiveGrant(false);
                return CalcSleepTime();
            }
        }

        if (AllowedToStealth())
        {
            // Do not stealth until the become-stealthed timer has elapsed.
            if (_stealthAllowedFrame > now)
            {
                return CalcSleepTime();
            }

            GameObject.SetObjectStatus(ObjectStatus.Stealthed, true);
        }
        else
        {
            // Re-arm the timer and reveal (GPL update() else-branch).
            _stealthAllowedFrame = now + _data.StealthDelay;
            GameObject.SetObjectStatus(ObjectStatus.Stealthed, false);
        }

        // Detected-while-stealthed timer (GPL update() lines 778-808).
        GameObject.SetObjectStatus(ObjectStatus.Detected, _detectionExpiresFrame > now);

        return CalcSleepTime();
    }

    // ---- the single contract walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrame("StealthAllowedFrame", ref _stealthAllowedFrame, Tolerance.Quantum);
        xfer.XferFrame("DetectionExpiresFrame", ref _detectionExpiresFrame, Tolerance.Quantum);
        xfer.XferBool("Enabled", ref _enabled);
        xfer.XferFrameSpan("FramesGranted", ref _framesGranted, Tolerance.Quantum);
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept byte-for-byte so the
    // corpus self-diff does not regress, remapped onto the real fields. Retail layout:
    // base, stealth-allowed frame, detection-expires frame, enabled, pulse-phase rate + phase
    // (client visual, discarded), disguise-as player index (-1, discarded), disguise tail
    // (discarded), [v>=2] frames-granted (discarded here - reset on load, as retail grants do
    // not survive a mid-grant reload cleanly). ----
    internal override void Load(StatePersister reader)
    {
        var version = reader.PersistVersion(2);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistLogicFrame(ref _stealthAllowedFrame);
        reader.PersistLogicFrame(ref _detectionExpiresFrame);

        var enabled = true;
        reader.PersistBoolean(ref enabled);
        if (!enabled)
        {
            throw new InvalidStateException();
        }
        _enabled = enabled;

        // pulse-phase rate + phase (client opacity animation; 4 bytes each) - discarded.
        reader.SkipUnknownBytes(4);
        reader.SkipUnknownBytes(4);

        var unknownInt2 = -1;
        reader.PersistInt32(ref unknownInt2);
        if (unknownInt2 != -1)
        {
            throw new InvalidStateException();
        }

        reader.SkipUnknownBytes(8);

        if (version >= 2)
        {
            reader.SkipUnknownBytes(4);
        }
    }
}

/// <summary>
/// Allows the use of the <see cref="ObjectDefinition.SoundStealthOn"/> and
/// <see cref="ObjectDefinition.SoundStealthOff"/> parameters on the object and is hardcoded to
/// display MESSAGE:StealthNeutralized when the object has been discovered.
/// </summary>
[SimDataAudited]
public sealed class StealthUpdateModuleData : BehaviorModuleData
{
    internal static StealthUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<StealthUpdateModuleData> FieldParseTable = new IniParseTable<StealthUpdateModuleData>
    {
        // S5 audit: StealthDelay is a duration in the INI (ms); consumed as a frame span.
        { "StealthDelay", (parser, x) => x.StealthDelay = parser.ParseTimeMillisecondsToLogicFrames() },
        { "StealthForbiddenConditions", (parser, x) => x.StealthForbiddenConditions = parser.ParseEnumBitArray<ModelConditionFlag>() },
        { "HintDetectableConditions", (parser, x) => x.HintDetectableConditions = parser.ParseEnumBitArray<ModelConditionFlag>() },
        { "FriendlyOpacityMin", (parser, x) => x.FriendlyOpacityMin = parser.ParsePercentage() },
        { "FriendlyOpacityMax", (parser, x) => x.FriendlyOpacityMax = parser.ParsePercentage() },
        { "PulseFrequency", (parser, x) => x.PulseFrequency = parser.ParseInteger() },
        { "MoveThresholdSpeed", (parser, x) => x.MoveThresholdSpeed = parser.ParseInteger() },
        { "InnateStealth", (parser, x) => x.InnateStealth = parser.ParseBoolean() },
        { "OrderIdleEnemiesToAttackMeUponReveal", (parser, x) => x.OrderIdleEnemiesToAttackMeUponReveal = parser.ParseBoolean() },
        { "DisguisesAsTeam", (parser, x) => x.DisguisesAsTeam = parser.ParseBoolean() },
        { "RevealDistanceFromTarget", (parser, x) => x.RevealDistanceFromTarget = parser.ParseFix64() },
        { "DisguiseFX", (parser, x) => x.DisguiseFX = parser.ParseAssetReference() },
        { "DisguiseRevealFX", (parser, x) => x.DisguiseRevealFX = parser.ParseAssetReference() },
        { "DisguiseTransitionTime", (parser, x) => x.DisguiseTransitionTime = parser.ParseInteger() },
        { "DisguiseRevealTransitionTime", (parser, x) => x.DisguiseRevealTransitionTime = parser.ParseInteger() },
        { "GrantedBySpecialPower", (parser, x) => x.GrantedBySpecialPower = parser.ParseBoolean() },
        { "EnemyDetectionEvaEvent", (parser, x) => x.EnemyDetectionEvaEvent = parser.ParseAssetReference() },
        { "OwnDetectionEvaEvent", (parser, x) => x.OwnDetectionEvaEvent = parser.ParseAssetReference() },
        { "UseRiderStealth", (parser, x) => x.UseRiderStealth = parser.ParseBoolean() },
        { "DetectedByAnyoneRange", (parser, x) => x.DetectedByAnyoneRange = parser.ParseFix64() },
        { "RemoveTerrainRestrictionOnUpgrade", (parser, x) => x.RemoveTerrainRestrictionOnUpgrade = parser.ParseString() },
        { "RevealWeaponSets", (parser, x) => x.RevealWeaponSets = parser.ParseEnumFlags<WeaponSetConditions>() },
        { "StartsActive", (parser, x) => x.StartsActive = parser.ParseBoolean() },
        { "DetectedByFriendliesOnly", (parser, x) => x.DetectedByFriendliesOnly = parser.ParseBoolean() },
        { "VoiceMoveToStealthyArea", (parser, x) => x.VoiceMoveToStealthyArea = parser.ParseAssetReference() },
        { "VoiceEnterStateMoveToStealthyArea", (parser, x) => x.VoiceEnterStateMoveToStealthyArea = parser.ParseAssetReference() },
        { "OneRingDelayOn", (parser, x) => x.OneRingDelayOn = parser.ParseInteger() },
        { "OneRingDelayOff", (parser, x) => x.OneRingDelayOff = parser.ParseInteger() },
        { "RingAnimTimeOn", (parser, x) => x.RingAnimTimeOn = parser.ParseInteger() },
        { "RingAnimTimeOff", (parser, x) => x.RingAnimTimeOff = parser.ParseInteger() },
        { "RingDelayAfterRemoving", (parser, x) => x.RingDelayAfterRemoving = parser.ParseInteger() },

        { "BecomeStealthedFX", (parser, x) => x.BecomeStealthedFX = parser.ParseAssetReference() },
        { "ExitStealthFX", (parser, x) => x.ExitStealthFX = parser.ParseAssetReference() },
        { "BecomeStealthedOneRingFX", (parser, x) => x.BecomeStealthedOneRingFX = parser.ParseAssetReference() },
        { "ExitStealthOneRingFX", (parser, x) => x.ExitStealthOneRingFX = parser.ParseAssetReference() },
         { "RequiredUpgradeNames", (parser, x) => x.RequiredUpgradeNames = parser.ParseAssetReferenceArray() },
    };

    /// <summary>
    /// Frames the object must spend un-revealed before it becomes stealthed, and the default
    /// detection duration. S5 audit: ms in the INI, ceil-quantized to frames at parse.
    /// </summary>
    public LogicFrameSpan StealthDelay { get; private set; }

    /// <summary>Model conditions (firing / moving / ...) that forbid stealthing while set; the
    /// S1-maintained gate the runtime consumes.</summary>
    public BitArray<ModelConditionFlag> StealthForbiddenConditions { get; private set; }

    public BitArray<ModelConditionFlag> HintDetectableConditions { get; private set; }
    public Percentage FriendlyOpacityMin { get; private set; }
    public Percentage FriendlyOpacityMax { get; private set; }
    public int PulseFrequency { get; private set; }

    /// <summary>Velocity threshold above which a "not while moving" unit reveals. Float velocity
    /// substrate; subsumed by the Moving forbidden condition and not consumed sim-side (SU-3).</summary>
    public int MoveThresholdSpeed { get; private set; }

    public bool InnateStealth { get; private set; }
    public bool OrderIdleEnemiesToAttackMeUponReveal { get; private set; }
    public bool DisguisesAsTeam { get; private set; }

    /// <summary>Distance to the current attack target at which the object reveals. Needs an
    /// AI-victim seam; parsed (Fix64-audited), not consumed yet (SU-3).</summary>
    public Fix64 RevealDistanceFromTarget { get; private set; }

    public string DisguiseFX { get; private set; }
    public string DisguiseRevealFX { get; private set; }
    public int DisguiseTransitionTime { get; private set; }
    public int DisguiseRevealTransitionTime { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public bool GrantedBySpecialPower { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public string EnemyDetectionEvaEvent { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public string OwnDetectionEvaEvent { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public bool UseRiderStealth { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public Fix64 DetectedByAnyoneRange { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string RemoveTerrainRestrictionOnUpgrade { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public WeaponSetConditions RevealWeaponSets { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool StartsActive { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool DetectedByFriendliesOnly { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string VoiceMoveToStealthyArea { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string VoiceEnterStateMoveToStealthyArea { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int OneRingDelayOn { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int OneRingDelayOff { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int RingAnimTimeOn { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int RingAnimTimeOff { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public int RingDelayAfterRemoving { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string BecomeStealthedFX { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string ExitStealthFX { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string BecomeStealthedOneRingFX { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string ExitStealthOneRingFX { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string[] RequiredUpgradeNames { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new StealthUpdate(gameObject, gameEngine.SimContext, this);
    }
}
