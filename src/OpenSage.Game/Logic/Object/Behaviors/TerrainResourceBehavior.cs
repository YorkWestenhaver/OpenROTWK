// TerrainResourceBehavior - R13 port. Data-derivable (no GPL sibling; the Generals/ZH economy
// mechanic is the truck-hauls-resource-piles supply chain (SupplyWarehouseDockUpdate.h/
// SupplyCenterDockUpdate.h/SupplyTruckAIUpdate.h), structurally unrelated to this passive
// area-income building - see bfme2-workbench/research/modules-r13/specs/
// TerrainResourceBehaviorModuleData.md §0/§1 for the full field-by-field grounding). The tick
// mechanic below is a from-field-set derivation cross-checked against two landed sibling
// patterns (AttributeModifierAuraUpdate's periodic-rearm-scan shape, AutoDepositUpdate's
// direct-poll upgrade-bonus shape), not an invention: every field on the frozen schema is
// assigned exactly one role (spec §1.2).
//
// Every IncomeInterval frames: scan Context.Partition.QueryObjectsInRadius(GameObject, Radius)
// for a live candidate that matches UpgradeMustBePresent and carries Upgrade; if found, deposit
// MaxIncome plus an uncapped UpgradeBonusPercent extra (spec F-TRB-1: this port takes the
// uncapped-extra reading, not a MaxIncome-as-hard-ceiling reading, because the uncapped reading
// is the only one that doesn't require inventing an unauthored "base income" field - filed for
// port-review sign-off, not blocking). UpgradeMustBePresent == null or Upgrade?.Value == null
// means the bonus condition can never be true (null-is-inert, same convention as
// AttributeModifierAuraUpdate.RefreshTargets's bonus == null early return) - note GameObject.
// HasUpgrade(null) itself returns true, so this module checks Upgrade?.Value == null explicitly
// rather than relying on HasUpgrade's own null handling.
//
// TODO-spec (filed, not invented - spec §1.3):
//   F-TRB-2 (HighPriority): parsed and stored, never consumed - no priority-ordered cash-event
//     queue exists in ISimContext (client-presentation-only, deliberately excluded surface).
//   F-TRB-3 (Visible): parsed and stored, never consumed - minimap/fog-of-war visibility has no
//     sim-side seam (ISimContext deliberately excludes rendering/UI).
//   F-TRB-4 (precedent divergence): AutoDepositUpdate is the only other landed module parsing
//     this exact Upgrade/UpgradeBonusPercent/UpgradeMustBePresent triple, and it parses-but-
//     never-applies all three (its own bonus path reads the unrelated UpgradedBoost field
//     instead). This module's consumption of the triple is therefore a fresh, first-of-its-kind
//     reading grounded in the spec's own field-complete audit, not a copy of a landed runtime
//     precedent - flagged because there is no second landed data point to cross-check against.
//   Not modeled: no CashEvent-equivalent client-visual is requested for the income deposit
//     (unlike AutoDepositUpdate.GenerateAutoDepositCashEvent, which lives on the legacy
//     IGameEngine-era GameObject.ActiveCashEvent field) - the money deposit itself is the
//     sim-visible effect either way.
//
// Fix64.FromInt does not exist on this engine's Fix64 (spec §1.1 named it, the actual API is
// the public Fix64(int) constructor) - Radius is converted via `new Fix64(Radius)` instead;
// noted here since the spec's line drifted from the code.
//
// This module carries zero of its own mutable sim fields (spec §2: no accumulator, no counter,
// no cached eligibility state - every tick recomputes the scan fresh). The IncomeInterval-cadence
// wake schedule is engine-owned (UpdateModule.NextWakeFrameForWalk, xfered by the per-object
// walk, never by this module - design-module-api §1.3). Xfer body is the version stamp only.

using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class TerrainResourceBehavior : UpdateModule
{
    private readonly TerrainResourceBehaviorModuleData _data;

    /// <summary>Radius, converted once at construction (parse-side is a raw int, spec §1.1).</summary>
    private readonly Fix64 _radius;

    public TerrainResourceBehavior(GameObject gameObject, ISimContext context, TerrainResourceBehaviorModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _radius = new Fix64(data.Radius);

        SetWakeFrame(UpdateSleepTime.Frames(_data.IncomeInterval));
    }

    public override UpdateSleepTime Update()
    {
        var amount = _data.MaxIncome;

        if (HasNearbyMatchingUpgradedObject())
        {
            var bonus = (int)(long)Fix64.Floor(new Fix64(_data.MaxIncome) * _data.UpgradeBonusPercent);
            amount += bonus;
        }

        // playSound: false - this is the sim lane; audio is a client concern and BankAccount's
        // sound path dereferences the (headless-absent) audio system. Same posture as the landed
        // SlaughterHordeContain refund deposit.
        GameObject.Owner.BankAccount.Deposit((uint)amount, playSound: false);

        return UpdateSleepTime.Frames(_data.IncomeInterval);
    }

    /// <summary>
    /// Spec §1.2 step 1: a live candidate within <see cref="TerrainResourceBehaviorModuleData.Radius"/>
    /// (excluding self) that matches <see cref="TerrainResourceBehaviorModuleData.UpgradeMustBePresent"/>
    /// and carries <see cref="TerrainResourceBehaviorModuleData.Upgrade"/>. Either field unauthored
    /// (null) means the bonus can never apply - not an error, just "no bonus configured".
    /// </summary>
    private bool HasNearbyMatchingUpgradedObject()
    {
        var filter = _data.UpgradeMustBePresent;
        var upgrade = _data.Upgrade?.Value;
        if (filter == null || upgrade == null)
        {
            return false;
        }

        foreach (var candidate in Context.Partition.QueryObjectsInRadius(GameObject, _radius))
        {
            if (candidate == GameObject)
            {
                continue;
            }

            if (candidate.IsDestroyed || candidate.IsEffectivelyDead || candidate.IsOffMap)
            {
                continue;
            }

            if (!filter.Matches(candidate))
            {
                continue;
            }

            if (candidate.HasUpgrade(upgrade))
            {
                return true;
            }
        }

        return false;
    }

    // ---- the single walk (F8 Objects channel): zero mutable fields, version stamp only ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer) => xfer.XferVersion(1);
}

// ============================================================================
// PARSE SIDE - immutable flyweight, quantized at load (design-module-api §2.2).
// ============================================================================

/// <summary>
/// Passive area-income terrain marker: every <see cref="IncomeInterval"/> frames, deposits
/// <see cref="MaxIncome"/> (plus an uncapped <see cref="UpgradeBonusPercent"/> extra when a
/// matching, upgraded structure is nearby) into the owning player's bank account.
/// </summary>
[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class TerrainResourceBehaviorModuleData : UpgradeModuleData
{
    internal static TerrainResourceBehaviorModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<TerrainResourceBehaviorModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<TerrainResourceBehaviorModuleData>
        {
            { "Radius", (parser, x) => x.Radius = parser.ParseInteger() },
            { "MaxIncome", (parser, x) => x.MaxIncome = parser.ParseInteger() },
            // ms in INI, ceil-quantized to logic frames at parse (S5 wire boundary) - the same
            // [SimState]-family idiom as AttributeModifierAuraUpdate.RefreshDelay/EmpUpdate's
            // duration fields, not the legacy ParseTimeMillisecondsToLogicFrames() idiom
            // AutoDepositUpdate.DepositTiming still uses (spec §1.1).
            { "IncomeInterval", (parser, x) => x.IncomeInterval = parser.ParseDurationLogicFrames() },
            { "HighPriority", (parser, x) => x.HighPriority = parser.ParseBoolean() },
            { "Visible", (parser, x) => x.Visible = parser.ParseBoolean() },
            { "Upgrade", (parser, x) => x.Upgrade = parser.ParseUpgradeReference() },
            // text / 100 exactly (decimal exponent shift, no division) - Percentage is a
            // float-backed struct, illegal in this [SimState]-scoped file (spec §1.1).
            { "UpgradeBonusPercent", (parser, x) => x.UpgradeBonusPercent = parser.ParseFix64Percentage() },
            { "UpgradeMustBePresent", (parser, x) => x.UpgradeMustBePresent = ObjectFilter.Parse(parser) },
        });

    public int Radius { get; private set; }
    public int MaxIncome { get; private set; }

    /// <summary>Frames between income deposits (ms in INI, ceil-quantized at parse, S5).</summary>
    public LogicFrameSpan IncomeInterval { get; private set; }

    /// <summary>F-TRB-2: parsed and stored; no priority-ordered cash-event queue is exposed
    /// through ISimContext, so this is not consumed.</summary>
    public bool HighPriority { get; private set; }

    /// <summary>F-TRB-3: parsed and stored; minimap/fog-of-war visibility has no sim-side seam.</summary>
    public bool Visible { get; private set; }

    public LazyAssetReference<UpgradeTemplate> Upgrade { get; private set; }

    /// <summary>Exact-fraction bonus fraction (e.g. 50% -&gt; 0.5), applied to <see cref="MaxIncome"/>
    /// as an uncapped extra (spec F-TRB-1) when a nearby object matches <see cref="UpgradeMustBePresent"/>
    /// and carries <see cref="Upgrade"/>.</summary>
    public Fix64 UpgradeBonusPercent { get; private set; }

    public ObjectFilter UpgradeMustBePresent { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
        => new TerrainResourceBehavior(gameObject, gameEngine.SimContext, this);
}
