// AllowBannerSpawnUpgrade - R12 module port. Marker upgrade module: the whole retail
// state inventory is the shared upgrade-mux triggered flag (UpgradeLogic), same shape as
// the StatusBitsUpgrade / LargeGroupAudioUpdate pilots.
//
// GPL behavior facts used (the whole state inventory):
//   - AllowBannerSpawnUpgrade is a pure marker upgrade module: NO update tick, NO fields
//     of its own beyond the standard UpgradeModuleData block (TriggeredBy/ConflictsWith/
//     StartsActive/...), and NO OnUpgrade side effect - it exists so the owning object can
//     be queried for "does this object have the banner-spawn upgrade" via the module's
//     Triggered flag.
//   - The consumer of that flag is SimBannerCarrierUpdate.Update's UpgradeRequired check
//     (banner-carrier unit spawning in hordes). That seam is parked pending R13
//     spec work (see task packet) - this module only owns and persists the trigger state;
//     it does not (yet) gate anything itself.
//
// This mirrors the StatusBitsUpgrade contract shape (BehaviorModule + IUpgradeableModule +
// own UpgradeLogic) rather than the legacy UpgradeModule base, which is still on the
// IGameEngine ctor with a private mux and no contract Xfer; going through it would force a
// shared-file edit for zero benefit on a stateless module. Every mutable sim field this
// module owns appears in Xfer exactly once (api-freeze-v1 S4 / §3): here only the mux flag.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class AllowBannerSpawnUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly AllowBannerSpawnUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public AllowBannerSpawnUpgrade(GameObject gameObject, ISimContext context, AllowBannerSpawnUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // The mux fires OnUpgradeTriggered from its own ctor when StartsActive (GPL: an
        // initially-active upgrade applies immediately). The callback is a no-op here: this
        // module has no side effect of its own, it is a pure marker read by
        // SimBannerCarrierUpdate (parked; R13).
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    /// <summary>Whether the banner-spawn upgrade has been triggered on this object.</summary>
    public bool Triggered => _upgradeLogic.Triggered;

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        // No side effect: marker-only module. See file header.
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
public sealed class AllowBannerSpawnUpgradeModuleData : UpgradeModuleData
{
    internal static AllowBannerSpawnUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<AllowBannerSpawnUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<AllowBannerSpawnUpgradeModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new AllowBannerSpawnUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
