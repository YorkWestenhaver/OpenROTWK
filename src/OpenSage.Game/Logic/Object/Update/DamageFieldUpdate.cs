// DamageFieldUpdate - R13 port (see
// bfme2-workbench/research/modules-r13/specs/DamageFieldUpdateModuleData.md for the full port
// spec this file implements).
//
// Behavioral reference: no generals-gpl sibling for this BFME2-only class. The port instead
// composes three already-landed [SimState] seams, cited line-for-line in the spec (§0):
//   - PointDefenseLaserUpdate.cs - resolving a WeaponTemplate, caching its first DamageNugget,
//     and delivering direct damage through DamagePipeline with cadence drawn from the weapon's
//     own DelayBetweenShots.
//   - AutoAbilityBehavior.cs - the UpdateModule + hand-composed UpgradeLogic mux shape
//     (SetWakeFrame(Forever) in the ctor; wake on trigger; absolute next-pulse-frame cadence).
//   - AttributeModifierAuraUpdate.cs - the periodic QueryObjectsInRadius -> ObjectFilter.Matches
//     per-candidate loop, including the ascending-ObjectId determinism note and the fact that
//     the S3 entry-overload excludes its own centre object (so the fortress never damages
//     itself; no AllowSelf-style field exists or is invented here).
//
// RequiredUpgrade is a single upgrade name, not the mux's TriggeredBy array, so the parse side
// adapts it into UpgradeLogicData.TriggeredBy rather than re-implementing gating (spec §2.2). An
// absent RequiredUpgrade is treated as "no gate" (UpgradeData.StartsActive = true when
// TriggeredBy is null) - a permanently inert module is not a plausible reading of an optional
// gate field; unexercised by AotR, which authors RequiredUpgrade on every call site.
//
// RELATIONSHIP HAZARD (spec §2.4, not a nuance - a correctness requirement): ObjectFilter.Matches
// parses but ignores every relationship rule bit (Enemies/Allies/Neutrals/SamePlayer/Self/...).
// Every AotR call site authors "ObjectFilter = ALL ENEMIES"; trusting Matches alone would have a
// razor-spines fortress damage its own army and its allies every pulse. Enforced locally below
// (PassesRelationshipGate), the same way PointDefenseLaserUpdate and AttributeModifierAuraUpdate
// enforce relationship next to, not inside, ObjectFilter.Matches - ObjectFilter.cs itself is not
// touched (shared-file, out of scope; reserved for a separate task).
//
// Radius is the module's OWN Radius field (100 in every AotR call site), never the weapon's
// AttackRange and never the DamageNugget's own splash Radius (150) - same posture as
// PointDefenseLaserUpdate, which reads only Damage/DamageType/DeathType off the nugget.
//
// Cadence is the weapon template's own DelayBetweenShots (WeaponTemplate.
// CoolDownDelayBetweenShots), drawn exactly as PointDefenseLaserUpdate.DrawDelayBetweenShots():
// range.Min when Min == Max, otherwise a Context.GameLogicRandom draw. This is the landed,
// grounded cadence source - not the held-back WeaponNugget.FireDelay (see below).
//
// HELD (parsed, not modeled): WeaponNugget.FireDelay / OneShot / Offset. No GPL correspondence
// (GeneralsMD's same-named FireWeaponNugget is an unrelated ObjectCreationList nugget), no
// landed consumer, and no data-derivation: every DamageFieldUpdate call site in unmodified AotR
// authors FireDelay = 0, OneShot = No, and no Offset, so nothing in shipping data exercises
// them. Revisit if a Ghidra-lane doc pins WeaponNugget semantics or AotR authors a non-zero
// value. Do not guess a behavior for them.
//
// Other residual gaps (parked, not invented; spec §5):
//   - ObjectFilter's SamePlayer/NotSimilar/Self/Suicide/NotAirborne/SameHeightOnly/Mines rule
//     bits stay unenforced - no AotR call site here authors them; fixing this belongs in a
//     shared-file ObjectFilter.cs task, already reserved elsewhere.
//   - FX/sound/OCL/projectile nuggets are unmodeled (S8), exactly as PointDefenseLaserUpdate.cs
//     already documents for itself.
//   - The DamageNugget's own Radius/DamageScalar/SpecialObjectFilter/
//     DamageMaxHeightAboveTerrain are not consumed - only Damage/DamageType/DeathType are
//     cached, matching PointDefenseLaserUpdate.cs.
//
// Every mutable sim field appears in Xfer exactly once; tolerances are the field's conformance
// class at its declaration site.

using System.Linq;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class DamageFieldUpdate : UpdateModule, IUpgradeableModule
{
    private readonly DamageFieldUpdateModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    /// <summary>The resolved weapon template, or null when the module is misconfigured (no
    /// weapon authored); the module just parks (PointDefenseLaserUpdate.cs precedent).</summary>
    private readonly WeaponTemplate _weaponTemplate;

    /// <summary>The first DamageNugget's payload, cached once (all Fix64/enum already).</summary>
    private readonly bool _hasDamageNugget;
    private readonly Fix64 _damageAmount;
    private readonly DamageType _damageType;
    private readonly DeathType _deathType;

    // ---- mutable sim state (the whole inventory; every field is in Xfer) ----

    /// <summary>Absolute frame of the next pulse; advanced by DrawDelayBetweenShots() each
    /// time the field pulses.</summary>
    private LogicFrame _nextPulseFrame;

    public DamageFieldUpdate(GameObject gameObject, ISimContext context, DamageFieldUpdateModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        SetWakeFrame(UpdateSleepTime.Forever);

        // The mux fires OnUpgradeTriggered from its own ctor when StartsActive.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);

        _weaponTemplate = _data.FireWeaponNugget?.WeaponName?.Value;
        if (_weaponTemplate == null)
        {
            // Nothing sim-meaningful to do without a weapon.
            return;
        }

        var nugget = _weaponTemplate.Nuggets.OfType<DamageNugget>().FirstOrDefault();
        if (nugget != null)
        {
            _hasDamageNugget = true;
            _damageAmount = nugget.Damage;
            _damageType = nugget.DamageType;
            _deathType = nugget.DeathType;
        }
    }

    /// <summary>Test/inspector-only view of the upgrade gate; not part of the save
    /// contract.</summary>
    internal bool Triggered => _upgradeLogic.Triggered;

    /// <summary>Test/inspector-only view of the next pulse frame; not part of the save
    /// contract.</summary>
    internal LogicFrame NextPulseFrame => _nextPulseFrame;

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        SetWakeFrame(UpdateSleepTime.None);
    }

    public override UpdateSleepTime Update()
    {
        if (!_upgradeLogic.Triggered)
        {
            return UpdateSleepTime.Forever;
        }

        if (_weaponTemplate == null)
        {
            return UpdateSleepTime.Forever;
        }

        if (Context.CurrentFrame >= _nextPulseFrame)
        {
            _nextPulseFrame = Context.CurrentFrame + DrawDelayBetweenShots();
            PulseOnce();
        }

        return UpdateSleepTime.None;
    }

    /// <summary>The §2.3 scan + §2.5 delivery over every surviving, matching candidate within
    /// Radius. The S3 entry-based overload excludes its own centre object, so the fortress
    /// never damages itself.</summary>
    private void PulseOnce()
    {
        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _data.Radius))
        {
            if (candidate.IsDestroyed || candidate.IsEffectivelyDead || candidate.IsOffMap)
            {
                continue;
            }

            if (!PassesRelationshipGate(candidate))
            {
                continue;
            }

            if (_data.ObjectFilter != null && !_data.ObjectFilter.Matches(candidate))
            {
                continue;
            }

            ApplyPulseDamage(candidate);
        }
    }

    /// <summary>
    /// ObjectFilter.Matches ignores the relationship rule bits it parses; enforce them here, the
    /// same way PointDefenseLaserUpdate and AttributeModifierAuraUpdate enforce relationship
    /// locally rather than through the filter. SamePlayer/NotSimilar/Self/Suicide/NotAirborne/
    /// SameHeightOnly/Mines remain unenforced - no AotR call site authors them.
    /// </summary>
    private bool PassesRelationshipGate(GameObject candidate)
    {
        var rules = _data.ObjectFilter?.Rules;
        var wantsEnemies = rules?.Get(ObjectFilterRule.Enemies) ?? false;
        var wantsAllies = rules?.Get(ObjectFilterRule.Allies) ?? false;
        var wantsNeutrals = rules?.Get(ObjectFilterRule.Neutrals) ?? false;

        if (!wantsEnemies && !wantsAllies && !wantsNeutrals)
        {
            return true; // no relationship constraint authored
        }

        return DamagePipeline.GetRelationship(GameObject, candidate) switch
        {
            DamagePipeline.CombatRelationship.Enemies => wantsEnemies,
            DamagePipeline.CombatRelationship.Allies => wantsAllies,
            _ => wantsNeutrals,
        };
    }

    private void ApplyPulseDamage(GameObject candidate)
    {
        if (!_hasDamageNugget)
        {
            // A weapon with no DamageNugget still pulses and still burns cooldown, dealing no
            // damage - same documented posture as PointDefenseLaserUpdate.
            return;
        }

        DamagePipeline.DealDirectDamage(candidate, new CombatDamageInput
        {
            SourceId = GameObject.Id,
            DamageType = _damageType,
            DeathType = _deathType,
            Amount = _damageAmount,
        });
    }

    /// <summary>GPL-equivalent WeaponTemplate::getDelayBetweenShots with no rate-of-fire
    /// modifier applied: uniform draw in [min, max] frames, drawn only when min != max
    /// (PointDefenseLaserUpdate.DrawDelayBetweenShots precedent).</summary>
    private LogicFrameSpan DrawDelayBetweenShots()
    {
        var range = _weaponTemplate.CoolDownDelayBetweenShots;
        if (range.Min == range.Max)
        {
            return range.Min;
        }

        return new LogicFrameSpan((uint)Context.GameLogicRandom.Next((int)range.Min.Value, (int)range.Max.Value));
    }

    // ---- the single walk (§3/§4): save/load + CRC + deep-dump + conformance ----
    // Field order = declaration order = OUR choice (F9), never the original's.

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);                                                  // ch.1: Exact (mux)
        xfer.XferFrame("NextPulseFrame", ref _nextPulseFrame, Tolerance.Quantum);  // ch.2 timer
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================
[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class DamageFieldUpdateModuleData : UpdateModuleData
{
    internal static DamageFieldUpdateModuleData Parse(IniParser parser)
    {
        var result = parser.ParseBlock(FieldParseTable);

        // An absent RequiredUpgrade is treated as "no gate" rather than a permanently inert
        // module (spec §2.2, gap 4) - unexercised by AotR, which authors RequiredUpgrade on
        // every call site.
        if (result.UpgradeData.TriggeredBy == null)
        {
            result.UpgradeData.StartsActive = true;
        }

        return result;
    }

    private static readonly IniParseTable<DamageFieldUpdateModuleData> FieldParseTable = new IniParseTable<DamageFieldUpdateModuleData>
    {
        // Deterministic S3-query radius -> Fix64 (never float across the analyzer wall; S5,
        // PointDefenseLaserUpdateModuleData.ScanRange precedent).
        { "Radius", (parser, x) => x.Radius = parser.ParseFix64() },
        { "ObjectFilter", (parser, x) => x.ObjectFilter = ObjectFilter.Parse(parser) },
        { "RequiredUpgrade", (parser, x) => x.UpgradeData.TriggeredBy = new[] { parser.ParseUpgradeReference() } },
        { "FireWeaponNugget", (parser, x) => x.FireWeaponNugget = WeaponNugget.Parse(parser) }
    };

    public UpgradeLogicData UpgradeData { get; } = new();

    public Fix64 Radius { get; private set; }
    public ObjectFilter ObjectFilter { get; private set; }
    public WeaponNugget FireWeaponNugget { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new DamageFieldUpdate(gameObject, gameEngine.SimContext, this);
    }
}
