// OneRingPenaltyUpdate - R13 port (api-freeze-v1 §6 / template v1.1).
//
// Classification: data-derivable (§0 of the port spec). `grep -rli
// "onering|ring.*penalty|ringpenalty" generals-gpl generals-community` returns zero hits -
// confirmed BFME-only, no GPL C++ source to diff against. This file is derived from the
// class's own 7-field INI vocabulary, the frozen module API (api-freeze-v1 + amendments v1.1),
// and landed engine idiom in sibling Update modules (EmpUpdate.cs, ReplaceObjectUpdate.cs). No
// Ghidra/game.dat material is read or cited anywhere in this file (CLEAN-ROOM RULE).
//
// Behavior (engineering composition per the spec's F-RING-1, not a GPL translation - see
// bfme2-workbench/research/modules-r13/specs/OneRingPenaltyUpdateModuleData.md for the full
// derivation):
//   Idle --(ctor)--> WaitingToSpawn --(RingTimeBeforeSpawning elapses)--> Roaming
//   Roaming --NotifyRingDiscovered()--> Discovered (plays DiscoveredSound, no penalty, terminal)
//   Roaming --(TimeSpentRoamingAround elapses, not discovered)--> Penalized (terminal)
//
//   - Constructor: captures now = Context.CurrentFrame, _spawnFrame = now +
//     RingTimeBeforeSpawning, ticks every frame (SetWakeFrame(UpdateSleepTime.None)) - same
//     ctor-driven-timer posture as EmpUpdate (no gating field exists in this class's INI
//     vocabulary to hold the chain back; F-RING-1).
//   - WaitingToSpawn: once now >= _spawnFrame, spawns SpecialObjectName at a random angle
//     (Context.GameLogicRandom.NextFix64(0, 2*PI), same draw shape as
//     ReplaceObjectUpdate.PerformReplace's scatter angle) and a FIXED radius
//     StartingDistanceFromMe (a single scalar, not a min/max pair - F-RING-3) from the
//     module's own GameObject, via the donor-offset CreateObjectAt overload
//     (ISimContext.CreateObjectAt(definition, owner, at, in FixVector3, orientation)). A null
//     (unresolved) SpecialObjectName is a silent no-op straight to Penalized - no penalty
//     applied - matching ReplaceObjectUpdate.PerformReplace's own null-template guard
//     (F-RING-4).
//   - Roaming: once now >= _roamEndFrame (TimeSpentRoamingAround after spawn), applies the
//     penalty: GameObject.Disable(DisabledType.Paralyzed, now + TimeFrozenFromPenalty) (reusing
//     the already-landed DisabledType.Paralyzed rather than inventing a new enum member - both
//     name the same "frozen" effect) and starts a self-tracked
//     _ringPowerSuppressedUntilFrame = now + TimeRingPowerSuppressed, exposed via
//     IsRingPowerSuppressed (F-RING-6: no landed "suppress this object's special power" gate
//     exists anywhere in ISimContext/GameObject to attach to - confirmed by grep for
//     "PowerSuppress"/"SuppressPower", zero hits).
//   - NotifyRingDiscovered() (driven, F-RING-2: no landed cross-object "another module tells me
//     something happened" callback surface exists to call this automatically - wired here as a
//     driven method for a future ring-token pickup/collide module to call): no-op (false)
//     unless phase is exactly Roaming (same exclusivity-guard shape as
//     ReplaceObjectUpdate.InitiateIntentToDoSpecialPower's _phase != Idle check). On success,
//     fires DiscoveredSound via Context.Events.FireAudioEventAtObject at "me" (the only object
//     identity this module's Xfer tracks - F-RING-1's target choice applies here too),
//     transitions to Discovered, and skips the penalty entirely.
//
// FINDINGS (behavior-fact gaps, filed not invented - see the port spec §5 for the full text):
//   F-RING-1 (who "me" is / what starts the chain): design choice, not a binary-only fact - no
//     SpecialPowerTemplate or other gating field exists on this class, so the chain is modeled
//     as ctor-driven, and the penalty target is the module's own GameObject (no field carries a
//     different target's identity).
//   F-RING-2 (discovery signal): NotifyRingDiscovered() is a driven method with no landed
//     caller yet - wire it when the ring-token's own pickup/collide module ports.
//   F-RING-3 (fixed-radius vs randomized placement): StartingDistanceFromMe is modeled as an
//     exact-radius, random-angle placement (a single scalar, not a min/max scatter pair).
//   F-RING-4 (bad SpecialObjectName): silent no-op, no penalty applied.
//   F-RING-5 (frozen penalty does not auto-clear) - CLOSED (A0-prime): GameObject.
//     CheckDisabledStates (the sweep that auto-clears a timed DisabledType) is called from the
//     internal GameObject.Update(), which A0-prime now wires into GameLogic.Update() - same
//     shared fix as EmpUpdate's F-EMP-6. The correct un-disable frame recorded here now takes
//     effect automatically once it passes.
//   F-RING-6 (TimeRingPowerSuppressed has no consumer): tracked and exposed via
//     IsRingPowerSuppressed, unconsumed by anything landed today - wire it into whatever
//     special-power-invocation gate eventually checks "is this hero allowed to use the Ring
//     power right now."
//
// Every mutable sim field appears in Xfer exactly once (§3 of the spec); tolerances match the
// field's conformance class (lifecycle/identity facts Exact, timers Quantum). _ringFrozenUntilFrame
// is NOT Xfer'd separately - it is the argument passed straight into GameObject.Disable, whose
// own _disabledTypesFrames array is already part of GameObject's own Xfer walk.

using OpenSage.Audio;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class OneRingPenaltyUpdate : UpdateModule
{
    private readonly OneRingPenaltyUpdateModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    private OneRingPenaltyPhase _phase;
    private LogicFrame _spawnFrame;
    private LogicFrame _roamEndFrame;
    private ObjectId _ringObjectId;
    private LogicFrame _ringPowerSuppressedUntilFrame;

    public OneRingPenaltyUpdate(GameObject gameObject, ISimContext context, OneRingPenaltyUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _phase = OneRingPenaltyPhase.WaitingToSpawn;
        _ringObjectId = ObjectId.Invalid;

        var now = Context.CurrentFrame;
        _spawnFrame = now + data.RingTimeBeforeSpawning;

        // No gate field exists in this class's INI vocabulary to hold the chain back
        // (F-RING-1): the timer starts immediately, same posture as EmpUpdate's ctor.
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        switch (_phase)
        {
            case OneRingPenaltyPhase.WaitingToSpawn:
                if (now >= _spawnFrame)
                {
                    SpawnRingObject();
                }
                break;

            case OneRingPenaltyPhase.Roaming:
                if (now >= _roamEndFrame)
                {
                    ApplyPenalty();
                }
                break;
        }

        return UpdateSleepTime.None;
    }

    /// <summary>
    /// GPL-less engineering composition (F-RING-1/F-RING-3/F-RING-4): spawns
    /// <see cref="OneRingPenaltyUpdateModuleData.SpecialObjectName"/> at a random angle, fixed
    /// radius <see cref="OneRingPenaltyUpdateModuleData.StartingDistanceFromMe"/> from this
    /// module's own GameObject. A null (unresolved) definition is a silent no-op straight to
    /// Penalized - no penalty applied.
    /// </summary>
    private void SpawnRingObject()
    {
        var definition = _data.SpecialObjectName?.Value;
        if (definition == null)
        {
            // F-RING-4: bad/unset SpecialObjectName - silent no-op, no penalty.
            _phase = OneRingPenaltyPhase.Penalized;
            return;
        }

        var angle = Context.GameLogicRandom.NextFix64(Fix64.Zero, Fix64.PiTimes2);
        var radius = _data.StartingDistanceFromMe;
        var offset = new FixVector3(radius * FixTrig.Cos(angle), radius * FixTrig.Sin(angle), Fix64.Zero);

        var ringObject = Context.GameLogic.CreateObjectAt(definition, GameObject.Owner, GameObject, offset, Fix64.Zero);

        _ringObjectId = ringObject?.Id ?? ObjectId.Invalid;
        _phase = OneRingPenaltyPhase.Roaming;
        _roamEndFrame = Context.CurrentFrame + _data.TimeSpentRoamingAround;
    }

    /// <summary>
    /// GPL-less engineering composition (F-RING-5/F-RING-6): applies the frozen penalty
    /// (DisabledType.Paralyzed for TimeFrozenFromPenalty) and starts the self-tracked ring-power
    /// suppression window, then transitions to the terminal Penalized phase.
    /// </summary>
    private void ApplyPenalty()
    {
        var now = Context.CurrentFrame;

        GameObject.Disable(DisabledType.Paralyzed, now + _data.TimeFrozenFromPenalty);
        _ringPowerSuppressedUntilFrame = now + _data.TimeRingPowerSuppressed;

        _phase = OneRingPenaltyPhase.Penalized;
    }

    /// <summary>
    /// Driven input (F-RING-2): a future ring-token pickup/collide module calls this when the
    /// spawned ring object is discovered. No-op unless the phase is exactly Roaming (same
    /// exclusivity-guard shape as ReplaceObjectUpdate.InitiateIntentToDoSpecialPower). On
    /// success, fires DiscoveredSound at this module's own GameObject and cancels the penalty
    /// entirely (no timers fire).
    /// </summary>
    public bool NotifyRingDiscovered()
    {
        if (_phase != OneRingPenaltyPhase.Roaming)
        {
            return false;
        }

        _phase = OneRingPenaltyPhase.Discovered;

        if (!string.IsNullOrEmpty(_data.DiscoveredSound))
        {
            Context.Events.FireAudioEventAtObject(_data.DiscoveredSound, GameObject.Id);
        }

        return true;
    }

    /// <summary>
    /// F-RING-6: self-tracked frame comparison (no engine sweep exists to clear this
    /// automatically - see F-RING-5's sibling gap). True from the moment the penalty is applied
    /// until <see cref="OneRingPenaltyUpdateModuleData.TimeRingPowerSuppressed"/> frames later,
    /// even once this module has no more Update() work left to do in the Penalized phase.
    /// </summary>
    public bool IsRingPowerSuppressed => Context.CurrentFrame < _ringPowerSuppressedUntilFrame;

    private enum OneRingPenaltyPhase
    {
        WaitingToSpawn,
        Roaming,
        Discovered,
        Penalized,
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's (there is no
    // original - §0's GPL-search finding). Tolerances (ruling A3): the phase enum and the ring
    // object's identity are lifecycle/identity facts, so Exact; the three frame fields are
    // timers, so Quantum (XferFrame's own default).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferEnum("Phase", ref _phase);
        xfer.XferFrame("SpawnFrame", ref _spawnFrame);
        xfer.XferFrame("RoamEndFrame", ref _roamEndFrame);
        xfer.XferObjectId("RingObjectId", ref _ringObjectId);
        xfer.XferFrame("RingPowerSuppressedUntilFrame", ref _ringPowerSuppressedUntilFrame);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// BFME-only "Ring bearer" penalty mechanic: after a delay, spawns a wandering pickup object
/// near the bearer; if it is not discovered within a bounded window, the bearer is frozen and
/// has its Ring power suppressed for a time. See the file header and the port spec
/// (bfme2-workbench/research/modules-r13/specs/OneRingPenaltyUpdateModuleData.md) for the full
/// data-derivation argument - no GPL source exists for this class (confirmed by grep, §0).
/// </summary>
[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class OneRingPenaltyUpdateModuleData : UpdateModuleData
{
    internal static OneRingPenaltyUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<OneRingPenaltyUpdateModuleData> FieldParseTable = new IniParseTable<OneRingPenaltyUpdateModuleData>
    {
        { "SpecialObjectName", (parser, x) => x.SpecialObjectName = parser.ParseObjectReference() },
        { "RingTimeBeforeSpawning", (parser, x) => x.RingTimeBeforeSpawning = parser.ParseDurationLogicFrames() },
        { "TimeSpentRoamingAround", (parser, x) => x.TimeSpentRoamingAround = parser.ParseDurationLogicFrames() },
        { "TimeRingPowerSuppressed", (parser, x) => x.TimeRingPowerSuppressed = parser.ParseDurationLogicFrames() },
        { "StartingDistanceFromMe", (parser, x) => x.StartingDistanceFromMe = parser.ParseFix64() },
        { "TimeFrozenFromPenalty", (parser, x) => x.TimeFrozenFromPenalty = parser.ParseDurationLogicFrames() },
        { "DiscoveredSound", (parser, x) => x.DiscoveredSound = parser.ParseAssetReference() },
    };

    /// <summary>The wandering pickup object spawned after <see cref="RingTimeBeforeSpawning"/>.</summary>
    public LazyAssetReference<ObjectDefinition> SpecialObjectName { get; private set; }

    /// <summary>Delay before the special object spawns (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan RingTimeBeforeSpawning { get; private set; }

    /// <summary>Window the spawned object may be discovered in before the penalty applies (ms in INI, ceil-quantized).</summary>
    public LogicFrameSpan TimeSpentRoamingAround { get; private set; }

    /// <summary>Duration the Ring power is suppressed for after the penalty applies (ms in INI, ceil-quantized). F-RING-6: tracked, unconsumed.</summary>
    public LogicFrameSpan TimeRingPowerSuppressed { get; private set; }

    /// <summary>Fixed spawn radius from this module's own GameObject; direction is randomized (F-RING-3).</summary>
    public Fix64 StartingDistanceFromMe { get; private set; }

    /// <summary>Duration DisabledType.Paralyzed is applied for when the penalty triggers (ms in INI, ceil-quantized).</summary>
    public LogicFrameSpan TimeFrozenFromPenalty { get; private set; }

    /// <summary>
    /// One-shot cue played on <see cref="OneRingPenaltyUpdate.NotifyRingDiscovered"/> success.
    /// Integrate-lane fixup: held as the literal AudioEvent asset NAME (parser.ParseAssetReference),
    /// matching the two landed users of this same seam - HordeSiegeEngineContain's
    /// EnterSound/ExitSound and WeaponModeSpecialPowerUpdate's InitiateSound - because
    /// ISimEvents.FireAudioEventAtObject takes a name, and a LazyAssetReference resolves to null
    /// (silently dropping the cue) whenever the AudioEvent asset is not present in the loaded
    /// scope. Keeping the name on the sim side removes that asset-scope dependency entirely.
    /// </summary>
    public string DiscoveredSound { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new OneRingPenaltyUpdate(gameObject, gameEngine.SimContext, this);
    }
}
