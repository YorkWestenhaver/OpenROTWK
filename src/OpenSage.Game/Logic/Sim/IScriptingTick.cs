// R15 packet 3 ("one clock", workbench research/design-sim-presentation-bridge.md §2 packet 3):
// the scripting engine's per-frame entry point, narrowed to the one method the frame driver
// needs.
//
// Before packet 3 the headed game ran ScriptingSystem.ScriptingTick() off a SECOND wall-clock
// accumulator in Game.Update, independent of the logic accumulator. Every shipped game has
// ScriptingTicksPerSecond == LogicFramesPerSecond (5 for the BFME family, 30 for Generals /
// Zero Hour - IGameDefinition implementations vs SageGameExtensions.LogicFramesPerSecond), so
// the two accumulators were drifting copies of one cadence and the drift was pure desync risk.
// The tick now runs inside a phase, once per logic frame, and this interface is how it gets
// there.

namespace OpenSage.Logic.Sim;

/// <summary>
/// One scripting evaluation pass, run once per logic frame at the head of
/// <c>SimPhase.ModuleUpdate</c>. Implemented by <c>ScriptingSystem</c>; existing as an
/// interface at all is what lets a render-free host observe the call without standing up the
/// whole scripting engine.
/// </summary>
internal interface IScriptingTick
{
    void ScriptingTick();
}
