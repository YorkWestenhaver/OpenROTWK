// InvisibilityUpdate - BFME2 stealth/camouflage update (experiment-round-4 §4.1; template v1.1).
//
// CLEAN-ROOM / REFERENCE CAVEAT (packet): there is NO same-name GPL file - InvisibilityUpdate is
// BFME2-specific. The BEHAVIORAL analog is generals-gpl GeneralsMD StealthUpdate.cpp/.h (GPL used
// as a fact source only; this is fresh code). The facts borrowed from that analog:
//   - sim state is a small timer/flag set: { stealthAllowedFrame, detectionExpiresFrame, enabled }
//     (StealthUpdate.cpp xfer: m_stealthAllowedFrame, m_detectionExpiresFrame, m_enabled; the
//     disguise + client pulse-phase members do not apply - InvisibilityNugget has no disguise
//     fields and opacity/FX are client rendering, not sim state).
//   - update(): while disabled -> sleep forever; while enabled -> demand every-frame attention
//     (StealthUpdate::calcSleepTime). "allowed to stealth" is gated by the forbidden-condition
//     mask (StealthUpdate::allowedToStealth reads status bits while firing / using-ability); when
//     allowed and the re-stealth timer has elapsed, set the STEALTHED logic status; when not
//     allowed, re-arm the timer (now + delay) and clear STEALTHED. A separate detection timer
//     forces the DETECTED status until it expires (StealthUpdate::markAsDetected).
//   - the re-stealth delay is StealthUpdate's m_stealthDelay; InvisibilityUpdate's INI spells it
//     UpdatePeriod (real AotR data: UpdatePeriod = 2000, i.e. milliseconds -> 10 logic frames).
//
// The stealth EFFECT is driven onto the object's ObjectStatus logic bits (Stealthed / Detected /
// CanStealth) - the same channel GPL StealthUpdate writes (OBJECT_STATUS_STEALTHED/DETECTED/
// CAN_STEALTH). Opacity, sounds and the Become/ExitStealth FX are client outputs: emitted through
// ISimEvents (S8), never read back as sim inputs.
//
// InvisibilityType CAMOUFLAGE vs STEALTH needs no branch here: the distinction (camouflage is
// broken by movement/firing, stealth is not) is expressed by the per-object ForbiddenConditions
// mask in the INI (real data: CAMOUFLAGE units carry ForbiddenConditions = FIRING_ANY), which the
// forbidden-condition gate already honours.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class InvisibilityUpdate : UpdateModule
{
    private readonly InvisibilityUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Earliest frame the object may (re)become stealthed; re-armed whenever a forbidden
    /// condition breaks stealth (StealthUpdate m_stealthAllowedFrame).</summary>
    private LogicFrame _stealthAllowedFrame;

    /// <summary>Frame until which the object is forcibly DETECTED; 0 = not detected
    /// (StealthUpdate m_detectionExpiresFrame).</summary>
    private LogicFrame _detectionExpiresFrame;

    /// <summary>Whether the module is active (StartsActive / receiveGrant; StealthUpdate m_enabled).</summary>
    private bool _enabled;

    public InvisibilityUpdate(GameObject gameObject, ISimContext context, InvisibilityUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _enabled = data.StartsActive;
        _detectionExpiresFrame = new LogicFrame(0);

        if (_enabled)
        {
            // GPL onObjectCreated: eligible-to-stealth objects carry CAN_STEALTH; the first stealth
            // is deferred by one re-stealth delay so a freshly-built unit does not pop invisible.
            GameObject.SetObjectStatus(ObjectStatus.CanStealth, true);
            _stealthAllowedFrame = Context.CurrentFrame + _data.UpdatePeriod;
            SetWakeFrame(UpdateSleepTime.None);
        }
        else
        {
            _stealthAllowedFrame = new LogicFrame(UpdateSleepTime.SleepForever);
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    /// <summary>External enable/disable (GPL receiveGrant): a special power or upgrade toggling
    /// this object's invisibility on or off. Never restarts itself.</summary>
    public void SetInvisibilityActive(bool active)
    {
        if (_enabled == active)
        {
            return;
        }

        _enabled = active;

        if (active)
        {
            GameObject.SetObjectStatus(ObjectStatus.CanStealth, true);
            _stealthAllowedFrame = Context.CurrentFrame;
            SetWakeFrame(UpdateSleepTime.None);
        }
        else
        {
            GameObject.SetObjectStatus(ObjectStatus.CanStealth, false);
            GameObject.SetObjectStatus(ObjectStatus.Stealthed, false);
            _stealthAllowedFrame = new LogicFrame(UpdateSleepTime.SleepForever);
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    /// <summary>External detection hint (GPL markAsDetected): a detector forces this object visible
    /// for a window. Zero frames uses the re-stealth delay as the default window.</summary>
    public void MarkAsDetected(LogicFrameSpan numFrames = default)
    {
        var now = Context.CurrentFrame;
        if (numFrames == LogicFrameSpan.Zero)
        {
            _detectionExpiresFrame = now + _data.UpdatePeriod;
        }
        else if (_detectionExpiresFrame < now + numFrames)
        {
            _detectionExpiresFrame = now + numFrames;
        }

        if (_enabled)
        {
            SetWakeFrame(UpdateSleepTime.None);
        }
    }

    private bool AllowedToStealth()
    {
        // GPL allowedToStealth: an object may not stealth while any forbidden condition holds, and
        // only while it still carries CAN_STEALTH (cleared by external disable / detection systems).
        if (!GameObject.TestStatus(ObjectStatus.CanStealth))
        {
            return false;
        }

        var nugget = _data.InvisibilityNugget;
        if (nugget == null)
        {
            return true;
        }

        // Forbidden model conditions (real data: USING_ABILITY, FIRING_ANY) break stealth.
        if (nugget.ForbiddenConditions != null &&
            nugget.ForbiddenConditions.Intersects(GameObject.ModelConditionFlags))
        {
            return false;
        }

        // Forbidden weapon-set conditions (analog StealthUpdate m_requiresWeaponSetType) break stealth.
        if (nugget.ForbiddenWeaponConditions != null &&
            nugget.ForbiddenWeaponConditions.Intersects(GameObject.WeaponSetConditions))
        {
            return false;
        }

        return true;
    }

    public override UpdateSleepTime Update()
    {
        if (!_enabled || GameObject.IsEffectivelyDead)
        {
            return UpdateSleepTime.Forever;
        }

        var now = Context.CurrentFrame;
        var nugget = _data.InvisibilityNugget;

        if (AllowedToStealth())
        {
            // Wait out the re-stealth timer before going invisible.
            if (_stealthAllowedFrame <= now && !GameObject.TestStatus(ObjectStatus.Stealthed))
            {
                GameObject.SetObjectStatus(ObjectStatus.Stealthed, true);
                if (nugget?.BecomeStealthedFX != null)
                {
                    Context.Events.FireFXAtObject(nugget.BecomeStealthedFX, GameObject.Id);
                }
            }
        }
        else
        {
            // Re-arm the timer so stealth returns only after the delay once conditions clear.
            _stealthAllowedFrame = now + _data.UpdatePeriod;
            if (GameObject.TestStatus(ObjectStatus.Stealthed))
            {
                GameObject.SetObjectStatus(ObjectStatus.Stealthed, false);
                if (nugget?.ExitStealthFX != null)
                {
                    Context.Events.FireFXAtObject(nugget.ExitStealthFX, GameObject.Id);
                }
            }
        }

        // Detection window (GPL markAsDetected drives OBJECT_STATUS_DETECTED).
        GameObject.SetObjectStatus(ObjectStatus.Detected, _detectionExpiresFrame > now);

        // GPL calcSleepTime: enabled -> attend every frame.
        return UpdateSleepTime.None;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrame("StealthAllowedFrame", ref _stealthAllowedFrame, Tolerance.Quantum);   // timer
        xfer.XferFrame("DetectionExpiresFrame", ref _detectionExpiresFrame, Tolerance.Quantum); // timer
        xfer.XferBool("Enabled", ref _enabled);                                               // Exact
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[SimDataAudited]
[AddedIn(SageGame.Bfme2)]
public sealed class InvisibilityUpdateModuleData : UpdateModuleData
{
    internal static InvisibilityUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<InvisibilityUpdateModuleData> FieldParseTable = new IniParseTable<InvisibilityUpdateModuleData>
    {
        // UpdatePeriod is milliseconds in INI (real AotR data: 2000); ceil-quantized to logic
        // frames (S5) - this is the re-stealth delay (StealthUpdate m_stealthDelay analog).
        { "UpdatePeriod", (parser, x) => x.UpdatePeriod = parser.ParseDurationLogicFrames() },
        { "StartsActive", (parser, x) => x.StartsActive  = parser.ParseBoolean() },
        { "InvisibilityNugget", (parser, x) => x.InvisibilityNugget = InvisibilityNugget.Parse(parser) },
        { "RequiredUpgrades", (parser, x) => x.RequiredUpgrades = parser.ParseAssetReferenceArray() },
        { "ForbiddenUpgrades", (parser, x) => x.ForbiddenUpgrades = parser.ParseAssetReferenceArray() },
        { "UnitSpecificSoundNameToUseAsVoiceMoveToStealthyArea", (parser, x) =>
            x.UnitSpecificSoundNameToUseAsVoiceMoveToStealthyArea = parser.ParseAssetReference() },
        { "UnitSpecificSoundNameToUseAsVoiceEnterStateMoveToStealthyArea", (parser, x) =>
            x.UnitSpecificSoundNameToUseAsVoiceEnterStateMoveToStealthyArea = parser.ParseAssetReference() },
        { "Broadcast", (parser, x) => x.Broadcast = parser.ParseBoolean() },
        { "BroadcastRange", (parser, x) => x.BroadcastRange = parser.ParseInteger() },
        { "BroadcastObjectFilter", (parser, x) => x.BroadcastObjectFilter = ObjectFilter.Parse(parser) }
    };

    /// <summary>Re-stealth delay (ms in INI, ceil-quantized to frames at parse, S5).</summary>
    public LogicFrameSpan UpdatePeriod { get; private set; }

    public bool StartsActive { get; private set; }
    public InvisibilityNugget InvisibilityNugget { get; private set; }

    /// <summary>Upgrades that must all be held for invisibility; unconsumed - no upgrade-template
    /// lookup on ISimContext.Assets yet (see InvisibilityUpdate.md behavior-fact gaps).</summary>
    public string[] RequiredUpgrades { get; private set; }

    /// <summary>Upgrades that forbid invisibility while held; unconsumed (same gap as above).</summary>
    public string[] ForbiddenUpgrades { get; private set; }

    public string UnitSpecificSoundNameToUseAsVoiceMoveToStealthyArea { get; private set; }
    public string UnitSpecificSoundNameToUseAsVoiceEnterStateMoveToStealthyArea { get; private set; }

    /// <summary>Broadcast stealth to nearby allies; unconsumed - no GPL/spec reference for the
    /// broadcast fact yet (see InvisibilityUpdate.md).</summary>
    public bool Broadcast { get; private set; }

    /// <summary>Broadcast radius; unconsumed (time-as-int retained pending the broadcast fact).</summary>
    public int BroadcastRange { get; private set; }

    public ObjectFilter BroadcastObjectFilter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new InvisibilityUpdate(gameObject, gameEngine.SimContext, this);
    }
}

public sealed class InvisibilityNugget
{
    internal static InvisibilityNugget Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<InvisibilityNugget> FieldParseTable = new IniParseTable<InvisibilityNugget>
    {
        { "InvisibilityType", (parser, x) => x.Type = parser.ParseEnum<InvisibilityType>() },
        // Distance the object is detectable at (S5 distance vocabulary -> Fix64); consumed by the
        // detector side (StealthDetectorUpdate), not by this module.
        { "DetectionRange", (parser, x) => x.DetectionRange  = parser.ParseFix64() },
        { "Options", (parser, x) => x.Options = parser.ParseEnum<InvisibilityOptions>() },
        { "ForbiddenConditions", (parser, x) => x.ForbiddenConditions = parser.ParseEnumBitArray<ModelConditionFlag>() },
        { "BecomeStealthedFX", (parser, x) => x.BecomeStealthedFX = parser.ParseAssetReference() },
        { "ExitStealthFX", (parser, x) => x.ExitStealthFX = parser.ParseAssetReference() },
        { "ForbiddenWeaponConditions", (parser, x) => x.ForbiddenWeaponConditions = parser.ParseEnumBitArray<WeaponSetConditions>() },
        { "HintDetectableConditions", (parser, x) => x.HintDetectableConditions = parser.ParseEnum<DetectableConditions>() }
    };

    public InvisibilityType Type { get; private set; }

    /// <summary>Detectable range (quantized Q31.32); read by detectors, not by InvisibilityUpdate.</summary>
    public Fix64 DetectionRange { get; private set; }

    public InvisibilityOptions Options { get; private set; }
    public BitArray<ModelConditionFlag> ForbiddenConditions { get; private set; }
    public string BecomeStealthedFX { get; private set; }
    public string ExitStealthFX { get; private set; }
    public BitArray<WeaponSetConditions> ForbiddenWeaponConditions { get; private set; }
    public DetectableConditions HintDetectableConditions { get; private set; }
}

public enum InvisibilityType
{
    [IniEnum("CAMOUFLAGE")]
    Camouflage,

    [IniEnum("STEALTH")]
    Stealth,
}

public enum InvisibilityOptions
{
    [IniEnum("DETECTED_BY_FRIENDLIES")]
    DetectedByFriendlies,

    [IniEnum("UNTOGGLE_HIDDEN_WHEN_LEAVING_STEALTH")]
    UntoggleHiddenWhenLeavingStealth,

    [IniEnum("ALLOW_NEAR_TREES")]
    AllowNearTrees,
}

public enum DetectableConditions
{
    [IniEnum("IS_FIRING_WEAPON")]
    IsFiringWeapon,
}
