// DelayedDeathBody - Round-8 Body-batch port to the frozen module contract
// (api-freeze-v1 §3/§5, template v1.1 = pilot-autoheal §3/§6). Builds ON S1
// (weapon/damage/armor): it consumes the landed ActiveBody / BodyDamageCore health ledger
// and the ImmortalBody-style Fix64 floor seam (ClampCombatHealthLoss); it does NOT
// reimplement damage math. It adds only a frame timer (no partition/vision/economy).
//
// Behavioral reference: BFME / BFME2-RotWK ONLY - this class is ABSENT from generals-gpl, so
// there is no GPL source to read. Behavior facts are inferred from (a) the AotR INI corpus and
// (b) the task packet's own model ("Update-style timer state (DelayedDeathTime) +
// ImmortalUntilDeathTime floor"). Every inference is recorded in
// research/modules-r8/DelayedDeathBody.md and the ones needing Ghidra are flagged as
// behavior-fact gaps (the pilot's discipline: act only on what is evidenced, park the rest).
//
// EVIDENCE that fixes the arming trigger (modules-r8/DelayedDeathBody.md §Evidence): every AotR
// use is on a summoned/temporary unit (CaveTroll, WildHillTroll, Golfimbul, ...) with
//     DelayedDeathTime = 5000   (ms)
//     DoHealthCheck    = No     ;// "Don't want to get the delayed death behaviour when we die normally."
//     CanRespawn       = No
// The author comment on DoHealthCheck=No states the delayed-death is deliberately NOT triggered
// by dying normally (health reaching zero). Yet DelayedDeathTime is still set - so it must be
// armed by something other than the health-zero path. On a Body the only intrinsic event left is
// object creation. Hence the evidenced, non-no-op behavior is a CREATION-ARMED lifetime: the
// unit lives DelayedDeathTime frames, then dies. This is exactly the summon-duration use those
// units need, and it is the reading that matches the task packet's one-line model.
//
// MUTABLE SIM STATE INVENTORY:
//   - the death-timer state (armed flag, death frame, fired flag) - it needs to TICK, which a
//     BodyModule cannot do (a Body is not on GameLogic's sleepy-update schedule, and C# single
//     inheritance forbids a class being both ActiveBody and UpdateModule). So the timer lives on
//     a companion UpdateModule (DelayedDeathTimer) - the OpenSAGE realization of retail SAGE's
//     multiply-inherited Body+Update object (the engine already uses companion updates like
//     ExperienceHelper/StatusDamageHelper for exactly this "a body-ish concern that ticks"
//     shape). The companion is the [SimState] analyzer-policed file; it owns the timer and its
//     Xfer. See finding F-DDB-1 for the one additive framework seam this needs.
//   - the Body subclass (DelayedDeathBody) itself adds NO mutable field of its own; the immortal
//     floor is a stateless rule that reads the companion's ImmortalActive flag.
//
// THE FIX64 FLOOR (ImmortalUntilDeathTime): while the timer is armed and unfired, the unit may
// not be killed early. Reusing the landed S1 seam (ActiveBody.ClampCombatHealthLoss, the same
// hook ImmortalBody uses), the floor runs on the post-armor / post-scalar / post-Kill health
// loss, entirely in Fix64 on the canonical BodyDamageCore (never the float display view). When
// the timer fires, the companion sets Fired FIRST (so the floor lifts) and THEN kills the unit,
// so the scheduled death is not itself floored.

#nullable enable

using System;
using System.Collections.Generic;
using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

/// <summary>
/// Like ActiveBody, but the object dies on a frame timer (DelayedDeathTime) rather than only
/// from damage, and - when ImmortalUntilDeathTime is set - cannot be killed before that timer
/// expires. The ticking half is the companion <see cref="DelayedDeathTimer"/>.
/// </summary>
public sealed class DelayedDeathBody : ActiveBody
{
    private readonly DelayedDeathBodyModuleData _data;
    private DelayedDeathTimer? _timer;

    internal DelayedDeathBody(GameObject gameObject, IGameEngine gameEngine, DelayedDeathBodyModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _data = moduleData;
    }

    /// <summary>
    /// F-DDB-1: the companion update that ticks the death timer. Created here so it registers in
    /// GameLogic's sleepy-update queue through the object's normal module-construction path (see
    /// the additive seam in <c>GameObject</c>). The Body keeps a reference so its immortal floor
    /// can consult the timer's state.
    /// </summary>
    internal override IEnumerable<BehaviorModule> CreateAuxiliaryModules()
    {
        _timer = new DelayedDeathTimer(GameObject, GameEngine.SimContext, _data);
        yield return _timer;
    }

    /// <summary>
    /// The combat floor (S1 seam), identical in shape to ImmortalBody's, but conditioned on the
    /// timer being armed-and-unfired AND ImmortalUntilDeathTime being set. Runs on the post-armor,
    /// post-scalar, post-Kill-resolution health loss, so an immortal DelayedDeathBody survives
    /// lethal damage and even DAMAGE_KILL until its scheduled death. Fix64 on the canonical core
    /// health (never the float display view).
    /// </summary>
    protected override Fix64 ClampCombatHealthLoss(Fix64 loss)
    {
        if (_timer is not { ImmortalActive: true })
        {
            return loss;
        }

        // loss = min(loss, getHealth() - 1), floored at zero (GPL ImmortalBody floor shape).
        var maxLoss = DamageCore.CurrentHealth - Fix64.One;
        if (maxLoss < Fix64.Zero)
        {
            maxLoss = Fix64.Zero;
        }

        return loss < maxLoss ? loss : maxLoss;
    }

    // ---- the contract Xfer walk. DelayedDeathBody owns no mutable sim state of its own (the
    // timer lives on the companion, which xfers itself), so this is only the class's own version
    // layer over the ActiveBody contract walk. HasSimXfer is inherited (true) from ActiveBody. ----

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        base.Xfer(xfer);
    }

    internal override void Load(StatePersister reader)
    {
        // Retail .sav layout (F9-exempt legacy reader): this class had no runtime module before
        // this port, so there is no bespoke retail layout to remap - it reads as an ActiveBody.
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

// ============================================================================
// THE COMPANION UPDATE - the [SimState] analyzer-policed half (float-free): it owns the death
// timer and ticks it. Created by DelayedDeathBody.CreateAuxiliaryModules and registered by the
// object's normal sleepy-update path. It is the faithful realization of retail SAGE's
// Body+UpdateModule multiple inheritance for this class.
// ============================================================================
[SimState]
public sealed class DelayedDeathTimer : UpdateModule
{
    private readonly DelayedDeathBodyModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Whether a death timer is running.</summary>
    private bool _armed;

    /// <summary>Whether the timer has already fired the scheduled death (one-shot).</summary>
    private bool _fired;

    /// <summary>The frame the object is scheduled to die on (valid only while armed).</summary>
    private LogicFrame _deathFrame;

    internal DelayedDeathTimer(GameObject gameObject, ISimContext context, DelayedDeathBodyModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // Creation-armed lifetime (see the file header's evidence): if DelayedDeathTime is set,
        // schedule the death that far out and wake exactly then. Otherwise sleep forever - the
        // health-check / prerequisite-upgrade arming triggers are Ghidra-gated behavior-fact gaps
        // (F-DDB-2), so with the corpus's DoHealthCheck=No units the timer is armed here or not
        // at all.
        if (_data.DelayedDeathTime > LogicFrameSpan.Zero)
        {
            _armed = true;
            _deathFrame = context.CurrentFrame + _data.DelayedDeathTime;
            SetWakeFrame(UpdateSleepTime.Frames(_data.DelayedDeathTime));
        }
        else
        {
            SetWakeFrame(UpdateSleepTime.Forever);
        }
    }

    /// <summary>
    /// True while the immortal floor should hold: the timer is armed, has not fired, and the data
    /// asks for immortality until the death time. Read by <see cref="DelayedDeathBody"/>.
    /// </summary>
    internal bool ImmortalActive => _armed && !_fired && _data.ImmortalUntilDeathTime;

    public override UpdateSleepTime Update()
    {
        // Nothing more to do once fired, or once the object is gone / dead by another path.
        if (_fired || !_armed || GameObject.IsEffectivelyDead || GameObject.IsDestroyed)
        {
            return UpdateSleepTime.Forever;
        }

        var now = Context.CurrentFrame;
        if (now >= _deathFrame)
        {
            // Set Fired FIRST so ImmortalActive goes false and the floor lifts, THEN kill: the
            // scheduled death must not be floored by our own immortality.
            _fired = true;
            GameObject.Kill();
            return UpdateSleepTime.Forever;
        }

        // Not yet - sleep exactly until the death frame (self-correcting after a load, which may
        // wake us early: we simply recompute and re-sleep).
        return UpdateSleepTime.Frames(_deathFrame - now);
    }

    // ---- the single walk (§3/§4): declaration order, ours (F9). Frame counts are integers on
    // both sides, so the timer field is Exact; the booleans are Exact. ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferBool("Armed", ref _armed);
        xfer.XferBool("Fired", ref _fired);
        xfer.XferFrame("DeathFrame", ref _deathFrame, Tolerance.Exact);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2). R8 Body
// ModuleData audit (S5 vocabulary): DelayedDeathTime is a duration (ms in INI), now
// ceil-quantized to LogicFrameSpan via ParseDurationLogicFrames (S5) instead of a bare integer.
// F-R7-2: the shadowing Parse re-applies the base InitialHealth->MaxHealth default.
// ============================================================================
[SimDataAudited]
[AddedIn(SageGame.Bfme)]
public sealed class DelayedDeathBodyModuleData : ActiveBodyModuleData
{
    internal static new DelayedDeathBodyModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);
        result.ApplyHealthDefaults(parser);   // F-R7-2: keep the base InitialHealth defaulting.
        return result;
    }

    private static new readonly IniParseTable<DelayedDeathBodyModuleData> FieldParseTable = ActiveBodyModuleData.FieldParseTable
        .Concat(new IniParseTable<DelayedDeathBodyModuleData>
        {
            { "DelayedDeathTime", (parser, x) => x.DelayedDeathTime = parser.ParseDurationLogicFrames() },
            { "CanRespawn", (parser, x) => x.CanRespawn = parser.ParseBoolean() },
            { "DoHealthCheck", (parser, x) => x.DoHealthCheck = parser.ParseBoolean() },
            { "ImmortalUntilDeathTime", (parser, x) => x.ImmortalUntilDeathTime = parser.ParseBoolean() },
            { "DelayedDeathPrerequisiteUpgrade", (parser, x) => x.DelayedDeathPrerequisiteUpgrade = parser.ParseAssetReference() },
            { "InvulnerableFX", (parser, x) => x.InvulnerableFX = parser.ParseAssetReference() },
            { "PermanentlyKilledByFilter", (parser, x) => x.PermanentlyKilledByFilter = ObjectFilter.Parse(parser) }
        });

    /// <summary>Frames the unit lives before the timer kills it (ms in INI, ceil-quantized, S5).</summary>
    public LogicFrameSpan DelayedDeathTime { get; private set; }

    /// <summary>Whether the unit can be revived after death (respawn system - Ghidra-gated, F-DDB-2).</summary>
    public bool CanRespawn { get; private set; }

    /// <summary>Whether reaching zero health arms the delayed death (Ghidra-gated arm path, F-DDB-2).</summary>
    public bool DoHealthCheck { get; private set; }

    /// <summary>Whether the unit is immortal (health floored) until the death time.</summary>
    public bool ImmortalUntilDeathTime { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public string? DelayedDeathPrerequisiteUpgrade { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public string? InvulnerableFX { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public ObjectFilter? PermanentlyKilledByFilter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DelayedDeathBody(gameObject, gameEngine, this);
    }
}
