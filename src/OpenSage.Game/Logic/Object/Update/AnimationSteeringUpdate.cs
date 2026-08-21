// AnimationSteeringUpdate - R12 port. Behavioral reference (semantics only):
// generals-gpl GeneralsMD GameLogic/Module/AnimationSteeringUpdate.h/.cpp. Purely a
// visual state machine: it polls the object's SimLocomotorUpdate physics turning
// direction and drives the drawable's steering model-condition flags (the
// CENTER_TO_LEFT/RIGHT <-> LEFT/RIGHT_TO_CENTER animation set), gated by a minimum
// dwell time between transitions. No gameplay-visible sim state is produced by this
// module (its own current-animation/next-transition-frame bookkeeping is visual-only),
// but it is still walked through Xfer like any other module so save/load reproduces the
// same animation on resume (the GPL original does not xfer these two fields explicitly
// beyond the base class - not reproduced here, since a bare-base xfer would desync the
// displayed animation across a save/load boundary for no conformance benefit).
//
// State machine (GPL update(), faithfully translated):
//   straight (null) --turn left/right--> CENTER_TO_LEFT / CENTER_TO_RIGHT
//   CENTER_TO_LEFT   --turn stops being left-->  LEFT_TO_CENTER
//   CENTER_TO_RIGHT  --turn stops being right--> RIGHT_TO_CENTER
//   LEFT_TO_CENTER / RIGHT_TO_CENTER --turn == NONE--> straight (null)
// Any other turning value observed while in a *_TO_CENTER state (including the opposite
// turn direction) is ignored: the model must finish recentering before a new turn can
// start (GPL has no case for it, so nothing happens until TURN_NONE is observed).

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AnimationSteeringUpdate : UpdateModule
{
    private readonly AnimationSteeringUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory) ----
    // null == GPL MODELCONDITION_INVALID (currently going straight).
    private ModelConditionFlag? _currentTurnAnim;
    private LogicFrame _nextTransitionFrame;

    public AnimationSteeringUpdate(GameObject gameObject, ISimContext context, AnimationSteeringUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        var drawable = GameObject.Drawable;
        var physicsUpdate = GameObject.FindBehavior<SimLocomotorUpdate>();
        var now = Context.CurrentFrame;

        if (drawable != null && physicsUpdate != null && now >= _nextTransitionFrame)
        {
            var currentTurn = physicsUpdate.Physics.Turning;

            switch (_currentTurnAnim)
            {
                case null:
                    // We're currently going straight. Check if we want to turn.
                    if (currentTurn == PhysicsTurningType.Negative)
                    {
                        // Initiate a right turn.
                        GameObject.ModelConditionFlags.Set(ModelConditionFlag.CenterToRight, true);
                        _nextTransitionFrame = now + _data.MinTransitionTime;
                        _currentTurnAnim = ModelConditionFlag.CenterToRight;
                    }
                    else if (currentTurn == PhysicsTurningType.Positive)
                    {
                        // Initiate a left turn.
                        GameObject.ModelConditionFlags.Set(ModelConditionFlag.CenterToLeft, true);
                        _nextTransitionFrame = now + _data.MinTransitionTime;
                        _currentTurnAnim = ModelConditionFlag.CenterToLeft;
                    }
                    break;

                case ModelConditionFlag.CenterToRight:
                    // We're currently initiating a turn to the right. The only thing we
                    // can do is go back to center or maintain the turn.
                    if (currentTurn != PhysicsTurningType.Negative)
                    {
                        // Recenter!
                        GameObject.ModelConditionFlags.Set(ModelConditionFlag.CenterToRight, false);
                        GameObject.ModelConditionFlags.Set(ModelConditionFlag.RightToCenter, true);
                        _nextTransitionFrame = now + _data.MinTransitionTime;
                        _currentTurnAnim = ModelConditionFlag.RightToCenter;
                    }
                    break;

                case ModelConditionFlag.CenterToLeft:
                    // We're currently initiating a turn to the left. The only thing we
                    // can do is go back to center or maintain the turn.
                    if (currentTurn != PhysicsTurningType.Positive)
                    {
                        // Recenter!
                        GameObject.ModelConditionFlags.Set(ModelConditionFlag.CenterToLeft, false);
                        GameObject.ModelConditionFlags.Set(ModelConditionFlag.LeftToCenter, true);
                        _nextTransitionFrame = now + _data.MinTransitionTime;
                        _currentTurnAnim = ModelConditionFlag.LeftToCenter;
                    }
                    break;

                case ModelConditionFlag.LeftToCenter:
                case ModelConditionFlag.RightToCenter:
                    if (currentTurn == PhysicsTurningType.None)
                    {
                        // Finish the turn.
                        GameObject.ModelConditionFlags.Set(ModelConditionFlag.LeftToCenter, false);
                        GameObject.ModelConditionFlags.Set(ModelConditionFlag.RightToCenter, false);
                        _nextTransitionFrame = now;
                        _currentTurnAnim = null;
                    }
                    break;

                default:
                    // Unreachable: only the four states above are ever assigned.
                    break;
            }
        }

        return UpdateSleepTime.None;
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);

        var hasTurnAnim = _currentTurnAnim.HasValue;
        xfer.XferBool("HasTurnAnim", ref hasTurnAnim);
        if (hasTurnAnim)
        {
            var turnAnim = _currentTurnAnim.GetValueOrDefault();
            xfer.XferEnum("TurnAnim", ref turnAnim);
            _currentTurnAnim = turnAnim;
        }
        else if (xfer.Mode == XferMode.Load)
        {
            _currentTurnAnim = null;
        }

        xfer.XferFrame("NextTransitionFrame", ref _nextTransitionFrame);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.CncGeneralsZeroHour)]
[SimDataAudited]
public sealed class AnimationSteeringUpdateModuleData : UpdateModuleData
{
    internal static AnimationSteeringUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<AnimationSteeringUpdateModuleData> FieldParseTable = new IniParseTable<AnimationSteeringUpdateModuleData>
    {
        { "MinTransitionTime", (parser, x) => x.MinTransitionTime = parser.ParseTimeMillisecondsToLogicFrames() }
    };

    public LogicFrameSpan MinTransitionTime { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AnimationSteeringUpdate(gameObject, gameEngine.SimContext, this);
    }
}
