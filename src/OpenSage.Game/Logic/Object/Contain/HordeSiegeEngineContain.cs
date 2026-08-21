// HordeSiegeEngineContain - R12 port. BFME-only (no generals-gpl sibling: AddedIn(SageGame.Bfme))
// and the only field-level authority is the module inventory row in
// bfme2-workbench/research/spec-hordes.md § 2 ("HordeGarrisonContain / HordeTransportContain /
// HordeSiegeEngineContain / ... - structures/vehicles that hold hordes")
// - a census entry, not a behavioral recovery. No decompiled logic is transplanted
// here (clean-room); the field grammar itself (already landed, unused, on TransportContain -
// see FadeFilter/FadePassengerOnEnter/EnterFadeTime/FadePassengerOnExit/ExitFadeTime/
// UpgradeCreationTrigger there) is the only faithful source for what this wrapper does.
//
// SCOPE (finding, matches the packet's own framing: "Inherits base contain from
// SiegeEngineContainModuleData - members remain pathfindable and targetable; horde
// selection/commands route through horde object, audio through members"): the base
// SiegeEngineContainModuleData crew/slot/exit-path system stays [ParseOnly] - a separate,
// larger port. This class does not reimplement crew seating or exit paths; it owns exactly
// the FADE-EFFECT WRAPPER responsibility the packet names - EnterSound/ExitSound,
// FadePassengerOnEnter/Exit + EnterFadeTime/ExitFadeTime + FadeReverse + FadeFilter, and
// UpgradeCreationTriggers - exposed as NotifyMemberEntered/NotifyMemberExited hooks that a
// future SiegeEngineContain crew-seating port (or, meanwhile, tests) drives passenger
// membership through.
//
// Fade timeline: same posture as FadeAndDieOrnamentUpdate (R12 sibling) - opacity is a pure
// function of (fade start frame, duration, direction) sampled at the current frame via
// OpacityAtFrame/GetPassengerOpacity; a zero-length duration is instantaneous (S5 default: a
// zero-span fraction reads as fully elapsed), which is exactly "EnterFadeTime=0 ... applies
// instantly with no animation frames". FadeReverse flips BOTH directions (the packet: "opacity
// in if normally out, out if normally in"): normal entry fades IN (0->1) and normal exit fades
// OUT (1->0); FadeReverse swaps each transition to the opposite endpoint. Opacity itself is a
// render-only quantity (S8: rendering is deliberately absent from ISimContext), so it carries
// no sim-input obligation - only the timeline (fade start frame, duration, direction) is
// tracked/Xfered state, exactly as FadeAndDieOrnamentUpdate's CurrentOpacity is derived rather
// than stored.
//
// R13 fix (blocker): a per-member PassengerFade record, once written by StartFade, is NEVER
// deleted by frame-advance (Update() no longer purges completed records - see below). This is
// load-bearing, not incidental: OpacityAtFrame's per-fade math (Frac, clamped) already yields
// the correct FROZEN terminal value forever once elapsed >= span (One for a completed ENTER
// fade, Zero for a completed EXIT fade, and the FadeReverse-flipped equivalents) - exactly
// FadeAndDieOrnamentUpdate's "stateless function of a fixed anchor, no deletion" posture the
// rest of this header already (correctly) claims. Deleting the record once it aged out (the
// R12 bug) collapsed that per-direction terminal value down to a single hardcoded
// Fix64.One fallback in OpacityAtFrame - wrong for every EXIT fade and every FadeReverse-
// flipped ENTRY fade, and reachable within a single game tick for a zero-duration fade (a
// normal INI value - see SiegeEngineHostInstant in the contract tests). StartFade still calls
// RemoveFade before adding, so a member's fade record is replaced (not accumulated) each time
// NotifyMemberEntered/Exited starts a new fade for them; the fallback Fix64.One in
// OpacityAtFrame is now reached only for a member that has never had any fade started, which is
// the one case it was always correct for.
//
// Audio: EnterSound/ExitSound are literal AudioEvent asset references (parser.ParseAssetReference,
// same as TransportContain's identically-named fields), not UnitSpecificSounds keys, so they
// go through the new ISimEvents.FireAudioEventAtObject seam (grown for this port, mirroring
// FireCrateFreeUnitPickupSound's per-name-not-per-key shape) rather than FireUnitSoundAtObject.
//
// UpgradeCreationTriggers (BFME2Rotwk): fired once, the first time this wrapper's own entry
// count transitions from empty to occupied - the base contain's crew list is not yet ported/
// readable (finding, matches the SCOPE note above), so "activation" is read off this module's
// own NotifyMemberEntered/Exited bookkeeping rather than the (unported) real crew list. Each
// trigger's Upgrade is granted to the container object via UpgradeTemplate.GrantUpgrade - same
// routing GrantUpgradeCreate uses. The trigger's Model field (a draw-model swap) has no
// ISimContext seam (S8: rendering is deliberately absent) and is deliberately not applied - a
// finding, not an omission. The Unknown field's meaning is unrecovered (already flagged
// "Unknown" on the identical trigger shape in the landed TransportContain sibling) and is not
// read here either.

using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class HordeSiegeEngineContain : UpdateModule
{
    private readonly HordeSiegeEngineContainModuleData _data;

    private struct PassengerFade
    {
        public ObjectId Member;
        public LogicFrame StartFrame;
        public LogicFrameSpan Duration;

        /// <summary>True: opacity ramps Zero -&gt; One over [StartFrame, StartFrame+Duration). False: One -&gt; Zero.</summary>
        public bool FadeIn;
    }

    // ---- mutable sim state (the whole inventory) ----
    private readonly List<PassengerFade> _fades = new();
    private int _memberCount;
    private readonly List<bool> _upgradeTriggersFired = new();

    public HordeSiegeEngineContain(GameObject gameObject, ISimContext context, HordeSiegeEngineContainModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        for (var i = 0; i < data.UpgradeCreationTriggers.Count; i++)
        {
            _upgradeTriggersFired.Add(false);
        }
        SetWakeFrame(UpdateSleepTime.None);
    }

    // ---- public surface (read by tests / the future crew-seating caller) ----

    /// <summary>
    /// Current member count as tracked by this wrapper's own Notify calls (finding: the base
    /// contain's real crew list is not yet ported/readable - see the header SCOPE note).
    /// </summary>
    public int MemberCount => _memberCount;

    /// <summary>
    /// The passenger's fade opacity right now (Fix64 in [Zero, One]). Fix64.One (fully
    /// visible) only when the member has NEVER had a fade started - a completed fade's record
    /// is never purged (R13 fix), so its correct frozen terminal value (One for a completed
    /// ENTER fade, Zero for a completed EXIT fade, and the FadeReverse-flipped equivalents) is
    /// what OpacityAtFrame keeps returning, forever. Render-only output (S8), same posture as
    /// FadeAndDieOrnamentUpdate.CurrentOpacity.
    /// </summary>
    public Fix64 GetPassengerOpacity(ObjectId member) => OpacityAtFrame(member, Context.CurrentFrame);

    internal Fix64 OpacityAtFrame(ObjectId member, LogicFrame now)
    {
        foreach (var fade in _fades)
        {
            if (fade.Member != member)
            {
                continue;
            }
            var t = Frac(now, fade.StartFrame, fade.StartFrame + fade.Duration);
            return fade.FadeIn ? t : Fix64.One - t;
        }
        return Fix64.One;
    }

    /// <summary>
    /// Fraction of [start, end) elapsed at <paramref name="now"/>. A zero-length span reads as
    /// fully elapsed regardless of <paramref name="now"/> (S5 default, matching
    /// FadeAndDieOrnamentUpdate's Frac - this is what makes EnterFadeTime/ExitFadeTime = 0
    /// apply instantly, with no animation frames). Otherwise a not-yet-started fade reads as
    /// zero and a finished fade (whose record is never purged - R13 fix) clamps to fully
    /// elapsed forever, which is what freezes GetPassengerOpacity at the correct terminal
    /// value.
    /// </summary>
    private static Fix64 Frac(LogicFrame now, LogicFrame start, LogicFrame end)
    {
        var span = (end - start).Value;
        if (span == 0)
        {
            return Fix64.One;
        }
        if (now <= start)
        {
            return Fix64.Zero;
        }
        var elapsed = (now - start).Value;
        if (elapsed >= span)
        {
            return Fix64.One;
        }
        return new Fix64((int)elapsed) / new Fix64((int)span);
    }

    /// <summary>
    /// Passenger-entry hook (called by the future crew-seating caller, or directly by tests):
    /// plays EnterSound and starts the enter fade when FadePassengerOnEnter is set and the
    /// member matches FadeFilter. Fires any not-yet-fired UpgradeCreationTriggers when this is
    /// the first member (empty -&gt; occupied transition).
    /// </summary>
    public void NotifyMemberEntered(ObjectId memberId)
    {
        var wasEmpty = _memberCount == 0;
        _memberCount++;

        Context.Events.FireAudioEventAtObject(_data.EnterSound, memberId);

        if (_data.FadePassengerOnEnter && MatchesFadeFilter(memberId))
        {
            StartFade(memberId, _data.EnterFadeTime, fadeIn: !_data.FadeReverse);
        }

        if (wasEmpty)
        {
            FireUpgradeTriggers();
        }
    }

    /// <summary>
    /// Passenger-exit hook: plays ExitSound and starts the exit fade when FadePassengerOnExit
    /// is set and the member matches FadeFilter.
    /// </summary>
    public void NotifyMemberExited(ObjectId memberId)
    {
        if (_memberCount > 0)
        {
            _memberCount--;
        }

        Context.Events.FireAudioEventAtObject(_data.ExitSound, memberId);

        if (_data.FadePassengerOnExit && MatchesFadeFilter(memberId))
        {
            StartFade(memberId, _data.ExitFadeTime, fadeIn: _data.FadeReverse);
        }
    }

    private bool MatchesFadeFilter(ObjectId memberId)
    {
        var filter = _data.FadeFilter;
        if (filter == null)
        {
            return false;
        }
        var member = Context.GameLogic.GetObjectById(memberId);
        return member != null && filter.Matches(member);
    }

    private void StartFade(ObjectId memberId, LogicFrameSpan duration, bool fadeIn)
    {
        RemoveFade(memberId);
        _fades.Add(new PassengerFade
        {
            Member = memberId,
            StartFrame = Context.CurrentFrame,
            Duration = duration,
            FadeIn = fadeIn,
        });
    }

    private void RemoveFade(ObjectId memberId)
    {
        for (var i = _fades.Count - 1; i >= 0; i--)
        {
            if (_fades[i].Member == memberId)
            {
                _fades.RemoveAt(i);
            }
        }
    }

    private void FireUpgradeTriggers()
    {
        for (var i = 0; i < _data.UpgradeCreationTriggers.Count; i++)
        {
            if (_upgradeTriggersFired[i])
            {
                continue;
            }

            var trigger = _data.UpgradeCreationTriggers[i];
            if (string.IsNullOrEmpty(trigger.Upgrade))
            {
                continue;
            }

            var template = Context.Assets.GetUpgradeTemplate(trigger.Upgrade);
            if (template == null)
            {
                continue;
            }

            template.GrantUpgrade(GameObject);
            _upgradeTriggersFired[i] = true;
        }
    }

    /// <summary>
    /// R13 fix: this used to purge each PassengerFade record once its timeline finished
    /// (`now >= StartFrame + Duration`), which collapsed OpacityAtFrame's per-direction frozen
    /// terminal value down to a single wrong constant (see the header's "R13 fix" note and
    /// finding 1 in review/horde-siege-engine-contain.md). OpacityAtFrame's own math already
    /// freezes correctly at the terminal value once a fade completes, so there is nothing left
    /// for Update() to do here; it still ticks every frame (SetWakeFrame(UpdateSleepTime.None))
    /// in case a future crew-seating port needs a per-frame hook on this module.
    /// </summary>
    public override UpdateSleepTime Update()
    {
        return UpdateSleepTime.None;
    }

    // ---- the single walk (F8 Objects channel; field order = declaration order, F9) ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferInt("MemberCount", ref _memberCount);
        xfer.XferList("Fades", _fades, XferFade);
        xfer.XferList("UpgradeTriggersFired", _upgradeTriggersFired, XferTriggerFired);
    }

    private static void XferFade(IXfer xfer, ref PassengerFade fade)
    {
        xfer.XferObjectId("Member", ref fade.Member);
        xfer.XferFrame("StartFrame", ref fade.StartFrame);
        xfer.XferFrameSpan("Duration", ref fade.Duration);
        xfer.XferBool("FadeIn", ref fade.FadeIn);
    }

    private static void XferTriggerFired(IXfer xfer, ref bool fired) => xfer.XferBool("Fired", ref fired);
}

[AddedIn(SageGame.Bfme)]
public class HordeSiegeEngineContainModuleData : SiegeEngineContainModuleData
{
    internal static new HordeSiegeEngineContainModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    internal static readonly new IniParseTable<HordeSiegeEngineContainModuleData> FieldParseTable = SiegeEngineContainModuleData.FieldParseTable
        .Concat(new IniParseTable<HordeSiegeEngineContainModuleData>
        {
            { "EnterSound", (parser, x) => x.EnterSound = parser.ParseAssetReference() },
            { "ExitSound", (parser, x) => x.ExitSound = parser.ParseAssetReference() },
            { "FadeFilter", (parser, x) => x.FadeFilter = ObjectFilter.Parse(parser) },
            { "FadePassengerOnEnter", (parser, x) => x.FadePassengerOnEnter = parser.ParseBoolean() },
            { "EnterFadeTime", (parser, x) => x.EnterFadeTime = parser.ParseDurationLogicFrames() },
            { "FadePassengerOnExit", (parser, x) => x.FadePassengerOnExit = parser.ParseBoolean() },
            { "ExitFadeTime", (parser, x) => x.ExitFadeTime = parser.ParseDurationLogicFrames() },
            { "FadeReverse", (parser, x) => x.FadeReverse = parser.ParseBoolean() },
            { "UpgradeCreationTrigger", (parser, x) => x.UpgradeCreationTriggers.Add(UpgradeCreationTrigger.Parse(parser)) },
        });

    public string EnterSound { get; private set; }
    public string ExitSound { get; private set; }
    public ObjectFilter FadeFilter { get; private set; } = new();
    public bool FadePassengerOnEnter { get; private set; }
    public LogicFrameSpan EnterFadeTime { get; private set; }
    public bool FadePassengerOnExit { get; private set; }
    public LogicFrameSpan ExitFadeTime { get; private set; }
    public bool FadeReverse { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public List<UpgradeCreationTrigger> UpgradeCreationTriggers { get; } = new List<UpgradeCreationTrigger>();

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new HordeSiegeEngineContain(gameObject, gameEngine.SimContext, this);
    }
}
