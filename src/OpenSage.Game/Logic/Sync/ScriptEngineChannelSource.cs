// S8 script-engine runtime (subset) — CRC/persist channel source.
//
// The F8 channel walk is frozen with no dedicated Script channel; the original persists its
// script engine inside the GameLogic snapshot and CRC-covers it through the same whole-state
// fold. INTERIM ASSIGNMENT (recorded finding SR-F3): our script state rides the AI channel —
// scripting is the AI-side subsystem in the original's architecture, the channel is otherwise
// unused today, and the per-channel vector still localizes a divergence to "script/AI" for
// F14 triage. If a real AI system later wants the channel to itself, the ruling for a v2
// channel split happens in the freeze doc, not here.

using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Sync;

internal sealed class ScriptEngineChannelSource : ICrcChannelSource
{
    private readonly OpenSage.Logic.Script.SimScriptEngine _engine;

    internal ScriptEngineChannelSource(OpenSage.Logic.Script.SimScriptEngine engine)
    {
        _engine = engine;
    }

    public CrcChannel Channel => CrcChannel.AI;

    public bool IsActive => true;

    public void Xfer(IXfer xfer) => _engine.Xfer(xfer);
}
