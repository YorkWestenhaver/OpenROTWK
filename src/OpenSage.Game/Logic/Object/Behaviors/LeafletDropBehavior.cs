// LeafletDropBehavior - R12 port.
//
// No GPL source exists for this module: GeneralsMD/Code/GameEngine/Source/Common/Thing/
// ModuleFactory.cpp registers "LeafletDropBehavior" (addModule(LeafletDropBehavior)) but the
// corresponding LeafletDropBehavior.cpp/.h implementation file is absent from this GPL
// snapshot (it is a BFME-only module; only SpecialPowerType.h's SPECIAL_LEAFLET_DROP /
// EARLY_SPECIAL_LEAFLET_DROP entries reference it at all). Per the CLEAN-ROOM RULE this port
// therefore has no line of original source to translate faithfully and instead implements the
// task packet's plain-language spec and testCases directly - the same posture the packet's
// own summary/testCases already assume ("translate, do not invent" does not apply when there
// is nothing to translate). Structurally this module mirrors the two closest landed R12
// exemplars: EmpUpdate.cs (radius scan -> per-victim Disable() + one attached-FX request per
// victim, ISimEvents.FireParticleSystemAtObject) and CastleMemberBehavior.cs (BehaviorModule
// + IDieModule pattern for a [SimState] module that also reacts to its own death).
//
// Design, from the task packet's summary and testCases:
//   - ctor: capture the frame the disable trigger is due (now + Delay).
//   - update() ticks every frame (ports the two behaviors the packet's summary calls out as
//     the two activation paths):
//       1. on the FIRST update() call ever (independent of Delay - the FX is a "the leaflets
//          are falling" visual, not the "the leaflets have grounded and now disable" effect),
//          request the LeafletFXParticleSystem attached to self via
//          ISimEvents.FireParticleSystemAtObject (packet testcase 6: "attached to object
//          during first update() call").
//       2. once CurrentFrame >= the due frame (>= rather than EmpUpdate's strict == : Delay
//          may legitimately be 0, meaning "the same frame as the first tick", which a strict
//          == against a frame computed at construction time - before that first tick runs -
//          would silently miss), run the disable scan exactly once.
//   - OnDie(): if the disable scan has not already run (the delay was never reached), run it
//     immediately, bypassing the delay (packet testcase 5, "Early death handler"). Also fires
//     the FX if it never got the chance to (defensive: an object killed before its first
//     update() tick should still visibly drop its leaflets).
//   - doDisableAttack(): scans AffectRadius for every candidate other than self via
//     ISimContext.Partition.QueryObjectsInRadius. A candidate is disabled only if:
//       - candidate.GetRelationship(self) == Enemies (packet: "enemies only ... allied units
//         immune"; Neutral is excluded too - the packet's radius-filtering testcase never
//         exercises Neutral, so this port takes the narrower, safer "Enemies only" reading
//         rather than the wider "not Allies" one - filed as F-LDB-1 below, not invented past
//         the text).
//       - candidate IsKindOf INFANTRY or VEHICLE (packet: "limited to INFANTRY and VEHICLE
//         kinds"; aircraft/structures/other kinds are left untouched, matching testcase 3).
//     A qualifying candidate is disabled via GameObject.Disable(DisabledType.Emp, now +
//     DisabledDuration) - DisabledType.Emp is the engine's existing generic area-disable flag
//     (DisabledUntil semantics: blocks move/shoot/build, api-freeze-v1's DisabledFlags), and
//     the packet's own testcase 4 names it explicitly ("DISABLED_EMP status") as the status
//     leaflets apply, so this is the packet's own vocabulary, not an invented reuse.
//
// FINDINGS (behavior-fact gaps, filed not invented):
//   F-LDB-1 (Neutral candidates): with no GPL source to check, whether a Neutral (unallied,
//   unenemied) unit is also disabled is genuinely unknown. This port takes the narrower
//   "RelationshipType.Enemies only" reading of the packet's "enemies only ... allied units
//   immune" summary, consistent with how other radius-effect ports in this codebase (e.g.
//   EnemyNearUpdate) treat "enemy" as the Enemies relationship specifically, not "not Allies".
//   F-LDB-2 (particle system lifecycle - packet testcase 6's "lifetime = DisabledDuration - 30
//   frames; initial delay randomized 1-100 frames per emitter"): ISimEvents.
//   FireParticleSystemAtObject (grown for TransitionDamageFX, see its own doc comment) is a
//   fire-and-forget request that names an asset and a bone; it has no parameter for a
//   caller-supplied per-instance lifetime or a per-emitter random delay override, and
//   ParticleSystemTemplate's own InitialDelay/lifetime fields are client-authored template
//   data, not something a [SimState] module can override at request time. This is the same
//   class of gap EmpUpdate already filed (F-EMP-1: emitter multiplicity) and
//   TransitionDamageFX filed (F-TDF-1: the client owns the created emitter's lifetime) - no
//   Fix64-safe facade exists today for a sim module to hand the client a dynamic lifetime or
//   per-emitter delay range, so this port requests exactly one attached instance of the named
//   template (the packet's own "attached to object" contract) and leaves the lifetime/delay
//   computation unmodeled pending that facade, rather than inventing one here.
//   F-LDB-3 (DisabledType auto-expiry) - CLOSED (A0-prime): same shared engine gap EmpUpdate
//   filed as F-EMP-6 - GameObject.CheckDisabledStates (the sweep that auto-clears a DisabledType
//   once its recorded expiry frame passes) is called from GameObject.Update(), which A0-prime
//   now wires into GameLogic.Update(). A victim this module disables clears automatically at
//   the recorded un-disable frame instead of staying disabled past DisabledDuration.
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4). Field order is OUR choice (F9): there is no
// GPL xfer() to mirror.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Graphics.ParticleSystems;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class LeafletDropUpdate : UpdateModule, IDieModule
{
    private readonly LeafletDropBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Frame the disable scan is due (ctor: now + Delay).</summary>
    private LogicFrame _disableDueFrame;

    /// <summary>Whether the LeafletFXParticleSystem attach request has fired yet.</summary>
    private bool _fxFired;

    /// <summary>Whether doDisableAttack() has run yet (either via the delay or via OnDie).</summary>
    private bool _disableAttackDone;

    public LeafletDropUpdate(GameObject gameObject, ISimContext context, LeafletDropBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        _disableDueFrame = Context.CurrentFrame + data.Delay;

        // Ticks every frame until both the FX request and the disable scan have fired.
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        FireFxIfNeeded();

        if (!_disableAttackDone && Context.CurrentFrame >= _disableDueFrame)
        {
            DoDisableAttack();
            _disableAttackDone = true;
        }

        return UpdateSleepTime.None;
    }

    void IDieModule.OnDie(in DamageInfoInput damageInput)
    {
        // Defensive: an object killed before it ever ticked update() should still visibly
        // drop its leaflets (F-LDB-2's request is otherwise silently skipped).
        FireFxIfNeeded();

        if (!_disableAttackDone)
        {
            // Packet testcase 5: "onDie() triggers doDisableAttack() immediately, bypassing
            // delay".
            DoDisableAttack();
            _disableAttackDone = true;
        }
    }

    /// <summary>Packet testcase 6: attached to object during the first update() call.</summary>
    private void FireFxIfNeeded()
    {
        if (_fxFired)
        {
            return;
        }

        _fxFired = true;

        var fxTemplate = _data.LeafletFXParticleSystem?.Value;
        if (fxTemplate != null)
        {
            Context.Events.FireParticleSystemAtObject(fxTemplate.Name, GameObject.Id, string.Empty, false);
        }
    }

    /// <summary>Scans AffectRadius, disabling qualifying enemy infantry/vehicles.</summary>
    private void DoDisableAttack()
    {
        if (_data.AffectRadius <= Fix64.Zero)
        {
            return;
        }

        var self = GameObject;

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(self, _data.AffectRadius))
        {
            if (candidate == self)
            {
                continue;
            }

            // F-LDB-1: enemies only - allied and neutral candidates are exempt.
            if (candidate.GetRelationship(self) != RelationshipType.Enemies)
            {
                continue;
            }

            // Packet: "limited to INFANTRY and VEHICLE kinds".
            if (!candidate.IsKindOf(ObjectKinds.Infantry) && !candidate.IsKindOf(ObjectKinds.Vehicle))
            {
                continue;
            }

            candidate.Disable(DisabledType.Emp, Context.CurrentFrame + _data.DisabledDuration);
        }
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrame("DisableDueFrame", ref _disableDueFrame, Tolerance.Exact);
        xfer.XferBool("FxFired", ref _fxFired);
        xfer.XferBool("DisableAttackDone", ref _disableAttackDone);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Spawns a visual leaflet-drop particle system and, after a frame delay (or immediately on
/// death, whichever comes first), disables enemy infantry and vehicles within AffectRadius
/// for DisabledDuration frames.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
[SimDataAudited]
public sealed class LeafletDropBehaviorModuleData : BehaviorModuleData
{
    internal static LeafletDropBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<LeafletDropBehaviorModuleData> FieldParseTable = new IniParseTable<LeafletDropBehaviorModuleData>
    {
        { "DisabledDuration", (parser, x) => x.DisabledDuration = new LogicFrameSpan((uint)parser.ParseInteger()) },
        { "Delay", (parser, x) => x.Delay = new LogicFrameSpan((uint)parser.ParseInteger()) },
        { "AffectRadius", (parser, x) => x.AffectRadius = parser.ParseFix64() },
        { "LeafletFXParticleSystem", (parser, x) => x.LeafletFXParticleSystem = parser.ParseFXParticleSystemTemplateReference() },
    };

    /// <summary>Frames a disabled victim stays disabled (raw frame count, S9: no GPL source to
    /// confirm ms-vs-frames, and the task packet's testcases are 1:1 frame-exact - see the
    /// header comment).</summary>
    public LogicFrameSpan DisabledDuration { get; private set; }

    /// <summary>Frames after spawn until the disable scan is due (raw frame count; see
    /// DisabledDuration's doc comment).</summary>
    public LogicFrameSpan Delay { get; private set; }

    public Fix64 AffectRadius { get; private set; }

    public LazyAssetReference<FXParticleSystemTemplate> LeafletFXParticleSystem { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new LeafletDropUpdate(gameObject, gameEngine.SimContext, this);
    }
}
