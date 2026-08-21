// OathbreakersFadeAwayBehavior - R13 port. GPL grounding (idiom, not class-name match):
// generals-gpl/GeneralsMD/Code/GameEngine/Include/GameLogic/Module/SlowDeathBehavior.h:27 -
// "Update that will count down a lifetime and destroy object when it reaches zero" - the
// canonical countdown-then-destroy shape, repeated at SlowDeathBehavior.cpp:246,401,444,473
// (each a TheGameLogic->destroyObject(obj) reached only after a phase/frame countdown
// completes, same family as JetSlowDeathBehavior.cpp / HelicopterSlowDeathUpdate.cpp).
//
// Data-derivation for the field: FadeOutTime has no companion field on this module (no
// start-opacity, end-opacity, easing curve, or destroy-delay-after-fade field). The
// self-descriptive name plus single-field shape reads as: on module creation, opacity ramps
// from One (fully visible) to Zero (invisible) over FadeOutTime; when the ramp completes, the
// object is destroyed - the GPL fade-then-destroy idiom applied with the engine's own landed
// fade-ramp primitive (HordeSiegeEngineContain.OpacityAtFrame, FadeAndDieOrnamentUpdate's
// linear Frac/Lerp shape), not anything new. See
// bfme2-workbench/research/modules-r13/specs/OathbreakersFadeAwayBehaviorModuleData.md.
//
// Trigger condition (out of scope for this module): what causes an Oathbreakers unit to gain
// this behavior's effect (e.g. an oath-related death/desertion event) is object-definition
// wiring elsewhere (whatever module/DieModule construct grants/attaches this behavior at the
// right moment), not this module's own logic - this module's contract is unconditional: from
// construction it ramps opacity down over FadeOutTime and destroys the object at the end.
//
// Zero-length-span convention (S5 default, already established by both R12 fade siblings): a
// FadeOutTime of 0 reads as immediately elapsed - Frac returns Fix64.One for a zero-length
// span - so the object destroys effectively on its first Update() tick.
//
// What is NOT invented: no easing curve (Fix64 linear ramp only, matching both R12 siblings),
// no separate destroy-delay-after-fade-completes field (GPL's destroyObject calls fire
// directly on countdown completion), no fade-in stage (name is "FadeAway", one field, one
// direction).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class OathbreakersFadeAwayBehavior : UpdateModule
{
    private readonly OathbreakersFadeAwayBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory) ----
    private LogicFrame _spawnFrame;

    // ---- derived timeline (pure function of _spawnFrame + _data; not independently Xfered) ----
    private LogicFrame EndFrame => _spawnFrame + _data.FadeOutTime;

    public OathbreakersFadeAwayBehavior(GameObject gameObject, ISimContext context, OathbreakersFadeAwayBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _spawnFrame = context.CurrentFrame;
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// Opacity at the current frame: linear ramp from One (spawn) to Zero (EndFrame), then
    /// pinned at Zero. Render-only output (S8) - exposed for tests and a future draw-module
    /// sampler, never a sim input.
    /// </summary>
    public Fix64 CurrentOpacity => OpacityAtFrame(Context.CurrentFrame);

    internal Fix64 OpacityAtFrame(LogicFrame now)
    {
        if (now < _spawnFrame)
        {
            // Unreachable in practice (spawn frame is construction time); kept only for
            // symmetry with the exemplar's pre-envelope guard.
            return Fix64.One;
        }
        if (now < EndFrame)
        {
            return Lerp(Fix64.One, Fix64.Zero, Frac(now, _spawnFrame, EndFrame));
        }
        return Fix64.Zero;
    }

    private static Fix64 Lerp(Fix64 from, Fix64 to, Fix64 t) => from + (to - from) * t;

    /// <summary>Fraction of [start, end) elapsed at <paramref name="now"/>; a zero-length span is fully elapsed.</summary>
    private static Fix64 Frac(LogicFrame now, LogicFrame start, LogicFrame end)
    {
        var span = (end - start).Value;
        if (span == 0)
        {
            return Fix64.One;
        }
        var elapsed = (now - start).Value;
        return new Fix64((int)elapsed) / new Fix64((int)span);
    }

    public override UpdateSleepTime Update()
    {
        if (Context.CurrentFrame >= EndFrame)
        {
            Context.GameLogic.DestroyObject(GameObject);
            return UpdateSleepTime.Forever;
        }
        return UpdateSleepTime.None;
    }

    // ---- the single walk: one field, the spawn frame the whole timeline is anchored to ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrame("SpawnFrame", ref _spawnFrame);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class OathbreakersFadeAwayBehaviorModuleData : UpdateModuleData
{
    internal static OathbreakersFadeAwayBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly IniParseTable<OathbreakersFadeAwayBehaviorModuleData> FieldParseTable = new IniParseTable<OathbreakersFadeAwayBehaviorModuleData>
    {
        { "FadeOutTime", (parser, x) => x.FadeOutTime = parser.ParseDurationLogicFrames() },
    };

    public LogicFrameSpan FadeOutTime { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new OathbreakersFadeAwayBehavior(gameObject, gameEngine.SimContext, this);
    }
}
