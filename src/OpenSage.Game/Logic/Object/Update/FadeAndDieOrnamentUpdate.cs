// FadeAndDieOrnamentUpdate - R12 port. BFME-only (no generals-gpl sibling: the module is
// AddedIn(SageGame.Bfme)) and no clean-room spec exists in bfme2-workbench/research/. The
// field grammar is the standard ADSR envelope shape already ported (client-side, float) for
// W3dLaserDraw's alpha-over-lifetime Envelope block: InitialOpacity holds through
// InitialDelay, ramps to PeakOpacity over AttackTime (attack), eases to SustainOpacity over
// DecayTime (decay), holds SustainOpacity for SustainTime (sustain), then fades to zero over
// ReleaseTime (release) - the mechanical ADSR formula is not sim-specific invention, only its
// application to an ornamental object's lifetime is BFME's.
//
// Sim-visible surface: opacity itself is a render-only quantity (S8: rendering is
// deliberately absent from ISimContext), so this module carries no opacity output - only the
// timeline. The ONE sim-visible effect is that the object is destroyed once the release stage
// completes (GPL "FadeAndDie": the ornament goes away when it finishes fading), which is why
// this needs a real runtime module rather than the LargeGroupAudioUpdate parked-empty
// pattern - object lifetime is sim-observable (partition queries, object counts) even though
// the fade itself is not. CurrentOpacity is exposed as a pure, stateless function of the
// stored spawn frame + the module data timeline for tests and for a future client-side draw
// module to sample; it is not sim state and carries no Xfer entry of its own.
//
// TODO-spec (unverified): whether the retail engine snaps stage boundaries with zero-length
// stages (AttackTime/DecayTime/ReleaseTime = 0) instantaneously or holds one frame is
// unrecovered; this port treats a zero-length stage as an instantaneous transition (Frac
// returns 1 when the stage span is zero), the natural reading of the ADSR shape.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class FadeAndDieOrnamentUpdate : UpdateModule
{
    private readonly FadeAndDieOrnamentUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory) ----
    private LogicFrame _spawnFrame;

    // ---- derived timeline (pure function of _spawnFrame + _data; not independently Xfered) ----
    private LogicFrame DelayEndFrame => _spawnFrame + _data.Envelope.InitialDelay;
    private LogicFrame AttackEndFrame => DelayEndFrame + _data.Envelope.AttackTime;
    private LogicFrame DecayEndFrame => AttackEndFrame + _data.Envelope.DecayTime;
    private LogicFrame SustainEndFrame => DecayEndFrame + _data.Envelope.SustainTime;
    private LogicFrame ReleaseEndFrame => SustainEndFrame + _data.Envelope.ReleaseTime;

    public FadeAndDieOrnamentUpdate(GameObject gameObject, ISimContext context, FadeAndDieOrnamentUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _spawnFrame = context.CurrentFrame;
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>
    /// The envelope's opacity at the current frame (spec: standard ADSR shape). Render-only
    /// output (S8) - exposed for tests and a future draw-module sampler, never a sim input.
    /// </summary>
    public Fix64 CurrentOpacity => OpacityAtFrame(Context.CurrentFrame);

    internal Fix64 OpacityAtFrame(LogicFrame now)
    {
        var envelope = _data.Envelope;

        if (now < DelayEndFrame)
        {
            return envelope.InitialOpacity;
        }
        if (now < AttackEndFrame)
        {
            return Lerp(envelope.InitialOpacity, envelope.PeakOpacity, Frac(now, DelayEndFrame, AttackEndFrame));
        }
        if (now < DecayEndFrame)
        {
            return Lerp(envelope.PeakOpacity, envelope.SustainOpacity, Frac(now, AttackEndFrame, DecayEndFrame));
        }
        if (now < SustainEndFrame)
        {
            return envelope.SustainOpacity;
        }
        if (now < ReleaseEndFrame)
        {
            return Lerp(envelope.SustainOpacity, Fix64.Zero, Frac(now, SustainEndFrame, ReleaseEndFrame));
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
        if (Context.CurrentFrame >= ReleaseEndFrame)
        {
            // The envelope has fully released: the ornament's fade is done, so it goes away
            // (GPL "FadeAndDie" naming).
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
public sealed class FadeAndDieOrnamentUpdateModuleData : UpdateModuleData
{
    internal static FadeAndDieOrnamentUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<FadeAndDieOrnamentUpdateModuleData> FieldParseTable = new IniParseTable<FadeAndDieOrnamentUpdateModuleData>
    {
        { "Envelope", (parser, x) => x.Envelope = FadeAndDieOrnamentEnvelope.Parse(parser) },
    };

    public FadeAndDieOrnamentEnvelope Envelope { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new FadeAndDieOrnamentUpdate(gameObject, gameEngine.SimContext, this);
    }
}

/// <summary>
/// The Fix64/LogicFrameSpan-quantized ADSR envelope for <see cref="FadeAndDieOrnamentUpdate"/>.
/// Deliberately a distinct type from the client-side float <c>Envelope</c> (W3dLaserDraw's
/// alpha-over-lifetime block, same field grammar, same "Envelope" INI keyword) - that type is
/// DrawModule/render substrate and stays float; this one is [SimState] substrate and is
/// Fix64/LogicFrameSpan end to end (no float anywhere on this surface, S5).
/// </summary>
[AddedIn(SageGame.Bfme)]
public sealed class FadeAndDieOrnamentEnvelope
{
    internal static FadeAndDieOrnamentEnvelope Parse(IniParser parser)
    {
        return new FadeAndDieOrnamentEnvelope
        {
            InitialOpacity = parser.ParseAttribute("InitialOpacity", parser.ScanFix64),
            PeakOpacity = parser.ParseAttribute("PeakOpacity", parser.ScanFix64),
            SustainOpacity = parser.ParseAttribute("SustainOpacity", parser.ScanFix64),
            InitialDelay = parser.ParseAttribute("InitialDelay", parser.ScanDurationLogicFrames),
            AttackTime = parser.ParseAttribute("AttackTime", parser.ScanDurationLogicFrames),
            DecayTime = parser.ParseAttribute("DecayTime", parser.ScanDurationLogicFrames),
            SustainTime = parser.ParseAttribute("SustainTime", parser.ScanDurationLogicFrames),
            ReleaseTime = parser.ParseAttribute("ReleaseTime", parser.ScanDurationLogicFrames),
        };
    }

    public Fix64 InitialOpacity { get; private set; }
    public Fix64 PeakOpacity { get; private set; }
    public Fix64 SustainOpacity { get; private set; }
    public LogicFrameSpan InitialDelay { get; private set; }
    public LogicFrameSpan AttackTime { get; private set; }
    public LogicFrameSpan DecayTime { get; private set; }
    public LogicFrameSpan SustainTime { get; private set; }
    public LogicFrameSpan ReleaseTime { get; private set; }
}
