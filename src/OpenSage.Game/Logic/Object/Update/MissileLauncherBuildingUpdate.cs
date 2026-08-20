// MissileLauncherBuildingUpdate - R12 port (api-freeze-v1 §6 / template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD MissileLauncherBuildingUpdate.cpp/.h (GPL
// semantics reference only; this is fresh code against the frozen contract). Behavior facts
// used:
//   - state is exactly { doorState, timeoutState, timeoutFrame } (GPL m_doorState,
//     m_timeoutState, m_timeoutFrame; m_specialPowerModule is a cached sibling pointer, not
//     serialized state, and m_openIdleAudio is a per-frame client audio handle, not sim
//     state either).
//   - a five-state door machine: CLOSED -> OPENING -> OPEN -> WAITING_TO_CLOSE -> CLOSING ->
//     CLOSED. switchToState(dst) is a no-op when dst == the current state; otherwise it
//     clears the door's model-condition flags, sets the destination's flag (CLOSED sets
//     none), fires the destination's FX at the object's position (unoriented, GPL
//     FXList::doFXPos), and computes the NEW m_timeoutFrame / m_timeoutState pair:
//       * OPENING:          timeoutFrame = specialPowerReadyFrame - 1 (finish one frame
//                            BEFORE the power is ready); timeoutState = OPEN.
//       * OPEN:             no timeout (0); timeoutState = OPEN (inert self-loop).
//       * WAITING_TO_CLOSE: timeoutFrame = now + DoorWaitOpenTime; timeoutState = CLOSING.
//       * CLOSING:          timeoutFrame = now + DoorClosingTime, clamped to at most
//                            now + (specialPowerReadyFrame - now) / 2 so a closing door
//                            never eats more than half the time left before the power is
//                            ready again; timeoutState = CLOSED.
//       * CLOSED:           no timeout (0); timeoutState = CLOSED.
//   - update() each frame: under OBJECT_STATUS_UNDER_CONSTRUCTION, do nothing (GPL's own
//     comment: the special power module may not exist yet under construction, which would
//     read as "ready at frame 0" and pop the door open early - skip the whole decision
//     rather than let that happen). Otherwise: an expired timeout (timeoutFrame != 0 and
//     now > timeoutFrame) switches to timeoutState; then, in this order: (1) if the door
//     is not OPEN and the power is ready (now >= specialPowerReadyFrame), FORCE it to OPEN
//     (the "pop the door open" catch-up branch - GPL's own DEBUG_LOG names this a
//     should-be-rare correction); else (2) if the door is CLOSED and now has reached
//     whenToStartOpening = (readyFrame >= DoorOpenTime) ? readyFrame - DoorOpenTime : 0,
//     switch to OPENING.
//   - initiateIntentToDoSpecialPower(templateName): a no-op (returns false) unless
//     templateName matches this module's own SpecialPowerTemplate; otherwise switches to
//     WAITING_TO_CLOSE and returns true. GPL's is-the-door-actually-open check here is a
//     DEBUG_ASSERTCRASH (diagnostic only, never a behavior gate), so it is not reproduced
//     as a runtime branch.
//
// MIGRATION NOTE (ready-frame input, mirrors SPCD-1's shape): GPL discovers its special
// power module via a same-object sibling lookup (getObject()->getSpecialPowerModule(...))
// and reads its cached getReadyFrame()/isReady() every update. OpenSAGE has no ported
// special-power reload-timer module yet, and ISimContext's frozen member list carries no
// special-power query (deliberately: audio/rendering/UI-adjacent subsystems are the
// examples named at that seam's top, and special-power reload timing has not grown a
// member there). Until that system lands, the ready frame is a DRIVEN INPUT:
// <see cref="NotifySpecialPowerReadyFrame"/> is the seam a future special-power timer
// module calls whenever its own ready frame changes (identical in shape to
// SpecialPowerCompletionDie's externally-driven SetCreator). A door that is never notified
// behaves exactly like GPL's own documented quirk for an uninitialized special power module
// (ready frame 0, i.e. "ready" from the first frame) - not invented, GPL names this exact
// case in its update() comment.
//
// UNMODELED (documented gap, not invented): DoorOpenIdleAudio is a raw audio-event asset
// reference (an AudioEventRTS name), not a per-object UnitSpecificSounds key or the single
// global MiscAudio "free unit" sting - neither shape ISimEvents exposes today
// (FireUnitSoundAtObject is key-based; FireCrateFreeUnitPickupSound is the one global
// sting). The data field is parsed and held for when a matching seam member exists, but is
// not fired here.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

/// <summary>
/// Allows the use of the DOOR_1_WAITING_OPEN, DOOR_1_CLOSING, DOOR_1_OPENING model condition states.
/// </summary>
[SimState]
public sealed class MissileLauncherBuildingUpdate : UpdateModule
{
    private readonly MissileLauncherBuildingUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private DoorState _doorState;
    private DoorState _timeoutState;

    /// <summary>Frame the current state times out at; zero means "no pending timeout" (GPL's own 0 sentinel).</summary>
    private LogicFrame _timeoutFrame;

    /// <summary>
    /// The associated special power's ready frame, as last reported through
    /// <see cref="NotifySpecialPowerReadyFrame"/> (see the MIGRATION NOTE above). Zero
    /// (the default) reproduces GPL's own "uninitialized special power module" quirk: ready
    /// from frame zero.
    /// </summary>
    private LogicFrame _specialPowerReadyFrame;

    public MissileLauncherBuildingUpdate(GameObject gameObject, ISimContext context, MissileLauncherBuildingUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _doorState = DoorState.Closed;
        _timeoutState = DoorState.Closed;

        // GPL update() ticks every frame (UPDATE_SLEEP_NONE).
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// Records the associated special power's current ready frame (see the MIGRATION NOTE
    /// at the top of this file). Called by whatever owns the power's reload timer whenever
    /// that ready frame changes.
    /// </summary>
    public void NotifySpecialPowerReadyFrame(LogicFrame readyFrame)
    {
        _specialPowerReadyFrame = readyFrame;
    }

    /// <summary>
    /// GPL SpecialPowerUpdateInterface::initiateIntentToDoSpecialPower: only this module's
    /// own special power (matched by template name) may fire it. Starts the WAITING_TO_CLOSE
    /// -> CLOSING -> CLOSED shutdown sequence and reports whether this module owned the call.
    /// </summary>
    public bool InitiateIntentToDoSpecialPower(string specialPowerTemplateName)
    {
        if (_data.SpecialPowerTemplate != specialPowerTemplateName)
        {
            return false;
        }

        SwitchToState(DoorState.WaitingToClose);
        return true;
    }

    public override UpdateSleepTime Update()
    {
        // GPL: under construction, any decision about door status could be wrong (a special
        // power module that has not finished initializing reads as "ready at frame 0").
        if (GameObject.TestStatus(ObjectStatus.UnderConstruction))
        {
            return UpdateSleepTime.None;
        }

        var now = Context.CurrentFrame;

        if (_timeoutFrame != LogicFrame.Zero && now > _timeoutFrame)
        {
            SwitchToState(_timeoutState);
        }

        var readyFrame = _specialPowerReadyFrame;
        var whenToStartOpening = readyFrame.Value >= (uint)_data.DoorOpenTime
            ? readyFrame - (uint)_data.DoorOpenTime
            : LogicFrame.Zero;

        if (_doorState != DoorState.Open && now >= readyFrame)
        {
            // "Had to POP the door open" catch-up branch (GPL DEBUG_LOG): the power became
            // ready before the timed OPENING->OPEN transition caught up.
            SwitchToState(DoorState.Open);
        }
        else if (_doorState == DoorState.Closed && now >= whenToStartOpening)
        {
            SwitchToState(DoorState.Opening);
        }

        return UpdateSleepTime.None;
    }

    /// <summary>GPL switchToState: model-condition flags, FX, and the next timeout for one door transition.</summary>
    private void SwitchToState(DoorState destination)
    {
        if (_doorState == destination)
        {
            return;
        }

        var now = Context.CurrentFrame;

        // Every destination clears the full door flag set first (GPL clears whichever subset
        // does not include its own destination flag; clearing all four and then setting the
        // destination's flag is the same net result, since a flag that was already clear
        // stays clear either way - no per-frame observer sees the intermediate state).
        GameObject.ClearModelConditionState(ModelConditionFlag.Door1Opening);
        GameObject.ClearModelConditionState(ModelConditionFlag.Door1WaitingOpen);
        GameObject.ClearModelConditionState(ModelConditionFlag.Door1WaitingToClose);
        GameObject.ClearModelConditionState(ModelConditionFlag.Door1Closing);

        switch (destination)
        {
            case DoorState.Closed:
                _timeoutFrame = LogicFrame.Zero;
                _timeoutState = DoorState.Closed;
                break;

            case DoorState.Opening:
                GameObject.SetModelConditionState(ModelConditionFlag.Door1Opening);
                // "we want this to be done BEFORE the power is ready, so end it one frame
                // ahead" (GPL). A ready frame of 0 has nothing to end ahead of.
                _timeoutFrame = _specialPowerReadyFrame.Value > 0
                    ? _specialPowerReadyFrame - 1
                    : LogicFrame.Zero;
                _timeoutState = DoorState.Open;
                FireFx(_data.DoorOpeningFX);
                break;

            case DoorState.Open:
                GameObject.SetModelConditionState(ModelConditionFlag.Door1WaitingOpen);
                _timeoutFrame = LogicFrame.Zero;
                _timeoutState = DoorState.Open;
                break;

            case DoorState.WaitingToClose:
                GameObject.SetModelConditionState(ModelConditionFlag.Door1WaitingToClose);
                _timeoutFrame = now + new LogicFrameSpan((uint)_data.DoorWaitOpenTime);
                _timeoutState = DoorState.Closing;
                FireFx(_data.DoorWaitingToCloseFX);
                break;

            case DoorState.Closing:
                GameObject.SetModelConditionState(ModelConditionFlag.Door1Closing);
                var timeoutFrame = now + new LogicFrameSpan((uint)_data.DoorCloseTime);

                // GPL clamp: never let closing eat more than half the time remaining before
                // the power is ready again.
                var delta = _specialPowerReadyFrame.Value > now.Value
                    ? _specialPowerReadyFrame.Value - now.Value
                    : 0u;
                var halfwayFrame = new LogicFrame(now.Value + delta / 2);
                if (timeoutFrame > halfwayFrame)
                {
                    timeoutFrame = halfwayFrame;
                }

                _timeoutFrame = timeoutFrame;
                _timeoutState = DoorState.Closed;
                break;
        }

        _doorState = destination;
    }

    private void FireFx(string fxListName)
    {
        if (string.IsNullOrEmpty(fxListName))
        {
            return;
        }

        // GPL FXList::doFXPos: position only, unoriented.
        Context.Events.FireFXAtObjectPosition(fxListName, GameObject.Id);
    }

    private enum DoorState
    {
        Closed,
        Opening,
        Open,
        WaitingToClose,
        Closing,
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.
    //
    // Tolerances (ruling A3): the two state enums and the ready-frame input are lifecycle/
    // identity facts, so Exact (ch.1/6). The timeout frame is a timer, so Quantum (ch.2),
    // matching XferFrame's own default.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("DoorState", ref _doorState);
        xfer.XferEnum("TimeoutState", ref _timeoutState);
        xfer.XferFrame("TimeoutFrame", ref _timeoutFrame);
        xfer.XferFrame("SpecialPowerReadyFrame", ref _specialPowerReadyFrame, Tolerance.Exact);
    }
}

[SimDataAudited]
public sealed class MissileLauncherBuildingUpdateModuleData : UpdateModuleData
{
    internal static MissileLauncherBuildingUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<MissileLauncherBuildingUpdateModuleData> FieldParseTable = new IniParseTable<MissileLauncherBuildingUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },

        { "DoorOpenTime", (parser, x) => x.DoorOpenTime = parser.ParseInteger() },
        { "DoorWaitOpenTime", (parser, x) => x.DoorWaitOpenTime = parser.ParseInteger() },
        { "DoorCloseTime", (parser, x) => x.DoorCloseTime = parser.ParseInteger() },

        { "DoorOpeningFX", (parser, x) => x.DoorOpeningFX = parser.ParseAssetReference() },
        { "DoorWaitingToCloseFX", (parser, x) => x.DoorWaitingToCloseFX = parser.ParseAssetReference() },

        { "DoorOpenIdleAudio", (parser, x) => x.DoorOpenIdleAudio = parser.ParseAssetReference() }
    };

    public string SpecialPowerTemplate { get; private set; }

    public int DoorOpenTime { get; private set; }
    public int DoorWaitOpenTime { get; private set; }
    public int DoorCloseTime { get; private set; }

    public string DoorOpeningFX { get; private set; }
    public string DoorWaitingToCloseFX { get; private set; }

    /// <summary>Parsed and held; not currently firable - see the UNMODELED note at the top of this file.</summary>
    public string DoorOpenIdleAudio { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new MissileLauncherBuildingUpdate(gameObject, gameEngine.SimContext, this);
    }
}
