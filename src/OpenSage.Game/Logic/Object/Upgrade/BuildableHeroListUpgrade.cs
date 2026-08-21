// BuildableHeroListUpgrade - R13 module port. Marker upgrade module: the whole retail
// state inventory is the shared upgrade-mux triggered flag (UpgradeLogic), same shape as
// the AllowBannerSpawnUpgrade / StatusBitsUpgrade pilots.
//
// GPL/data facts used (the whole state inventory):
//   - BuildableHeroListUpgrade is a pure marker upgrade module: NO update tick, NO fields
//     of its own beyond the standard UpgradeModuleData block (TriggeredBy/ConflictsWith/
//     StartsActive/...), and NO OnUpgrade side effect - confirmed by direct read of the
//     pre-port file's FieldParseTable (module-specific table is empty) and by the AotR
//     data corpus (system.ini, two usages, both `TriggeredBy = Upgrade_RingHero` only, no
//     module-specific override anywhere the module is used in shipped data).
//   - No GPL sibling exists (`BuildableHeroList`/`HeroList` has no Generals/ZH analog); this
//     is a BFME2-only concept. Its retail purpose (gating which hero(es) become buildable
//     once the trigger upgrade lands) is presumably consumed by a not-yet-landed
//     hero-production/command-set module that polls this module's Triggered flag -
//     analogous to how AllowBannerSpawnUpgrade's Triggered flag is consumed by
//     SimBannerCarrierUpdate. That consumer is unrecovered/parked (see spec F-BHLU-1); this
//     module only owns and persists the trigger state, it does not (yet) gate anything
//     itself.
//
// This mirrors the AllowBannerSpawnUpgrade contract shape (BehaviorModule + IUpgradeableModule +
// own UpgradeLogic) rather than the legacy UpgradeModule base, which is still on the
// IGameEngine ctor with a private mux and no contract Xfer; going through it would force a
// shared-file edit for zero benefit on a stateless module. Every mutable sim field this
// module owns appears in Xfer exactly once (api-freeze-v1 S4 / §3): here only the mux flag.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class BuildableHeroListUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly BuildableHeroListUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public BuildableHeroListUpgrade(GameObject gameObject, ISimContext context, BuildableHeroListUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // The mux fires OnUpgradeTriggered from its own ctor when StartsActive (GPL-independent
        // base behavior on UpgradeLogicData). The callback is a no-op here: this module has no
        // known consumer in this engine snapshot (F-BHLU-1); it is a pure marker exposing
        // Triggered for a future consumer to poll.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    /// <summary>Whether the buildable-hero-list upgrade has been triggered on this object.</summary>
    public bool Triggered => _upgradeLogic.Triggered;

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        // No side effect: marker-only module, no known consumer (F-BHLU-1). See file header.
    }

    // Field order = declaration order = OUR choice (F9). The only mutable sim field is the
    // upgrade mux triggered flag.
    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // ch.1: UpgradeTriggered, Tolerance.Exact
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class BuildableHeroListUpgradeModuleData : UpgradeModuleData
{
    internal static BuildableHeroListUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<BuildableHeroListUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<BuildableHeroListUpgradeModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new BuildableHeroListUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
