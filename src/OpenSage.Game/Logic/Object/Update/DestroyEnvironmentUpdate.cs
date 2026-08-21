// DestroyEnvironmentUpdate - R13 port (data-derivable, no GPL sibling; see
// bfme2-workbench/research/modules-r13/specs/DestroyEnvironmentUpdateModuleData.md).
//
// Fields (StartTime, DestructionTime) fully pin the semantics: the object is killed at
// now(creation) + StartTime + DestructionTime. Unlike its file-neighbors StructureCollapseUpdate
// (OCL/FXList/CollapseHeight/DestroyObjectWhenDone) and ToppleUpdate (ToppleFX/StumpName), this
// module has no asset-reference, FX, or model-condition field of any kind, so there is nothing
// authored to drive a mid-sequence visual transition at the StartTime checkpoint.
//
// FINDING F-DEU-1 (filed, not invented around): no field on this module names an OCL, FXList, or
// ModelConditionFlag to drive during [StartTime, StartTime+DestructionTime). This port tracks
// StartTime as real timer state (_startFrame) so a future revision has it available the moment a
// driving field is discovered, but adds no invented visual/state-flag behavior at that checkpoint
// today.
//
// Shape follows EmpUpdate.cs (two-phase authored-duration timer -> runtime frame state ->
// GameObject.Kill(), [SimState]/ISimContext/Xfer throughout), not the audit's original
// StructureCollapseUpdate/ToppleUpdate citation - see spec §0 for why those two are wrong-shape
// exemplars.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class DestroyEnvironmentUpdate : UpdateModule
{
    private readonly DestroyEnvironmentUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Frame the destruction sequence begins (F-DEU-1: currently no in-module effect
    /// at this checkpoint; tracked for Xfer fidelity and for the day a driving field lands).</summary>
    private LogicFrame _startFrame;

    /// <summary>Frame the object is killed (StartFrame + DestructionTime).</summary>
    private LogicFrame _destroyFrame;

    public DestroyEnvironmentUpdate(GameObject gameObject, ISimContext context, DestroyEnvironmentUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        var now = Context.CurrentFrame;
        _startFrame = now + data.StartTime;
        _destroyFrame = _startFrame + data.DestructionTime;

        // No SetWakeFrame call: GameLogic.CreateObject floors an unset NextCallFrame to "now"
        // exactly like a module that "didn't bother to call SetWakeFrame", and the first
        // Update() sleeps itself straight to _destroyFrame (below) - there is no per-frame work
        // to do before then (F-DEU-1), so this module does not tick every frame the way
        // EmpUpdate must (EmpUpdate has a continuous scale blend; this module has none).
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        if (now < _destroyFrame)
        {
            // Sleep straight to the destroy frame in one hop (sleepy-update idiom) rather than
            // ticking every frame - nothing observable happens in between (F-DEU-1).
            return UpdateSleepTime.Frames(_destroyFrame - now);
        }

        GameObject.Kill();
        _destroyFrame = LogicFrame.MaxValue; // guard against a repeat kill (LifetimeUpdate/EmpUpdate's own defensive shape)
        return UpdateSleepTime.Forever;
    }

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrame("StartFrame", ref _startFrame, Tolerance.Quantum);
        xfer.XferFrame("DestroyFrame", ref _destroyFrame, Tolerance.Quantum);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Kills the owning object after StartTime + DestructionTime frames have elapsed since
/// creation. No FX/OCL/model-condition field exists on this module (F-DEU-1), so the two
/// authored durations drive nothing but the single kill event.
/// </summary>
[SimDataAudited]
[AddedIn(SageGame.Bfme)]
public sealed class DestroyEnvironmentUpdateModuleData : UpdateModuleData
{
    internal static DestroyEnvironmentUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<DestroyEnvironmentUpdateModuleData> FieldParseTable = new IniParseTable<DestroyEnvironmentUpdateModuleData>
    {
        { "StartTime", (parser, x) => x.StartTime = parser.ParseDurationLogicFrames() },
        { "DestructionTime", (parser, x) => x.DestructionTime = parser.ParseDurationLogicFrames() },
    };

    /// <summary>Frames after creation before the destruction sequence begins (ms in INI,
    /// ceil-quantized at parse, S5). F-DEU-1: no in-module effect at this checkpoint today.</summary>
    public LogicFrameSpan StartTime { get; private set; }

    /// <summary>Frames the destruction sequence itself takes, once begun (ms in INI,
    /// ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan DestructionTime { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DestroyEnvironmentUpdate(gameObject, gameEngine.SimContext, this);
    }
}
