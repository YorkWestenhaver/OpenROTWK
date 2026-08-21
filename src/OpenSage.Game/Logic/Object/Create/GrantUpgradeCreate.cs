// GrantUpgradeCreate - Round-9 create-module port to the frozen module contract
// (api-freeze-v1 §3/§5, template v1.1 = pilot-autoheal §3/§6).
//
// Behavioral reference: generals-gpl GeneralsMD + Generals GrantUpgradeCreate.cpp/.h (GPL
// semantics only; this is fresh code). BFME2 intended semantics per experiment-round-4 §4.1
// packet (the binding authority): "on create (or on build-complete when GiveOnBuildComplete),
// grant UpgradeToGrant to object/owner unless ExemptStatus set."
//
// Behavior facts used (and the Generals-vs-BFME2 delta, filed as a finding in the doc):
//   - Generals/GeneralsMD onCreate() grants only when the module's ExemptStatus mask contains
//     UNDER_CONSTRUCTION *and* the object is not currently under construction; onBuildComplete()
//     always grants (gated by shouldDoOnBuildComplete). It has no GiveOnBuildComplete field.
//   - BFME2 refactored that create/build-complete timing into the explicit GiveOnBuildComplete
//     boolean the fork's parse table already carries, and generalised ExemptStatus from a mask
//     to a single status "skip the grant if the object currently has this status". This port
//     implements the BFME2 shape the packet specifies:
//       * GiveOnBuildComplete = No  -> grant on create, unless the object has ExemptStatus.
//       * GiveOnBuildComplete = Yes -> grant on build-complete (no exempt gate, matching the
//         original onBuildComplete which never tests exempt status).
//   - The upgrade is resolved by name from the AssetStore. A missing/empty name is a silent
//     no-op (the original DEBUG_ASSERTCRASHes then returns; the shipped build just returns).
//   - Player-vs-object routing is delegated to UpgradeTemplate.GrantUpgrade (Type==PLAYER ->
//     owner.AddUpgrade(Completed); Type==OBJECT -> object.Upgrade), which is exactly the
//     original's `if (getUpgradeType()==UPGRADE_TYPE_PLAYER) player->addUpgrade(...) else
//     obj->giveUpgrade(...)` fork. AcademyStats::recordUpgrade (GeneralsMD-only telemetry)
//     has no sim effect and no landed equivalent; deliberately omitted (finding).
//
// MUTABLE SIM STATE INVENTORY: EMPTY. Like the GPL class (whose xfer is version + base chain),
// this module is a fire-once reaction to the create/build-complete lifecycle events; the
// upgrade it grants lives on the GameObject / Player upgrade masks and is persisted by their
// own walks, not this module's. The base CreateModule owns the one persisted bit relevant here
// (_shouldCallOnBuildComplete) via its Load. This class adds no state, so its Load is the
// version stamp + base chain, and that is the complete walk, not an omission.
//
// Contract-shape / merge-hygiene note: CreateModule in this base is still on the legacy
// (GameObject, IGameEngine) constructor with a StatePersister Load and no IXfer contract
// surface (see SupplyCenterCreate, the landed sibling this packet points at). Promoting the
// shared CreateModule base to the ISimContext contract is a batch-wide change other Create-
// module branches depend on, so it is out of scope here (finding). This port therefore mirrors
// SupplyCenterCreate exactly and carries [SimState] purely for analyzer coverage - it has no
// float/random/threading surface, so the SIMCORE001-007 quarantine passes clean over it.

using OpenSage.Data.Ini;
using OpenSage.SimCore;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class GrantUpgradeCreate : CreateModule
{
    private readonly GrantUpgradeCreateModuleData _data;

    // ---- mutable sim state: NONE. See the header note; Load is version + base only. ----

    public GrantUpgradeCreate(GameObject gameObject, IGameEngine gameEngine, GrantUpgradeCreateModuleData data)
        : base(gameObject, gameEngine)
    {
        _data = data;
    }

    public override void OnCreate()
    {
        // GiveOnBuildComplete defers the grant to FinishConstruction -> OnBuildComplete.
        if (_data.GiveOnBuildComplete)
        {
            return;
        }

        // "do not execute if this status is set in the object" (GPL m_exemptStatus). Only the
        // create path is gated; the original's onBuildComplete never tests exempt status.
        if (IsExempt())
        {
            return;
        }

        GrantUpgrade();
    }

    protected override void OnBuildCompleteImpl()
    {
        if (!_data.GiveOnBuildComplete)
        {
            return;
        }

        GrantUpgrade();
    }

    private bool IsExempt()
    {
        // ObjectStatus.None is the "no exempt status configured" sentinel (GPL default
        // OBJECT_STATUS_NONE); an unset field must not accidentally test a real bit.
        return _data.ExemptStatus != ObjectStatus.None && GameObject.TestStatus(_data.ExemptStatus);
    }

    private void GrantUpgrade()
    {
        if (string.IsNullOrEmpty(_data.UpgradeToGrant))
        {
            return;
        }

        var upgrade = GameEngine.AssetStore.Upgrades.GetByName(_data.UpgradeToGrant);
        if (upgrade == null)
        {
            // The original DEBUG_ASSERTCRASHes on a bad UpgradeToGrant name and returns; the
            // shipped build just returns. A missing asset must not crash the sim.
            return;
        }

        // Routes PLAYER vs OBJECT exactly as the original's getUpgradeType() fork.
        upgrade.GrantUpgrade(GameObject);
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
/// Grants a named upgrade (player- or object-scoped) to the object or its owner at creation, or
/// deferred to build-complete when <see cref="GiveOnBuildComplete"/> is set. Skips the create-time
/// grant when the object already carries <see cref="ExemptStatus"/>.
/// </summary>
[SimDataAudited]
public sealed class GrantUpgradeCreateModuleData : CreateModuleData
{
    internal static GrantUpgradeCreateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<GrantUpgradeCreateModuleData> FieldParseTable = new IniParseTable<GrantUpgradeCreateModuleData>
    {
        { "UpgradeToGrant", (parser, x) => x.UpgradeToGrant = parser.ParseAssetReference() },
        { "ExemptStatus", (parser, x) => x.ExemptStatus = parser.ParseEnum<ObjectStatus>() },
        { "GiveOnBuildComplete", (parser, x) => x.GiveOnBuildComplete = parser.ParseBoolean() }
    };

    /// <summary>Name of the upgrade to grant; empty = nothing to grant (silent no-op).</summary>
    public string UpgradeToGrant { get; private set; }

    /// <summary>
    /// If the object already has this status at create time, the create-time grant is skipped.
    /// Defaults to <see cref="ObjectStatus.None"/> (GPL <c>OBJECT_STATUS_NONE</c>) so an
    /// unspecified field never masks a real status bit.
    /// </summary>
    public ObjectStatus ExemptStatus { get; private set; } = ObjectStatus.None;

    [AddedIn(SageGame.Bfme2)]
    public bool GiveOnBuildComplete { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new GrantUpgradeCreate(gameObject, gameEngine, this);
    }
}
