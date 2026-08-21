// AutoHealBehavior - THE Round-4 pilot port (api-freeze-v1 §5: the canonical template).
//
// Behavioral reference: generals-gpl GeneralsMD AutoHealBehavior.cpp/.h (GPL semantics
// reference only; this is fresh code against the frozen contract). Behavior facts used:
//   - state is exactly { soonestHealFrame, stopped } (+ the upgrade mux triggered flag);
//     the radius particle system is client-side and becomes an ISimEvents output here.
//   - ctor: initially-active triggers the self-upgrade and staggers the first pulse with a
//     logic-RNG draw in [1, healingDelay] frames so a crowd does not pulse on one frame.
//   - onDamage (radius==0, upgrade active, not stopped): a nonzero StartHealingDelay
//     re-arms the wake that far out; otherwise damage force-wakes only when the heal timer
//     has already expired (frame > soonestHealFrame).
//   - update(): stopped or un-upgraded or dead => sleep forever. Whole-player path heals
//     every same-player, on-map, alive, kindof-matching object (skip-self honored).
//     Radius==0 path heals self while below max health, else sleeps forever (woken by
//     damage). Radius>0 path heals allied, alive, kindof-matching objects in range under
//     the sole-benefactor rule; SingleBurst makes that path fire once and sleep forever.
//   - every pulse re-arms soonestHealFrame = now + healingDelay.
// BFME2-only INI additions (ButtonTriggered, HealOnlyOthers, HealOnlyIfNotInCombat,
// AffectsContained, NonStackable, RespawnNearbyHordeMembers, HealOnlyIfNotUnderAttack) have
// no GPL reference and no written behavioral spec yet: they are parsed (audited vocabulary)
// but deliberately not acted on - see pilot-autoheal.md, "behavior-fact gaps".
//
// Every mutable sim field appears in Xfer exactly once (§3); tolerances are the field's
// conformance class at its declaration site (§4).

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AutoHealBehavior : UpdateModule, IUpgradeableModule, IDamageModule
{
    private readonly AutoHealBehaviorModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Earliest frame the next pulse may fire; re-armed by every pulse.</summary>
    private LogicFrame _soonestHealFrame;

    /// <summary>Externally stopped (e.g. by a special power); never restarts itself.</summary>
    private bool _stopped;

    public AutoHealBehavior(GameObject gameObject, ISimContext context, AutoHealBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        SetWakeFrame(UpdateSleepTime.Forever);

        // The mux fires OnUpgradeTriggered from its ctor when StartsActive.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);

        if (data.UpgradeData.StartsActive)
        {
            // Random phasing of the first pulse (GPL ctor): [1, healingDelay] frames, drawn
            // from the context's logic stream (S3) so the stagger is lockstep-identical.
            var delayFrames = (int)_data.HealingDelay.Value;
            if (delayFrames > 1)
            {
                var stagger = Context.GameLogicRandom.Next(1, delayFrames);
                SetWakeFrame(UpdateSleepTime.Frames(new LogicFrameSpan((uint)stagger)));
            }
            else
            {
                SetWakeFrame(UpdateSleepTime.None);
            }
        }
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        // GPL upgradeImplementation(): wake as soon as possible.
        SetWakeFrame(UpdateSleepTime.None);
    }

    /// <summary>External stop (GPL stopHealing): permanent until save/load says otherwise.</summary>
    public void StopHealing()
    {
        _stopped = true;
        _soonestHealFrame = new LogicFrame(UpdateSleepTime.SleepForever);
        SetWakeFrame(UpdateSleepTime.Forever);
    }

    public void OnDamage(in DamageInfo damageData)
    {
        if (_stopped)
        {
            return;
        }

        // GPL onDamage: the re-arm applies only to the self-heal shape.
        if (_upgradeLogic.Triggered && _data.Radius == Fix64.Zero)
        {
            if (_data.StartHealingDelay != LogicFrameSpan.Zero)
            {
                // Getting damaged resets the healing process.
                SetWakeFrame(UpdateSleepTime.Frames(_data.StartHealingDelay));
            }
            else if (Context.CurrentFrame > _soonestHealFrame)
            {
                // Force an immediate wake only when already past the heal timer; otherwise
                // we would heal on the timer AND at every damage input.
                SetWakeFrame(UpdateSleepTime.None);
            }
        }
    }

    public override UpdateSleepTime Update()
    {
        if (_stopped)
        {
            return UpdateSleepTime.Forever;
        }

        if (!_upgradeLogic.Triggered || GameObject.IsEffectivelyDead)
        {
            return UpdateSleepTime.Forever;
        }

        if (_data.AffectsWholePlayer)
        {
            // Whole-player path: every object the owning player controls, blessed
            // ascending-ObjectId iteration (never spatial or hash order).
            foreach (var candidate in Context.GameLogic.ObjectsAscendingId)
            {
                if (candidate.IsEffectivelyDead ||
                    candidate.Owner != GameObject.Owner ||
                    candidate.IsOffMap ||
                    (_data.SkipSelfForHealing && candidate == GameObject) ||
                    !MatchesKindOfFilters(candidate) ||
                    !candidate.HealthBelowMax)
                {
                    continue;
                }

                PulseHealObject(candidate);
            }

            return UpdateSleepTime.Frames(_data.HealingDelay);
        }

        if (_data.Radius == Fix64.Zero)
        {
            // Original system: just heal self, sleep forever at full health (damage wakes us).
            if (GameObject.HealthBelowMax)
            {
                PulseHealObject(GameObject);
                return UpdateSleepTime.Frames(_data.HealingDelay);
            }

            return UpdateSleepTime.Forever;
        }

        // Expanded system: heal allies in radius under the sole-benefactor rule.
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.Radius))
        {
            if (candidate.IsEffectivelyDead ||
                !IsAlliedWith(candidate) ||
                candidate.IsOffMap != GameObject.IsOffMap ||
                !candidate.HealthBelowMax ||
                !MatchesKindOfFilters(candidate) ||
                (_data.SkipSelfForHealing && candidate == GameObject))
            {
                continue;
            }

            PulseHealObject(candidate);
        }

        return _data.SingleBurst
            ? UpdateSleepTime.Forever
            : UpdateSleepTime.Frames(_data.HealingDelay);
    }

    private bool MatchesKindOfFilters(GameObject candidate)
    {
        // GPL defaults: KindOf = all bits (null table entry = match everything),
        // ForbiddenKindOf = empty (null = forbid nothing).
        return _data.KindOf?.Intersects(candidate.Definition.KindOf) != false &&
               _data.ForbiddenKindOf?.Intersects(candidate.Definition.KindOf) != true;
    }

    private bool IsAlliedWith(GameObject candidate)
    {
        return candidate.Owner == GameObject.Owner ||
               candidate.Owner.Allies.Contains(GameObject.Owner);
    }

    private void PulseHealObject(GameObject target)
    {
        if (_stopped)
        {
            return;
        }

        if (_data.Radius == Fix64.Zero)
        {
            target.AttemptHealing(_data.HealingAmount, GameObject);
        }
        else
        {
            // Sole-benefactor rule (GPL attemptHealingFromSoleBenefactor): only one healer
            // may claim a target per window.
            if (target.HealedByObjectId.IsValid && target.HealedByObjectId != GameObject.Id)
            {
                return;
            }

            target.AttemptHealing(_data.HealingAmount, GameObject);
            if (target != GameObject)
            {
                target.SetBeingHealed(GameObject, _data.HealingDelay);
            }
        }

        if (_data.UnitHealPulseFX != null)
        {
            // Output event only - never a sim input (S8).
            Context.Events.FireFXAtObject(_data.UnitHealPulseFX, target.Id);
        }

        // In case OnDamage tries to wake us early.
        _soonestHealFrame = Context.CurrentFrame + _data.HealingDelay;
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);                                                 // ch.1: Exact
        xfer.XferFrame("SoonestHealFrame", ref _soonestHealFrame, Tolerance.Quantum);  // ch.2 timer
        xfer.XferBool("Stopped", ref _stopped);                                   // ch.1: Exact
    }

    // ---- legacy retail-save reader (outside the contract, F9): kept until the save
    // system migrates onto the Xfer walk. Layout from the original .sav stream:
    // base, upgrade mux, 4-byte client particle-system id (discarded), soonest-heal
    // frame, stopped flag. ----
    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();

        reader.PersistObject(_upgradeLogic);

        reader.SkipUnknownBytes(4);

        reader.PersistLogicFrame(ref _soonestHealFrame);

        reader.PersistBoolean(ref _stopped);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[SimDataAudited]
public sealed class AutoHealBehaviorModuleData : UpdateModuleData
{
    internal static AutoHealBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<AutoHealBehaviorModuleData> FieldParseTable =
        new IniParseTableChild<AutoHealBehaviorModuleData, UpgradeLogicData>(x => x.UpgradeData, UpgradeLogicData.FieldParseTable)
        .Concat(new IniParseTable<AutoHealBehaviorModuleData>
        {
            { "HealingAmount", (parser, x) => x.HealingAmount = parser.ParseFix64() },
            { "HealingDelay", (parser, x) => x.HealingDelay = parser.ParseDurationLogicFrames() },
            { "AffectsWholePlayer", (parser, x) => x.AffectsWholePlayer = parser.ParseBoolean() },
            { "KindOf", (parser, x) => x.KindOf = parser.ParseEnumBitArray<ObjectKinds>() },
            { "ForbiddenKindOf", (parser, x) => x.ForbiddenKindOf = parser.ParseEnumBitArray<ObjectKinds>() },
            { "StartHealingDelay", (parser, x) => x.StartHealingDelay = parser.ParseDurationLogicFrames() },
            { "Radius", (parser, x) => x.Radius = parser.ParseFix64() },
            { "SingleBurst", (parser, x) => x.SingleBurst = parser.ParseBoolean() },
            { "SkipSelfForHealing", (parser, x) => x.SkipSelfForHealing = parser.ParseBoolean() },
            { "HealOnlyIfNotInCombat", (parser, x) => x.HealOnlyIfNotInCombat = parser.ParseBoolean() },
            { "ButtonTriggered", (parser, x) => x.ButtonTriggered = parser.ParseBoolean() },
            { "HealOnlyOthers", (parser, x) => x.HealOnlyOthers = parser.ParseBoolean() },
            { "UnitHealPulseFX", (parser, x) => x.UnitHealPulseFX = parser.ParseAssetReference() },
            { "AffectsContained", (parser, x) => x.AffectsContained = parser.ParseBoolean() },
            { "NonStackable", (parser, x) => x.NonStackable = parser.ParseBoolean() },
            { "RespawnNearbyHordeMembers", (parser, x) => x.RespawnNearbyHordeMembers = parser.ParseBoolean() },
            { "RespawnFXList", (parser, x) => x.RespawnFXList = parser.ParseAssetReference() },
            { "RespawnMinimumDelay", (parser, x) => x.RespawnMinimumDelay = parser.ParseInteger() },
            { "HealOnlyIfNotUnderAttack", (parser, x) => x.HealOnlyIfNotUnderAttack = parser.ParseBoolean() }
        });

    public UpgradeLogicData UpgradeData { get; } = new();

    /// <summary>Hit points restored per pulse (quantized Q31.32).</summary>
    public Fix64 HealingAmount { get; private set; }

    /// <summary>Frames between pulses (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan HealingDelay { get; private set; }

    /// <summary>Whether healing affects every object of the owning player.</summary>
    public bool AffectsWholePlayer { get; private set; }

    /// <summary>Kinds eligible for healing; null = all kinds (GPL default: all bits set).</summary>
    public BitArray<ObjectKinds> KindOf { get; private set; }

    /// <summary>Kinds never healed; null = none forbidden.</summary>
    public BitArray<ObjectKinds> ForbiddenKindOf { get; private set; }

    /// <summary>Frames after taking damage before self-healing restarts.</summary>
    public LogicFrameSpan StartHealingDelay { get; private set; }

    /// <summary>Heal-area radius; zero = self only (quantized Q31.32).</summary>
    public Fix64 Radius { get; private set; }

    /// <summary>Whether the radius path fires once and sleeps forever.</summary>
    public bool SingleBurst { get; private set; }

    [AddedIn(SageGame.CncGeneralsZeroHour)]
    public bool SkipSelfForHealing { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool HealOnlyIfNotInCombat { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool ButtonTriggered { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public bool HealOnlyOthers { get; private set; }

    [AddedIn(SageGame.Bfme)]
    public string UnitHealPulseFX { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool AffectsContained { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool NonStackable { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public bool RespawnNearbyHordeMembers { get; private set; }

    [AddedIn(SageGame.Bfme2)]
    public string RespawnFXList { get; private set; }

    /// <summary>Milliseconds (time-as-int, F3); unconsumed until the respawn fact is pinned.</summary>
    [AddedIn(SageGame.Bfme2)]
    public int RespawnMinimumDelay { get; private set; }

    [AddedIn(SageGame.Bfme2Rotwk)]
    public bool HealOnlyIfNotUnderAttack { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AutoHealBehavior(gameObject, gameEngine.SimContext, this);
    }
}
