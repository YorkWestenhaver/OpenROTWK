// InheritUpgradeCreate - Round-10 create-module port to the frozen module contract
// (api-freeze-v1 §3/§5, template v1.1 = pilot-autoheal §3/§6). Lowest-risk Create burn-down.
//
// Behavioral reference: NONE. This module is AddedIn(Bfme2) and BFME-only - there is no
// generals-gpl class to mirror, so the semantics are read off the frozen parse table's three
// fields (Radius, Upgrade, ObjectFilter), exactly as the R10 task packet directs. The shape
// mirrors the R9-landed sibling GrantUpgradeCreate.cs in this same Create/ dir (same
// CreateModule base, same AssetStore upgrade resolve, same PLAYER/OBJECT routing through
// UpgradeTemplate.GrantUpgrade), plus one S3 partition radius scan (R8) for the "inherit from
// nearby" step. All consumed systems are landed public APIs; nothing is reimplemented here.
//
// Semantics implemented (self-evident from the field names, no GPL/Ghidra spec needed):
//   - On create, scan objects within Radius (the S3 partition query, ascending ObjectId).
//   - Of those neighbours, keep the ones matching ObjectFilter and that already carry the
//     named Upgrade. If ANY such neighbour exists, this newly-created object INHERITS the
//     upgrade - i.e. UpgradeTemplate.GrantUpgrade routes it to the object or its owner exactly
//     as GrantUpgradeCreate does.
//   - A missing/empty Upgrade name, or an unresolved AssetStore name, is a silent no-op (a bad
//     name must never crash the sim - same guard as GrantUpgradeCreate).
//   - The creating object excludes itself from the scan: it was just created and cannot be the
//     source it inherits from, and self-exclusion keeps the "is a neighbour a donor" test from
//     ever tripping on the object's own (absent) upgrade.
//
// The scan is deterministic: Context.Partition.QueryObjectsInRadius returns ascending-ObjectId
// GameObjects over the Fix64 SimPartitionGrid (S3), and Radius is a Fix64 quantized once at the
// F4 wire boundary in the parser (parser.ParseFix64, the same boundary AutoHealBehavior.Radius
// uses). No float enters this [SimState] module.
//
// FINDINGS (behaviour-fact gaps, filed not invented - see modules-r10/InheritUpgradeCreate.md):
//   F-IUC-1 ObjectFilter relationship/Things rules: the landed ObjectFilter.Matches(GameObject)
//     tests only KindOf include/exclude - it ignores the relationship rules (Allies/Enemies/
//     SamePlayer/...) and the IncludeThings/ExcludeThings template lists it parses. So this port
//     filters donors by KindOf only; an INI that scoped inheritance to (say) allied same-player
//     objects will over-match until ObjectFilter.Matches grows those rules. Consuming the landed
//     seam as-is; the gap is in the shared filter, not here.
//   F-IUC-2 GrantUpgradeCreate contract-shape note carries over verbatim: CreateModule in this
//     base is still on the legacy (GameObject, IGameEngine) ctor with a StatePersister Load and
//     no IXfer surface. Promoting the shared CreateModule base to the ISimContext contract ctor
//     is a batch-wide change other Create branches depend on, so it stays out of scope; this
//     port mirrors GrantUpgradeCreate exactly and reaches the partition seam through the public
//     IGameEngine.SimContext accessor (as EnemyNearUpdateModuleData.CreateModule already does).
//
// MUTABLE SIM STATE INVENTORY: EMPTY. Like GrantUpgradeCreate, this is a fire-once reaction to
// the create lifecycle event; the upgrade it grants lives on the GameObject / Player upgrade
// masks and is persisted by their own walks, not this module's. This class adds no state, so
// its Load is the version stamp + base chain (the base CreateModule owns the one persisted bit,
// _shouldCallOnBuildComplete). It carries [SimState] purely for analyzer coverage - it has no
// float/random/threading surface, so the SIMCORE001-007 quarantine passes clean over it.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme2)]
[SimState]
public sealed class InheritUpgradeCreate : CreateModule
{
    private readonly InheritUpgradeCreateModuleData _data;

    // ---- mutable sim state: NONE. See the header note; Load is version + base only. ----

    public InheritUpgradeCreate(GameObject gameObject, IGameEngine gameEngine, InheritUpgradeCreateModuleData data)
        : base(gameObject, gameEngine)
    {
        _data = data;
    }

    public override void OnCreate()
    {
        if (string.IsNullOrEmpty(_data.Upgrade))
        {
            return;
        }

        var upgrade = GameEngine.AssetStore.Upgrades.GetByName(_data.Upgrade);
        if (upgrade == null)
        {
            // A missing UpgradeToInherit asset must not crash the sim; silent no-op (same guard
            // GrantUpgradeCreate uses for a bad UpgradeToGrant name).
            return;
        }

        if (!AnyNeighbourHasUpgrade(upgrade))
        {
            return;
        }

        // Routes PLAYER vs OBJECT exactly as GrantUpgradeCreate does (getUpgradeType() fork).
        upgrade.GrantUpgrade(GameObject);
    }

    private bool AnyNeighbourHasUpgrade(UpgradeTemplate upgrade)
    {
        // The S3 partition seam (ISimContext.Partition, ascending ObjectId) reached through the
        // public IGameEngine.SimContext accessor - CreateModule is still the legacy-ctor base,
        // so Context is null here (F-IUC-2). Radius is Fix64; no float crosses the seam.
        foreach (var candidate in GameEngine.SimContext.Partition.QueryObjectsInRadius(GameObject, _data.Radius))
        {
            if (candidate == GameObject)
            {
                continue;
            }

            if (_data.ObjectFilter != null && !_data.ObjectFilter.Matches(candidate))
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

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// On creation, inherits a named upgrade from a nearby object: if any object within
/// <see cref="Radius"/> that matches <see cref="ObjectFilter"/> already carries
/// <see cref="Upgrade"/>, that upgrade is granted (player- or object-scoped) to this object.
/// AddedIn Bfme2; BFME-only, no generals-gpl equivalent.
/// </summary>
[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class InheritUpgradeCreateModuleData : CreateModuleData
{
    internal static InheritUpgradeCreateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<InheritUpgradeCreateModuleData> FieldParseTable = new IniParseTable<InheritUpgradeCreateModuleData>
    {
        // Fix64 at the F4 wire boundary (parser.ParseFix64), matching AutoHealBehavior.Radius:
        // the radius feeds the deterministic S3 partition query, so it must never be a float.
        { "Radius", (parser, x) => x.Radius = parser.ParseFix64() },
        { "Upgrade", (parser, x) => x.Upgrade = parser.ParseAssetReference() },
        { "ObjectFilter", (parser, x) => x.ObjectFilter = ObjectFilter.Parse(parser) }
    };

    /// <summary>Radius (Fix64) of the create-time scan for a donor object.</summary>
    public Fix64 Radius { get; private set; }

    /// <summary>Name of the upgrade to inherit; empty = nothing to inherit (silent no-op).</summary>
    public string Upgrade { get; private set; }

    /// <summary>
    /// Restricts which nearby objects can be donors. Landed <see cref="ObjectFilter.Matches"/>
    /// tests KindOf only (finding F-IUC-1).
    /// </summary>
    public ObjectFilter ObjectFilter { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new InheritUpgradeCreate(gameObject, gameEngine, this);
    }
}
