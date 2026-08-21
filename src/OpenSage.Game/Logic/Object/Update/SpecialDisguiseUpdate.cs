// SpecialDisguiseUpdate - R13 port (api-freeze-v1 / template v1.1), per
// bfme2-workbench/research/modules-r13/specs/SpecialDisguiseUpdateModuleData.md.
//
// Classification: gpl-sibling (timing fields) + data-derivable (disguise/opacity fields). No
// Ghidra/game.dat evidence is used or cited anywhere in this file - clean-room wall not
// implicated (see the spec's §0 scope note).
//
// generals-gpl carries no SpecialDisguiseUpdate at all (grep confirms - a BFME2-only class,
// same posture as the landed ToggleHiddenSpecialAbilityUpdate). The field set splits across
// two sources:
//   - Timing fields (SpecialPowerTemplate/UnpackTime/PreparationTime/PersistentPrepTime/
//     PackTime/AwardXPForTriggering) are literal name-for-name matches against
//     SpecialAbilityUpdateModuleData (generals-gpl SpecialAbilityUpdate.h:128-134,159).
//   - Disguise/opacity fields (OpacityTarget/DisguiseAsTemplate/
//     DisguisedAsTemplate_EnemyPerspective/DisguiseFX) have no literal GPL name match;
//     StealthUpdateModuleData/StealthUpdate (StealthUpdate.h:77-181, .cpp:939-1000) is cited
//     for MECHANISM only (enemy/ally visibility split, opacity-driven transition, FX-on-
//     transition), argued in the spec's §1.2.
//   - ForceMountedWhenDisguising has no GPL analog; the landed sibling
//     ToggleMountedSpecialAbilityUpdateModuleData.CancelDisguiseWhenDismounting is direct
//     in-engine evidence this engine's BFME2 content couples "disguise" and "mounted" state
//     in the same family (spec §1.3).
//
// STATE MACHINE (same five-phase shape as ToggleHiddenSpecialAbilityUpdate, same zero-duration
// skip convention): Packed -(InitiateIntentToDoSpecialPower, UnpackTime)-> Unpacking
// -(PreparationTime)-> Prepared -(Trigger, one-shot PersistentPrepTime extension if unused)->
// Active -(PackTime)-> Packing -> Packed. A Prepared window that times out with no Trigger()
// call skips Active entirely and packs straight from Prepared (no disguise applied, no XP).
// A zero-duration stage is skipped immediately.
//
// This class has no StartAbilityRange field (unlike ToggleHiddenSpecialAbilityUpdate /
// ReplaceObjectUpdate) - confirmed absent from this class's frozen 11-field INI vocabulary
// (spec F-SDU-4) - so InitiateIntentToDoSpecialPower here is a name-match + phase-guard only,
// no proximity gate.
//
// PersistentPrepTime (F-SDU-1, the largest judgment call in the spec - flagged for reviewer
// sign-off): GPL's own SpecialAbilityUpdate semantics (isPersistentAbility()/
// resetPreparation()) are a REPEATING re-arm of the prep window after every trigger. This port
// instead follows the already-landed ToggleHiddenSpecialAbilityUpdate sibling precedent (same
// field name, same directory, closer in shape to this class than the generic GPL base): a
// ONE-SHOT extension of the Prepared window, consumed at most once per cycle, tracked by
// _prepExtended.
//
// DISGUISE/OPACITY MECHANISM (spec §1.2): what GPL's changeVisualDisguise() actually does -
// destroy/recreate the client Drawable - is pure client-rendering work with no [SimState]-safe
// entry point (ISimContext is deliberately, permanently UI-absent). What IS modeled, all as
// sim state:
//   - ObjectStatus.Disguised set for the Active window's duration, cleared on pack-out.
//   - ModelConditionFlag.Disguised set/cleared the same way, for whatever presentation layer
//     consumes it.
//   - DisguiseAsTemplate / DisguisedAsTemplate_EnemyPerspective held as
//     LazyAssetReference<ObjectDefinition>, exposed read-only via
//     <see cref="GetResolvedTemplateNameFor"/> for whatever render/UI layer eventually resolves
//     "which template does this object look like to observer X" - parsed and held, not applied
//     to a Drawable (same posture as EmpUpdate's F-EMP-5 and ToggleHiddenSpecialAbilityUpdate's
//     ShowPalantirTimer).
//   - DisguiseFX fired once via Context.Events.FireFXAtObject at the moment the disguise
//     transition begins (on entering Active) - the explicit event standing in for GPL's
//     drawable-swap visual cue.
//   - OpacityTarget tracked as a Fix64 sim field (_currentOpacity), NOT pushed to a renderer -
//     same F-EMP-5-class gap as EmpUpdate.CurrentScale.
//
// F-SDU-2 (opacity ramp duration - flagged, not silently assumed): this class has no
// DisguiseTransitionFrames-equivalent field, so the fade-to-target curve's own duration is not
// specified independently. This port ties the ramp to the already-present UnpackTime/PackTime
// windows: _currentOpacity ramps linearly from Fix64.One toward OpacityTarget across
// UnpackTime on the way in, and linearly back toward Fix64.One across PackTime on the way out -
// the only two timer fields this class's own INI vocabulary supplies for a "ramp" shape.
//
// F-SDU-3 (fallback template for a non-ally, non-enemy observer - spec §1.2/§3 case 10):
// resolved to DisguiseAsTemplate as the narrowest reading of the DisguisedAsTemplate_
// EnemyPerspective field's own suffix - only a RelationshipType.Enemies observer gets the
// EnemyPerspective override; an Allies observer sees the object's own true template (per the
// cited GPL STEALTHLOOK_NONE rule); every other relationship falls back to DisguiseAsTemplate.
//
// ForceMountedWhenDisguising (spec §1.3): on entering Active, force ModelConditionFlag.Mounted
// on if not already set from a real mount; on leaving Active, clear it ONLY if this module set
// it (tracked via _forcedMountedFlag) so a genuinely-mounted unit's own real Mounted flag is
// never clobbered - same "don't stomp state you don't own" discipline as EmpUpdate's
// _dieFrame = LogicFrame.MaxValue repeat-kill guard.
//
// Every mutable sim field appears in Xfer exactly once (spec §2); tolerances are the field's
// conformance class at its declaration site.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.FX;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SpecialDisguiseUpdate : UpdateModule
{
    private readonly SpecialDisguiseUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private SpecialDisguisePhase _phase;
    private LogicFrame _phaseEndFrame;

    /// <summary>
    /// Whether the one-shot PersistentPrepTime extension has already been consumed for the
    /// current Prepared window (F-SDU-1 above).
    /// </summary>
    private bool _prepExtended;

    /// <summary>The deterministic opacity ramp value (F-SDU-2 above); not pushed to a renderer.</summary>
    private Fix64 _currentOpacity;

    /// <summary>Whether this module currently holds the object in the disguised (Active) state.</summary>
    private bool _disguised;

    /// <summary>
    /// Whether THIS module forced ModelConditionFlag.Mounted on (as opposed to the object
    /// already being genuinely mounted) - so pack-out only clears a flag this module owns.
    /// </summary>
    private bool _forcedMountedFlag;

    public SpecialDisguiseUpdate(GameObject gameObject, ISimContext context, SpecialDisguiseUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _phase = SpecialDisguisePhase.Packed;
        _currentOpacity = Fix64.One;

        // Ticks every frame like the rest of this SpecialPowerTemplate-gated family
        // (ToggleHiddenSpecialAbilityUpdate, MissileLauncherBuildingUpdate, ReplaceObjectUpdate).
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>The deterministic opacity ramp value (F-SDU-2); parsed and held, not rendered.</summary>
    public Fix64 CurrentOpacity => _currentOpacity;

    /// <summary>
    /// Resolves which template this object presents to <paramref name="observer"/> (F-SDU-3):
    /// an Allies observer is unaffected by disguise and sees this object's own true template;
    /// an Enemies observer sees <see cref="SpecialDisguiseUpdateModuleData.DisguisedAsTemplate_EnemyPerspective"/>
    /// (falling back to <see cref="SpecialDisguiseUpdateModuleData.DisguiseAsTemplate"/> if
    /// unset); every other relationship sees <see cref="SpecialDisguiseUpdateModuleData.DisguiseAsTemplate"/>.
    /// Returns this object's own template name whenever the module is not currently Active.
    /// Read-only accessor for a future render/UI consumer (spec §1.2) - not applied to a
    /// Drawable today.
    /// </summary>
    public string GetResolvedTemplateNameFor(GameObject observer)
    {
        if (!_disguised)
        {
            return GameObject.Definition.Name;
        }

        var relationship = GameObject.GetRelationship(observer);

        if (relationship == RelationshipType.Allies)
        {
            return GameObject.Definition.Name;
        }

        if (relationship == RelationshipType.Enemies)
        {
            return (_data.DisguisedAsTemplate_EnemyPerspective?.Value ?? _data.DisguiseAsTemplate?.Value)?.Name
                ?? GameObject.Definition.Name;
        }

        return _data.DisguiseAsTemplate?.Value?.Name ?? GameObject.Definition.Name;
    }

    /// <summary>
    /// Starts the Packed -> Unpacking -> Prepared sequence. Only this module's own special
    /// power (matched by template name) may fire it, only while Packed (no interrupting or
    /// re-triggering an in-flight cycle). This class has no StartAbilityRange field (F-SDU-4),
    /// so there is no proximity gate.
    /// </summary>
    public bool InitiateIntentToDoSpecialPower(string specialPowerTemplateName, GameObject triggeringObject)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        if (_phase != SpecialDisguisePhase.Packed)
        {
            return false;
        }

        EnterUnpackingOrLater();
        return true;
    }

    /// <summary>
    /// Manually fires the disguise's effect while Prepared: awards
    /// <see cref="SpecialDisguiseUpdateModuleData.AwardXPForTriggering"/> to
    /// <paramref name="triggeringObject"/>, sets <see cref="ObjectStatus.Disguised"/> and
    /// <see cref="ModelConditionFlag.Disguised"/>, fires <see cref="SpecialDisguiseUpdateModuleData.DisguiseFX"/>
    /// once, applies <see cref="SpecialDisguiseUpdateModuleData.ForceMountedWhenDisguising"/>,
    /// and enters Active. False (no-op) outside the Prepared phase.
    /// </summary>
    public bool Trigger(GameObject triggeringObject)
    {
        if (_phase != SpecialDisguisePhase.Prepared)
        {
            return false;
        }

        if (_data.AwardXPForTriggering != 0 && triggeringObject != null)
        {
            triggeringObject.ExperienceTracker.AddExperiencePoints(_data.AwardXPForTriggering);
        }

        GameObject.SetObjectStatus(ObjectStatus.Disguised, true);
        GameObject.SetModelConditionState(ModelConditionFlag.Disguised);
        _disguised = true;

        if (_data.DisguiseFX?.Value != null)
        {
            Context.Events.FireFXAtObject(_data.DisguiseFX.Value.Name, GameObject.Id);
        }

        if (_data.ForceMountedWhenDisguising && !GameObject.ModelConditionFlags.Get(ModelConditionFlag.Mounted))
        {
            GameObject.SetModelConditionState(ModelConditionFlag.Mounted);
            _forcedMountedFlag = true;
        }

        EnterActiveOrLater();
        return true;
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        switch (_phase)
        {
            case SpecialDisguisePhase.Unpacking:
                UpdateOpacityRamp(rampFrames: _data.UnpackTime, from: Fix64.One, to: _data.OpacityTarget);

                if (now >= _phaseEndFrame)
                {
                    EnterPreparedOrLater();
                }
                break;

            case SpecialDisguisePhase.Prepared:
                if (now >= _phaseEndFrame)
                {
                    if (!_prepExtended && _data.PersistentPrepTime.Value > 0)
                    {
                        _prepExtended = true;
                        _phaseEndFrame = now + _data.PersistentPrepTime;
                    }
                    else
                    {
                        // The window closed with no Trigger call: skip Active entirely, no
                        // effect, no XP (same "auto-packs" convention as
                        // ToggleHiddenSpecialAbilityUpdate).
                        EnterPackingOrLater();
                    }
                }
                break;

            case SpecialDisguisePhase.Active:
                // This class has no EffectDuration field (spec §1.1): PackTime is reused as
                // the Active window's own length (F-SDU-2 cross-reference - "what actually
                // ends the Active window"), and the Active->Packing hand-off collapses into a
                // single PackTime-length window straight to Packed (the diagram's
                // "Active --(PackTime)--> Packing --> Packed" tail read as one span, not two -
                // no separate ModelConditionFlag.Packing hold on this path, matching contract
                // test case 9's single-PackTime step count). The opacity ramp runs "on the way
                // out" across this same window (F-SDU-2).
                UpdateOpacityRamp(rampFrames: _data.PackTime, from: _data.OpacityTarget, to: Fix64.One);

                if (now >= _phaseEndFrame)
                {
                    ExitDisguise();
                    _phase = SpecialDisguisePhase.Packed;
                    _currentOpacity = Fix64.One;
                }
                break;

            case SpecialDisguisePhase.Packing:
                UpdateOpacityRamp(rampFrames: _data.PackTime, from: _data.OpacityTarget, to: Fix64.One);

                if (now >= _phaseEndFrame)
                {
                    ExitDisguise();
                    GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
                    _phase = SpecialDisguisePhase.Packed;
                    _currentOpacity = Fix64.One;
                }
                break;
        }

        return UpdateSleepTime.None;
    }

    private void UpdateOpacityRamp(LogicFrameSpan rampFrames, Fix64 from, Fix64 to)
    {
        if (rampFrames.Value == 0)
        {
            _currentOpacity = to;
            return;
        }

        var startFrame = _phaseEndFrame - rampFrames.Value;
        var now = Context.CurrentFrame;
        var elapsed = now.Value > startFrame.Value ? now.Value - startFrame.Value : 0u;
        if (elapsed > rampFrames.Value)
        {
            elapsed = rampFrames.Value;
        }

        var ratio = new Fix64((int)elapsed) / new Fix64((int)rampFrames.Value);
        _currentOpacity = from + (to - from) * ratio;
    }

    private void ExitDisguise()
    {
        if (_disguised)
        {
            GameObject.SetObjectStatus(ObjectStatus.Disguised, false);
            GameObject.ClearModelConditionState(ModelConditionFlag.Disguised);
            _disguised = false;
        }

        if (_forcedMountedFlag)
        {
            GameObject.ClearModelConditionState(ModelConditionFlag.Mounted);
            _forcedMountedFlag = false;
        }
    }

    private void EnterUnpackingOrLater()
    {
        if (_data.UnpackTime.Value > 0)
        {
            _phase = SpecialDisguisePhase.Unpacking;
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
        _currentOpacity = _data.OpacityTarget;

        if (_data.PreparationTime.Value > 0)
        {
            _phase = SpecialDisguisePhase.Prepared;
            _phaseEndFrame = Context.CurrentFrame + _data.PreparationTime;
            _prepExtended = false;
        }
        else
        {
            // Nothing to prepare, so there is no window in which Trigger() could ever be
            // called: skip straight to packing, matching the family's zero-duration convention.
            EnterPackingOrLater();
        }
    }

    private void EnterActiveOrLater()
    {
        // Entered synchronously from the driven Trigger() call, not from an Update() tick -
        // per this batch's sleepy-update discipline, the phase only ever advances OUT of
        // Active on a subsequent Update() tick (see the Active case above), never inside this
        // same synchronous call, even when PackTime is zero. This is what lets a test observe
        // _phase == Active immediately after Trigger() with no Step() in between (contract
        // test case 5) while a PackTime-zero configuration still packs out on the very next
        // tick rather than lingering forever.
        _phase = SpecialDisguisePhase.Active;
        _phaseEndFrame = Context.CurrentFrame + _data.PackTime;
    }

    private void EnterPackingOrLater()
    {
        if (_data.PackTime.Value > 0)
        {
            _phase = SpecialDisguisePhase.Packing;
            _phaseEndFrame = Context.CurrentFrame + _data.PackTime;
            GameObject.SetModelConditionState(ModelConditionFlag.Packing);
        }
        else
        {
            ExitDisguise();
            GameObject.ClearModelConditionState(ModelConditionFlag.Packing);
            _phase = SpecialDisguisePhase.Packed;
            _currentOpacity = Fix64.One;
        }
    }

    private enum SpecialDisguisePhase
    {
        Packed,
        Unpacking,
        Prepared,
        Active,
        Packing,
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never a translated GPL order (this
    // module is fresh code, not a field-for-field GPL translation - see the spec's own note).
    //
    // Tolerances (ruling A3): the phase enum and the two bool flags are lifecycle facts, so
    // Exact. The phase-end frame is a timer, so Quantum (ch.2, XferFrame's own default).
    // _currentOpacity is a deterministic ramp value, same conformance class as EmpUpdate's
    // CurrentScale, so Exact.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("PhaseEndFrame", ref _phaseEndFrame);
        xfer.XferBool("PrepExtended", ref _prepExtended);
        xfer.XferFix64("CurrentOpacity", ref _currentOpacity, Tolerance.Exact);
        xfer.XferBool("Disguised", ref _disguised);
        xfer.XferBool("ForcedMountedFlag", ref _forcedMountedFlag);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class SpecialDisguiseUpdateModuleData : UpdateModuleData
{
    internal static SpecialDisguiseUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<SpecialDisguiseUpdateModuleData> FieldParseTable = new IniParseTable<SpecialDisguiseUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseIdentifier() },
        { "UnpackTime", (parser, x) => x.UnpackTime = parser.ParseDurationLogicFrames() },
        { "PreparationTime", (parser, x) => x.PreparationTime = parser.ParseDurationLogicFrames() },
        { "PersistentPrepTime", (parser, x) => x.PersistentPrepTime = parser.ParseDurationLogicFrames() },
        { "PackTime", (parser, x) => x.PackTime = parser.ParseDurationLogicFrames() },
        { "OpacityTarget", (parser, x) => x.OpacityTarget = parser.ParseFix64() },
        { "AwardXPForTriggering", (parser, x) => x.AwardXPForTriggering = parser.ParseInteger() },
        { "DisguiseAsTemplate", (parser, x) => x.DisguiseAsTemplate = parser.ParseObjectReference() },
        { "DisguisedAsTemplate_EnemyPerspective", (parser, x) => x.DisguisedAsTemplate_EnemyPerspective = parser.ParseObjectReference() },
        { "DisguiseFX", (parser, x) => x.DisguiseFX = parser.ParseFXListReference() },
        { "ForceMountedWhenDisguising", (parser, x) => x.ForceMountedWhenDisguising = parser.ParseBoolean() }
    };

    public string SpecialPowerTemplate { get; private set; }
    public LogicFrameSpan UnpackTime { get; private set; }
    public LogicFrameSpan PreparationTime { get; private set; }
    public LogicFrameSpan PersistentPrepTime { get; private set; }
    public LogicFrameSpan PackTime { get; private set; }
    public Fix64 OpacityTarget { get; private set; }
    public int AwardXPForTriggering { get; private set; }
    public LazyAssetReference<ObjectDefinition> DisguiseAsTemplate { get; private set; }
    public LazyAssetReference<ObjectDefinition> DisguisedAsTemplate_EnemyPerspective { get; private set; }
    public LazyAssetReference<FXList> DisguiseFX { get; private set; }
    public bool ForceMountedWhenDisguising { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SpecialDisguiseUpdate(gameObject, gameEngine.SimContext, this);
    }
}
