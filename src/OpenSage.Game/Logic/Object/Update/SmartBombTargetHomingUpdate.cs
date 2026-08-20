// SmartBombTargetHomingUpdate - R12 port. Behavioral reference (semantics only):
// generals-gpl GeneralsMD GameLogic/Module/SmartBombTargetHomingUpdate.h/.cpp. Nudges a
// falling projectile's X/Y position each frame toward a designated target coordinate, an
// exponential-decay pull rather than a full physics steer:
//   newPos.xy = target.xy * (1 - scalar) + currentPos.xy * scalar
// Z is left untouched (vertical fall/momentum is someone else's job - the locomotor/physics
// pass, if any is attached to the same object). The module is otherwise a pass-through: no
// target set yet, or the object not "significantly above terrain" (Thing::isSignificantlyAboveTerrain,
// GameObject.IsSignificantlyAboveTerrain here), and update() is a same-position no-op.
//
// GPL update() (translated verbatim):
//   if (!m_targetReceived) return UPDATE_SLEEP_NONE;
//   if (!self->isSignificantlyAboveTerrain()) return UPDATE_SLEEP_NONE;
//   statusCoeff = clamp(d->m_courseCorrectionScalar, 0, 1); targetCoeff = 1 - statusCoeff;
//   pos.x = target.x * targetCoeff + current.x * statusCoeff;
//   pos.y = target.y * targetCoeff + current.y * statusCoeff;
//   pos.z = current.z;
//   self->setPosition(&pos);
//
// GPL SetTargetPosition() DEBUG_ASSERTCRASHes on a zero-length target ("received a zero
// coord") and then unconditionally returns without touching m_target/m_targetReceived - the
// assert is a debug-build-only diagnostic, never a hard failure in the shipped build, so the
// translated behavior is: reject a zero target silently, preserving whatever target (or
// absence of one) was already recorded (TC6).
//
// This module owns no locomotor/physics relationship; it reads/writes the object's transform
// directly through the SimTransformBridge float-substrate crossing (D-7 boundary pattern),
// the same seam UnitCrateCollide and SimHordeMember use for a one-shot position read/write
// outside the locomotor system.

using OpenSage.Data.Ini;
using OpenSage.Logic.Object.Locomotion;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SmartBombTargetHomingUpdate : UpdateModule
{
    private readonly SmartBombTargetHomingUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----
    private bool _targetReceived;
    private FixVector3 _target;

    public SmartBombTargetHomingUpdate(GameObject gameObject, ISimContext context, SmartBombTargetHomingUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// GPL SetTargetPosition. A zero-length target is rejected (DEBUG_ASSERTCRASH in the
    /// original, silent no-op in the shipped build): the previous target (or lack of one) is
    /// left untouched (TC6).
    /// </summary>
    public void SetTargetPosition(in FixVector3 target)
    {
        if (target == FixVector3.Zero)
        {
            return;
        }

        _target = target;
        _targetReceived = true;
    }

    public override UpdateSleepTime Update()
    {
        if (!_targetReceived)
        {
            return UpdateSleepTime.None;
        }

        if (!GameObject.IsSignificantlyAboveTerrain)
        {
            return UpdateSleepTime.None;
        }

        var currentPos = SimTransformBridge.PullPosition(GameObject);
        var yaw = SimTransformBridge.PullYaw(GameObject);

        var statusCoeff = FixMath.Clamp(_data.CourseCorrectionScalar, Fix64.Zero, Fix64.One);
        var targetCoeff = Fix64.One - statusCoeff;

        var newPos = new FixVector3(
            _target.X * targetCoeff + currentPos.X * statusCoeff,
            _target.Y * targetCoeff + currentPos.Y * statusCoeff,
            currentPos.Z);

        SimTransformBridge.Push(GameObject, newPos, yaw);

        return UpdateSleepTime.None;
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("TargetReceived", ref _targetReceived);
        xfer.XferFixVector3("Target", ref _target, Tolerance.Band);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Dynamically adjusts the "bomb" so it hits its designated target instead of missing it.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
[SimDataAudited]
public sealed class SmartBombTargetHomingUpdateModuleData : UpdateModuleData
{
    internal static SmartBombTargetHomingUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<SmartBombTargetHomingUpdateModuleData> FieldParseTable = new IniParseTable<SmartBombTargetHomingUpdateModuleData>
    {
        { "CourseCorrectionScalar", (parser, x) => x.CourseCorrectionScalar = parser.ParseFix64() },
    };

    public Fix64 CourseCorrectionScalar { get; private set; } = Fix64.FromDecimalLiteral("0.99");

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SmartBombTargetHomingUpdate(gameObject, gameEngine.SimContext, this);
    }
}
