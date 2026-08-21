// RubbleRiseUpdate - R13 port. No literal RubbleRiseUpdate.cpp exists in generals-gpl/
// generals-community (confirmed by exhaustive grep); the behavioral reference is the
// delay -> burst-loop -> shudder state machine of the field-name sibling
// StructureCollapseUpdate.cpp/.h, translated under the field-name remapping in
// bfme2-workbench/research/modules-r13/specs/RubbleRiseUpdateModuleData.md §1 (GPL semantics
// reference only; this is fresh code against the frozen api-freeze-v1 contract). Full spec
// citations (GPL line numbers, ISimContext surface, live-AotR-data derivation) live in that
// spec packet; this header carries only the behavior-fact summary and the findings.
//
// Behavior facts translated from StructureCollapseUpdate.cpp:
//   - ctor (translates beginStructureCollapse, L132-148, called unconditionally at creation
//     since this module is not die-gated the way the sibling is): draw _riseFrame = now +
//     GameLogicRandomValue(minDelay, maxDelay) (game-logic RNG stream); fire the Initial-phase
//     FX unconditionally (before the delay is even checked); set state to
//     WaitingForRiseStart; sleep until _riseFrame (AutoHealBehavior idiom - see F-RRU-2 for why
//     GPL's literal every-frame tick buys nothing here).
//   - Update(), WaitingForRiseStart (translates L183-206, minus the shudder - F-RRU-2): once
//     now >= _riseFrame, transition to Rising, fire the Burst-phase FX unconditionally (GPL
//     L202 - NOT the 1-in-N roll, that's the next loop's decision only), draw _burstFrame =
//     now + GameLogicRandomValue(minBurstDelay, maxBurstDelay) (GPL L204).
//   - Update(), Rising (translates L226-238 only; L213-214's height/velocity physics and
//     L241's height-based termination are NOT reproduced - F-RRU-1): once now >= _burstFrame,
//     roll GameLogicRandomValue(1, bigBurstFrequency) == 1 -> fire Burst-phase FX, else fire
//     Delay-phase FX (GPL L228-234); re-arm by ADDITION, _burstFrame += GameLogicRandomValue
//     (minBurstDelay, maxBurstDelay) (GPL L237, not "now + ..." - phase-preserving against
//     frame-processing slack). No further state transition: the burst loop repeats forever
//     (F-RRU-1).
//   - doPhaseStuff (L304-341): this ModuleData's FXLists is one string per phase (not a
//     list+count), so the random-index-pick step GPL performs is vacuous here - "fire the
//     phase's FX" reduces to "fire FXLists[phase] if present", a silent no-op otherwise (GPL's
//     null-tolerant doFXPos, L91) (F-RRU-3). No OCL field exists on this ModuleData and no live
//     data uses one, so the OCL half of doPhaseStuff is not ported.
//
// FINDINGS (behavior-fact gaps, filed not invented - same posture as EmpUpdate.cs F-EMP-#):
//   F-RRU-1 (no height/velocity physics, no termination, RubbleHeight/RubbleRiseDamping
//     unconsumed): GPL's COLLAPSING state drives m_currentHeight/m_collapseVelocity via
//     TheGlobalData->m_gravity and m_collapseDamping, terminating when m_currentHeight +
//     geometryHeight <= 0. ISimContext exposes no gravity constant anywhere, and this
//     ModuleData carries no acceleration/velocity field of its own - only RubbleRiseDamping (a
//     damping coefficient, meaningless without a driving acceleration) and RubbleHeight (a
//     scalar target, not a rate). Reproducing GPL's fall-physics shape mirrored into a "rise"
//     would require inventing which constant substitutes for gravity and what the rise's
//     terminal condition is. Both fields are parsed (round-trip fidelity, future consumer TBD)
//     but not consumed by Update() - the only reading consistent with the live data (all four
//     shipped AotR uses comment RubbleRiseDamping out entirely). Consequence: the Rising state
//     has no terminal condition and loops its burst FX forever.
//   F-RRU-2 (shudder unmodeled): GPL's per-frame shudder writes a jittered translation into
//     the Drawable's instance matrix - float-typed client-render substrate with no Fix64-safe
//     entry point from [SimState] code, the same class of gap as EmpUpdate's F-EMP-5. MaxShudder
//     is parsed for round-trip fidelity but not applied to anything. Consequence: this module
//     has no reason to tick every frame, so Update() sleeps until the next frame that actually
//     changes something (_riseFrame/_burstFrame), per the AutoHealBehavior idiom.
//   F-RRU-3 (per-phase FX reduces to "one or none", no OCL): see doPhaseStuff summary above.
//     This is the existing stub's field shape (predates this port) and matches every confirmed
//     live AotR usage (one FXList = INITIAL ... line per object).
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4). Field order is OUR choice (F9) - GPL's own
// xfer() shape does not apply to this field-name-remapped, non-die-gated module.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class RubbleRiseUpdate : UpdateModule
{
    private readonly RubbleRiseUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Two states; no terminal/Done value - the burst loop never finishes (F-RRU-1).</summary>
    private enum RubbleRiseState
    {
        WaitingForRiseStart,
        Rising,
    }

    private RubbleRiseState _state;

    /// <summary>Frame the Rising transition (and its unconditional Burst-phase fire) occurs on
    /// (GPL m_collapseFrame analog).</summary>
    private LogicFrame _riseFrame;

    /// <summary>Frame the next burst roll occurs on, re-armed by addition each roll (GPL
    /// m_burstFrame analog).</summary>
    private LogicFrame _burstFrame;

    internal RubbleRiseUpdate(GameObject gameObject, ISimContext context, RubbleRiseUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        var now = Context.CurrentFrame;

        // GPL beginStructureCollapse L140: GameLogicRandomValue(minDelay, maxDelay), inclusive.
        _riseFrame = now + new LogicFrameSpan((uint)Context.GameLogicRandom.Next(
            (int)data.MinRubbleRiseDelay.Value, (int)data.MaxRubbleRiseDelay.Value));

        // GPL L142: doPhaseStuff(SCPHASE_INITIAL, ...) unconditionally, before the delay check.
        FirePhaseFx(StructureCollapsePhase.Initial);

        _state = RubbleRiseState.WaitingForRiseStart;

        // Sleep until the frame that matters (AutoHealBehavior idiom) rather than GPL's literal
        // every-frame tick (F-RRU-2: the shudder that motivates that cadence is unmodeled here).
        SetWakeFrame(UpdateSleepTime.Frames(_riseFrame - now));
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        switch (_state)
        {
            case RubbleRiseState.WaitingForRiseStart:
                if (now < _riseFrame)
                {
                    // Should not normally happen given the sleep-until-_riseFrame scheduling
                    // above; kept as an explicit guard (e.g. re-entry after a save/load).
                    return UpdateSleepTime.Frames(_riseFrame - now);
                }

                _state = RubbleRiseState.Rising;

                // GPL L202: doPhaseStuff(SCPHASE_BURST, ...) unconditionally - NOT the
                // 1-in-BigBurstFrequency roll, which only happens in the Rising loop below.
                FirePhaseFx(StructureCollapsePhase.Burst);

                // GPL L204: GameLogicRandomValue(minBurstDelay, maxBurstDelay).
                _burstFrame = now + new LogicFrameSpan((uint)Context.GameLogicRandom.Next(
                    (int)_data.MinBurstDelay.Value, (int)_data.MaxBurstDelay.Value));

                return UpdateSleepTime.Frames(_burstFrame - now);

            case RubbleRiseState.Rising:
                if (now < _burstFrame)
                {
                    // Nothing to do yet - shudder (the only other per-frame GPL effect in this
                    // state) is unmodeled (F-RRU-2), so no reason to wake before _burstFrame.
                    return UpdateSleepTime.Frames(_burstFrame - now);
                }

                // GPL L228: GameLogicRandomValue(1, bigBurstFrequency) == 1. Guard: GPL's
                // GameLogicRandomValue(lo, hi) has no documented behavior for hi <= 0; live
                // data always uses 4. A non-positive BigBurstFrequency skips the roll and
                // always takes the Delay branch - defensive, not a translated fact.
                var isBigBurst = _data.BigBurstFrequency > 0
                    && Context.GameLogicRandom.Next(1, _data.BigBurstFrequency) == 1;

                FirePhaseFx(isBigBurst ? StructureCollapsePhase.Burst : StructureCollapsePhase.Delay);

                // GPL L237: re-arm by ADDITION (not "now + ..."), phase-preserving against
                // frame-processing slack.
                _burstFrame += new LogicFrameSpan((uint)Context.GameLogicRandom.Next(
                    (int)_data.MinBurstDelay.Value, (int)_data.MaxBurstDelay.Value));

                // No further state transition, no Final phase, no sleep-forever exit - the
                // burst loop repeats indefinitely (F-RRU-1).
                return UpdateSleepTime.Frames(_burstFrame - now);

            default:
                return UpdateSleepTime.Forever;
        }
    }

    /// <summary>GPL doPhaseStuff, reduced to "fire it or don't" (F-RRU-3): FXLists is one
    /// string per phase, so the random-index pick GPL performs over a list+count is vacuous.
    /// A missing entry is a silent no-op, matching GPL's null-tolerant doFXPos (L91).</summary>
    private void FirePhaseFx(StructureCollapsePhase phase)
    {
        if (_data.FXLists.TryGetValue(phase, out var fx) && fx != null)
        {
            Context.Events.FireFXAtObjectPosition(fx, GameObject.Id);
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("State", ref _state);
        xfer.XferFrame("RiseFrame", ref _riseFrame, Tolerance.Quantum);
        xfer.XferFrame("BurstFrame", ref _burstFrame, Tolerance.Quantum);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Simulates rubble slowly rising out of a collapsed structure's footprint: an initial delay,
/// then an indefinite burst loop of FX (per <see cref="StructureCollapsePhase"/>). No literal
/// GPL source file of this name exists; behavior is translated from the field-name sibling
/// StructureCollapseUpdate.cpp's delay/burst-loop machine (see RubbleRiseUpdate.cs header and
/// bfme2-workbench/research/modules-r13/specs/RubbleRiseUpdateModuleData.md).
/// </summary>
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class RubbleRiseUpdateModuleData : UpdateModuleData
{
    internal static RubbleRiseUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<RubbleRiseUpdateModuleData> FieldParseTable = new IniParseTable<RubbleRiseUpdateModuleData>
    {
        { "MinRubbleRiseDelay", (parser, x) => x.MinRubbleRiseDelay = parser.ParseDurationLogicFrames() },
        { "MaxRubbleRiseDelay", (parser, x) => x.MaxRubbleRiseDelay = parser.ParseDurationLogicFrames() },
        { "RubbleRiseDamping", (parser, x) => x.RubbleRiseDamping = parser.ParseFix64() },
        { "RubbleHeight", (parser, x) => x.RubbleHeight = parser.ParseFix64() },
        { "MaxShudder", (parser, x) => x.MaxShudder = parser.ParseFix64() },
        { "MinBurstDelay", (parser, x) => x.MinBurstDelay = parser.ParseDurationLogicFrames() },
        { "MaxBurstDelay", (parser, x) => x.MaxBurstDelay = parser.ParseDurationLogicFrames() },
        { "BigBurstFrequency", (parser, x) => x.BigBurstFrequency = parser.ParseInteger() },
        { "FXList", (parser, x) => x.FXLists[parser.ParseEnum<StructureCollapsePhase>()] = parser.ParseAssetReference() },
    };

    public LogicFrameSpan MinRubbleRiseDelay { get; private set; }
    public LogicFrameSpan MaxRubbleRiseDelay { get; private set; }

    /// <summary>F-RRU-1: parsed for authoring round-trip fidelity; not consumed by Update()
    /// (no gravity constant on ISimContext to drive a fall/rise physics model).</summary>
    public Fix64 RubbleRiseDamping { get; private set; }

    /// <summary>BFME-only addition (no GPL analog). F-RRU-1: parsed for round-trip fidelity;
    /// not consumed by Update().</summary>
    [AddedIn(SageGame.Bfme)]
    public Fix64 RubbleHeight { get; private set; }

    /// <summary>F-RRU-2: parsed for round-trip fidelity; not applied to a renderer (no
    /// Fix64-safe visual-jitter hook from [SimState] code).</summary>
    public Fix64 MaxShudder { get; private set; }

    public LogicFrameSpan MinBurstDelay { get; private set; }
    public LogicFrameSpan MaxBurstDelay { get; private set; }

    /// <summary>Plain integer denominator for the 1-in-N big-burst roll (GPL
    /// INI::parseInt, not a duration).</summary>
    public int BigBurstFrequency { get; private set; }

    /// <summary>One FX name per <see cref="StructureCollapsePhase"/> (F-RRU-3: not a
    /// list+count like the GPL sibling's m_fxs - a missing phase entry is a legal no-op).</summary>
    public Dictionary<StructureCollapsePhase, string> FXLists { get; } = new Dictionary<StructureCollapsePhase, string>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new RubbleRiseUpdate(gameObject, gameEngine.SimContext, this);
    }
}
