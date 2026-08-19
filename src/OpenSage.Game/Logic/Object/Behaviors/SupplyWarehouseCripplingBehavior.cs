// SupplyWarehouseCripplingBehavior - Round-10 structure/economy port (full task packet,
// template v1.1).
//
// Behavioral reference: generals-gpl GeneralsMD SupplyWarehouseCripplingBehavior.cpp/.h (GPL
// semantics reference only; this is FRESH code against the frozen contract). Behavior facts used:
//   - The module is an UpdateModule that also reacts to damage (GPL: UpdateModule +
//     DamageMuxInterface). Two jobs, wholly independent:
//       (a) CRIPPLING: on the body's damage-state transition, cripple/uncripple the supply dock.
//           onBodyDamageStateChange(old,new): entering BODY_REALLYDAMAGED -> dock.setDockCrippled(true);
//           leaving  BODY_REALLYDAMAGED -> dock.setDockCrippled(false). (GPL startCrippledEffects/
//           stopCrippledEffects both just call getObject()->getDockUpdateInterface()->setDockCrippled.)
//       (b) SELF-HEAL: a suppress-then-repeat timer, driven entirely by the sleepy-update system.
//   - ctor: both frame timers 0; sleep FOREVER (a healthy warehouse never ticks).
//   - onDamage(): ANY damage resets the suppression window and wakes the module that far out.
//     resetSelfHealSupression(): m_healingSupressedUntilFrame = now + selfHealSupression;
//     m_nextHealingFrame = m_healingSupressedUntilFrame; then setWakeFrame(supressedUntil - now).
//   - update(): suppression is enforced by sleeping, so being here means it is time to heal.
//     m_nextHealingFrame = now + selfHealDelay; attemptHealing(selfHealAmount, NULL); at full
//     health return SLEEP_FOREVER (damage wakes us again), else SLEEP(nextHealingFrame - now).
//   - crc/xfer: version(1) then base, then the two UnsignedInt frame timers. That is the whole
//     mutable-state inventory of THIS module (the crippled flag lives on the dock - see below).
//
// MUTABLE SIM STATE INVENTORY (written before any code, runbook step 1):
//   { m_healingSupressedUntilFrame, m_nextHealingFrame } - both absolute logic frames.
//   The crippled flag is NOT this module's state: GPL stores it on the DockUpdate and xfers it
//   there (DockUpdate::xfer). This port keeps that placement (see DockUpdate.SetDockCrippled),
//   so the behavior's Xfer walk carries only the two timers, matching GPL::xfer exactly.
//
// The downstream *effect* of a crippled supply dock (GPL: refuse entrance clearance / kill the
// active docker) rides the docking-clearance logic, which the landed SupplyWarehouseDockUpdate
// does not yet implement (it is a simplified box-dispenser stub). This port wires the relay
// end-to-end and lands the observable flag on the dock; the clearance consumption is a documented
// TODO, mirroring how BoneFXDamage (R7) landed its ChangeBodyDamageState relay into an unported
// BoneFXUpdate. See modules-r10/SupplyWarehouseCripplingBehavior.md finding F-SWCB-1.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class SupplyWarehouseCripplingBehavior : UpdateModule, IDamageModule
{
    private readonly SupplyWarehouseCripplingBehaviorModuleData _data;

    // ---- mutable sim state (the whole inventory; every field is in Xfer exactly once) ----

    /// <summary>Frame until which healing is suppressed after the most recent damage.</summary>
    private LogicFrame _healingSuppressedUntilFrame;

    /// <summary>Frame the next heal pulse is scheduled for.</summary>
    private LogicFrame _nextHealingFrame;

    public SupplyWarehouseCripplingBehavior(GameObject gameObject, ISimContext context, SupplyWarehouseCripplingBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        _healingSuppressedUntilFrame = LogicFrame.Zero;
        _nextHealingFrame = LogicFrame.Zero;

        // A healthy warehouse never heals: sleep until damage wakes us (GPL ctor).
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    // ---- (a) crippling: body damage-state transitions gate the dock ----

    public void OnBodyDamageStateChange(in DamageInfo damageInfo, BodyDamageType oldState, BodyDamageType newState)
    {
        if (newState == BodyDamageType.ReallyDamaged)
        {
            StartCrippledEffects();
        }
        else if (oldState == BodyDamageType.ReallyDamaged)
        {
            StopCrippledEffects();
        }
    }

    private void StartCrippledEffects()
    {
        GameObject.FindBehavior<DockUpdate>()?.SetDockCrippled(true);
    }

    private void StopCrippledEffects()
    {
        GameObject.FindBehavior<DockUpdate>()?.SetDockCrippled(false);
    }

    // ---- (b) self-heal: suppress-then-repeat timer driven by the sleepy-update system ----

    public void OnDamage(in DamageInfo damageInfo)
    {
        // We got hit: reset the suppression window, then wake after a quick snooze (GPL onDamage).
        ResetSelfHealSuppression();
        SetWakeFrame(UpdateSleepTime.Frames(_healingSuppressedUntilFrame - Context.CurrentFrame));
    }

    private void ResetSelfHealSuppression()
    {
        _healingSuppressedUntilFrame = Context.CurrentFrame + _data.SelfHealSuppression;
        _nextHealingFrame = _healingSuppressedUntilFrame;
    }

    public override UpdateSleepTime Update()
    {
        // Suppression is enforced by sleeping the module, so if we are here it is time to heal.
        var now = Context.CurrentFrame;
        _nextHealingFrame = now + _data.SelfHealDelay;

        GameObject.AttemptHealing(_data.SelfHealAmount, null);

        // At full health, sleep forever (this cannot live in an OnHealing hook: the heal comes
        // from HERE in the update, and a sleep set there would be overridden by our return value).
        if (!GameObject.HealthBelowMax)
        {
            return UpdateSleepTime.Forever;
        }

        // The delay between heals is also handled by sleeping the module.
        return UpdateSleepTime.Frames(_nextHealingFrame - now);
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9). Both timers are absolute logic frames,
    // conformance class Quantum (the frame-timer tolerance, as AutoHealBehavior's SoonestHealFrame).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        xfer.XferFrame("HealingSuppressedUntilFrame", ref _healingSuppressedUntilFrame, Tolerance.Quantum);
        xfer.XferFrame("NextHealingFrame", ref _nextHealingFrame, Tolerance.Quantum);
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept until the save system
    // migrates onto the Xfer walk. Layout from GPL xfer: version, base, then the two frames. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistLogicFrame(ref _healingSuppressedUntilFrame);
        reader.PersistLogicFrame(ref _nextHealingFrame);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// GPL buildFieldParse: SelfHealSupression/SelfHealDelay are parseDurationUnsignedInt (ms ->
// frames), SelfHealAmount is parseReal (health points). S5 audited vocabulary:
//   duration -> LogicFrameSpan (ParseDurationLogicFrames), health -> Fix64 (ParseFix64).
// ============================================================================
[SimDataAudited]
public sealed class SupplyWarehouseCripplingBehaviorModuleData : UpdateModuleData
{
    internal static SupplyWarehouseCripplingBehaviorModuleData Parse(IniParser parser)
    {
        return parser.ParseBlock(FieldParseTable);
    }

    private static readonly IniParseTable<SupplyWarehouseCripplingBehaviorModuleData> FieldParseTable = new IniParseTable<SupplyWarehouseCripplingBehaviorModuleData>
    {
        { "SelfHealSupression", (parser, x) => x.SelfHealSuppression = parser.ParseDurationLogicFrames() },
        { "SelfHealDelay", (parser, x) => x.SelfHealDelay = parser.ParseDurationLogicFrames() },
        { "SelfHealAmount", (parser, x) => x.SelfHealAmount = parser.ParseFix64() }
    };

    /// <summary>Frames since last damage until healing may start (ms in INI, quantized at parse, S5).</summary>
    public LogicFrameSpan SelfHealSuppression { get; private set; }

    /// <summary>Frames between heal pulses once healing is allowed (ms in INI, quantized at parse, S5).</summary>
    public LogicFrameSpan SelfHealDelay { get; private set; }

    /// <summary>Hit points restored per pulse (quantized Q31.32).</summary>
    public Fix64 SelfHealAmount { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SupplyWarehouseCripplingBehavior(gameObject, gameEngine.SimContext, this);
    }
}
