// SpyVisionUpdate - Round-9 full-packet port (api-freeze-v1 §6 fitness function).
//
// Behavioral reference: generals-gpl GeneralsMD GameLogic/Object/Update/SpyVisionUpdate.cpp/.h
// (GPL semantics reference only; this is FRESH code against the frozen contract). The original
// is an UpdateModule + UpgradeMux whose job is: while ACTIVE, spy on the vision of enemy units
// matching SpyOnKindof (GPL doActivationWork -> Player::setUnitsVisionSpied, per enemy player,
// keyed on the owner's player index). Activation is driven three ways: a self-powered
// duration/interval cycle, an upgrade trigger (NeedsUpgrade), and an external special power
// (activateSpyVision). Disable edges (sabotage/EMP) suspend it and re-arm on wake.
//
// PORTABLE-IN-CONTRACT SLICE (what this file implements and tests):
//   - the full activation STATE MACHINE and its sleep scheduling: deactivate frame,
//     currently-active flag, reset-timers-next-update flag, disabled-until frame;
//   - the self-powered duration/interval cycle (GPL update());
//   - upgrade-triggered activation via the shared UpgradeLogic mux (GPL upgradeImplementation);
//   - the external activate/disable entry points (activateSpyVision / setDisabledUntilFrame);
//   - the version-2 Xfer walk over every mutable field.
// The activation STATE is the determinism-relevant surface; it is persisted and asserted.
//
// DELIBERATELY NOT ACTED ON (frozen-contract gaps -> findings, never invented; see
// research/modules-r9/SpyVisionUpdate.md):
//   SVU-1  the shroud/vision REVEAL effect. The S3 verbs (SimPartitionGrid.DoShroudReveal /
//          RevealMapForPlayer) exist as a landed system but the grid is not instantiated in the
//          engine nor reachable through ISimContext - that wiring is the partition flag-day
//          (finding F-PV-1, owned by sys/partition-wiring). Until it lands the reveal is a
//          client-observable OUTPUT with no sim-input obligation, so DoActivationWork records
//          only the activation state (precedent: SimEventsAdapter.FireUnitSoundAtObject records
//          but does not play). No shroud call is faked here.
//   SVU-2  the per-enemy-player vision-spy fan-out. The frozen ISimContext exposes no player
//          enumeration, no inter-player relationship query, and no per-object "vision spied by
//          player" model (GPL Player::setUnitsVisionSpied). Enemy iteration cannot be
//          reproduced; when SVU-1's seam lands it needs these too.
//   SVU-3  onCapture re-flick (GPL onCapture): BehaviorModule has no capture hook.
//   SVU-4  the engine-driven disabled EDGE (GPL onDisabledEdge): ported modules have no
//          disabled-edge callback yet. setDisabledUntilFrame + the reset-timers path ARE
//          implemented and tested; only the engine's edge trigger is missing.
//   SVU-5  onDelete turn-off (GPL onDelete): no death/delete hook on the contract surface.

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

public sealed class SpyVisionUpdate : UpdateModule, IUpgradeableModule
{
    private readonly SpyVisionUpdateModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Frame at/after which an active spy vision turns itself off; the
    /// <see cref="ForeverFrame"/> sentinel means "stays on until told otherwise" (GPL UINT_MAX).</summary>
    private LogicFrame _deactivateFrame;

    /// <summary>Wake target while suspended by a disable (GPL m_disabledUntilFrame).</summary>
    private LogicFrame _disabledUntilFrame;

    /// <summary>Whether the spy vision is currently switched on (GPL m_currentlyActive).</summary>
    private bool _currentlyActive;

    /// <summary>Set when a disable expires so the next update re-arms the timers
    /// (GPL m_resetTimersNextUpdate).</summary>
    private bool _resetTimersNextUpdate;

    // GPL uses UINT_MAX; we use the frame budget's forever sentinel (0x3fffffff) so frame
    // arithmetic never overflows, matching the AutoHeal pilot's convention.
    private static LogicFrame ForeverFrame => new LogicFrame(UpdateSleepTime.SleepForever);

    public SpyVisionUpdate(GameObject gameObject, ISimContext context, SpyVisionUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // GPL ctor: asleep forever until an upgrade / special power / self-power wakes it.
        SetWakeFrame(UpdateSleepTime.Forever);
        _disabledUntilFrame = Context.CurrentFrame;

        // The mux fires OnUpgradeTriggered from its ctor when StartsActive.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    /// <summary>Test/inspection view of the activation flag (the observable effect while the
    /// reveal seam is deferred, SVU-1). This IS persisted sim state, not test-only scaffolding.</summary>
    internal bool IsCurrentlyActive => _currentlyActive;

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        // GPL upgradeImplementation(): only NeedsUpgrade modules activate on the trigger; a
        // duration of 0 means "on permanently". activateSpyVision does the wake-frame setting.
        if (_data.NeedsUpgrade)
        {
            ActivateSpyVision(_data.SelfPoweredDuration);
        }
    }

    /// <summary>GPL activateSpyVision: external (special-power) activation for a given duration;
    /// zero duration = stay on until deactivated.</summary>
    public void ActivateSpyVision(LogicFrameSpan duration)
    {
        var now = Context.CurrentFrame;
        _deactivateFrame = duration == LogicFrameSpan.Zero ? ForeverFrame : now + duration;

        DoActivationWork(true);

        SetWakeFrame(duration == LogicFrameSpan.Zero
            ? UpdateSleepTime.Forever
            : UpdateSleepTime.Frames(duration));
    }

    /// <summary>GPL setDisabledUntilFrame: suspend now, re-arm on wake. Reachable and tested, but
    /// the engine does not yet edge-trigger it (finding SVU-4).</summary>
    internal void SetDisabledUntilFrame(LogicFrame frame)
    {
        var now = Context.CurrentFrame;
        if (frame.Value > now.Value)
        {
            // Turn spy vision off now since we are disabled.
            if (_currentlyActive)
            {
                DoActivationWork(false);
            }

            _disabledUntilFrame = frame;
            _resetTimersNextUpdate = true;

            // Sleep until the disable expires (or until another disable pushes it out again).
            SetWakeFrame(UpdateSleepTime.Frames(new LogicFrameSpan(frame.Value - now.Value)));
        }
        else
        {
            // A wake-up: Update() does the turning back on.
            _disabledUntilFrame = now;
            _resetTimersNextUpdate = true;
            SetWakeFrame(UpdateSleepTime.None);
        }
    }

    /// <summary>GPL onDisabledEdge: nowDisabled => suspend forever, else => wake now. Not engine
    /// wired yet (SVU-4); exposed so the disable path is testable end-to-end.</summary>
    internal void OnDisabledEdge(bool nowDisabled)
        => SetDisabledUntilFrame(nowDisabled ? ForeverFrame : LogicFrame.Zero);

    public override UpdateSleepTime Update()
    {
        var now = Context.CurrentFrame;

        // Coming out of a disable: re-arm. Self-powered with no interval turns straight back on;
        // otherwise wait an interval before re-activating (GPL update() reset block).
        if (_resetTimersNextUpdate)
        {
            _resetTimersNextUpdate = false;

            if (_data.SelfPowered)
            {
                if (_data.SelfPoweredInterval == LogicFrameSpan.Zero)
                {
                    DoActivationWork(true);
                    return UpdateSleepTime.Forever;
                }

                return UpdateSleepTime.Frames(_data.SelfPoweredInterval);
            }
        }

        if (_currentlyActive && _deactivateFrame.Value <= now.Value)
        {
            // Duration elapsed: turn off.
            DoActivationWork(false);
            _deactivateFrame = LogicFrame.Zero;
        }
        else if (!_currentlyActive && _data.SelfPowered)
        {
            // Self-powered: turn on for the duration (zero = until further notice).
            DoActivationWork(true);
            _deactivateFrame = _data.SelfPoweredDuration == LogicFrameSpan.Zero
                ? ForeverFrame
                : now + _data.SelfPoweredDuration;
        }

        if (_data.SelfPowered)
        {
            if (_currentlyActive)
            {
                return _data.SelfPoweredDuration == LogicFrameSpan.Zero
                    ? UpdateSleepTime.Forever
                    : UpdateSleepTime.Frames(_data.SelfPoweredDuration);
            }

            return _data.SelfPoweredInterval == LogicFrameSpan.Zero
                ? UpdateSleepTime.Forever
                : UpdateSleepTime.Frames(_data.SelfPoweredInterval);
        }

        return UpdateSleepTime.Forever;
    }

    private void DoActivationWork(bool setting)
    {
        // GPL doActivationWork iterates every ENEMY player and calls
        // Player::setUnitsVisionSpied(setting, SpyOnKindof, ownerIndex), which shares those
        // units' vision with the owner. Neither the enemy fan-out (SVU-2) nor the reveal seam
        // (SVU-1) exists in the frozen contract, so we record only the activation STATE - the
        // determinism-relevant surface. The reveal is a client-observable output that the
        // partition flag-day (F-PV-1) will drive off this same flag.
        _currentlyActive = setting;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's. Absolute frame
    // indices and the mux flag are integers/bools on both sides -> Tolerance.Exact (ruling A3).

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        // GPL xfer is version 2 (v2 added the reset-timers flag + disabled-until frame); we keep
        // the version number as documentation of the field set, on OUR field order.
        xfer.XferVersion(2);
        _upgradeLogic.Xfer(xfer);
        xfer.XferFrame("DeactivateFrame", ref _deactivateFrame, Tolerance.Exact);
        xfer.XferBool("CurrentlyActive", ref _currentlyActive);
        xfer.XferBool("ResetTimersNextUpdate", ref _resetTimersNextUpdate);
        xfer.XferFrame("DisabledUntilFrame", ref _disabledUntilFrame, Tolerance.Exact);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// GPL SpyVisionUpdateModuleData: an UpdateModuleData carrying the self-power timing, the
// spy-on kindof mask, and an embedded UpgradeMux (TriggeredBy / ConflictsWith / ...).
// ============================================================================
[SimDataAudited]
public sealed class SpyVisionUpdateModuleData : UpdateModuleData
{
    internal static SpyVisionUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<SpyVisionUpdateModuleData> FieldParseTable =
        new IniParseTableChild<SpyVisionUpdateModuleData, UpgradeLogicData>(x => x.UpgradeData, UpgradeLogicData.FieldParseTable)
        .Concat(new IniParseTable<SpyVisionUpdateModuleData>
        {
            // GPL KindOfMaskType::parseFromINI -> a kindof MASK, not a single enum. Default is
            // "all kinds" (GPL sets KINDOFMASK_NONE then flips); null here means the same and is
            // unconsumed until the reveal seam lands (SVU-1/SVU-2), so it has no CRC effect.
            { "SpyOnKindof", (parser, x) => x.SpyOnKindof = parser.ParseEnumBitArray<ObjectKinds>() },
            { "NeedsUpgrade", (parser, x) => x.NeedsUpgrade = parser.ParseBoolean() },
            { "SelfPowered", (parser, x) => x.SelfPowered = parser.ParseBoolean() },
            // GPL parseDurationUnsignedInt -> ms; S5 quantizes to logic frames at parse (ceil).
            { "SelfPoweredDuration", (parser, x) => x.SelfPoweredDuration = parser.ParseDurationLogicFrames() },
            { "SelfPoweredInterval", (parser, x) => x.SelfPoweredInterval = parser.ParseDurationLogicFrames() },
        });

    /// <summary>The embedded UpgradeMux (GPL UpgradeMuxData): TriggeredBy / ConflictsWith /
    /// RequiresAllTriggers / StartsActive / ... shared with every other upgrade-driven module.</summary>
    public UpgradeLogicData UpgradeData { get; } = new();

    /// <summary>Kinds whose vision is spied on; null = all kinds (GPL default). Parsed but not
    /// yet consumed - the reveal fan-out is deferred (SVU-1/SVU-2).</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public BitArray<ObjectKinds> SpyOnKindof { get; private set; }

    /// <summary>Whether an upgrade trigger is what activates the spy vision (GPL m_needsUpgrade).</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public bool NeedsUpgrade { get; private set; }

    /// <summary>Whether the module cycles itself on/off on a duration/interval (GPL m_selfPowered).</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public bool SelfPowered { get; private set; }

    /// <summary>How long each self-powered activation lasts; zero = until deactivated.
    /// (ms in INI, ceil-quantized to logic frames at parse, S5).</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public LogicFrameSpan SelfPoweredDuration { get; private set; }

    /// <summary>Off-time between self-powered activations; zero = turn straight back on.
    /// (ms in INI, ceil-quantized to logic frames at parse, S5).</summary>
    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public LogicFrameSpan SelfPoweredInterval { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new SpyVisionUpdate(gameObject, gameEngine.SimContext, this);
    }
}
