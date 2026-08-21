// PassengersFireUpgrade - R12 module port. Behavioral reference: generals-gpl / BFME-RotWK
// GameEngine Module/PassengersFireUpgrade.cpp/.h (GPL semantics only; this is fresh code
// against the frozen contract).
//
// GPL behavior facts used (the whole state inventory):
//   - PassengersFireUpgrade is a pure upgrade module: NO update tick and NO mutable sim
//     state of its own beyond the shared upgrade-mux triggered flag (UpgradeLogic), same
//     shape as the StatusBitsUpgrade/CastleUpgrade R9-R11 ports.
//   - upgradeImplementation(): set the owning object's contain module
//     m_passengersAllowedToFire flag to true (obj->getContain()->setPassengersAllowedToFire
//     (TRUE)). If the object has no contain module, this is a no-op - there is nothing to
//     flip and no error is raised.
//   - The doc comment on the INI block says contain modules should have
//     "PassengersAllowedToFire = No" set so this upgrade has an observable effect; that is
//     the container's own INI-parsed default (e.g. TransportContainModuleData.
//     PassengersAllowedToFire), not runtime state this module owns.
//
// This mirrors the pilot's contract shape (BehaviorModule + IUpgradeableModule + own
// UpgradeLogic) rather than the legacy UpgradeModule base, for the same reason as
// StatusBitsUpgrade: a stateless module gets nothing from the legacy base's private mux.
// Every mutable sim field this module owns appears in Xfer exactly once (api-freeze-v1 S4 /
// §3): here only the mux flag. The runtime "allowed to fire" flag it flips lives on
// OpenContainModule (GameObject-owned, persisted by that module's own Load walk), not here.

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class PassengersFireUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly UpgradeLogic _upgradeLogic;

    public PassengersFireUpgrade(GameObject gameObject, ISimContext context, PassengersFireUpgradeModuleData data)
        : base(gameObject, context)
    {
        // The mux fires OnUpgradeTriggered from its own ctor when StartsActive (GPL: an
        // initially-active upgrade applies immediately).
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>
    /// GPL upgradeImplementation(): flip the owning object's contain module to allow its
    /// passengers to fire. A unit with no contain module has nothing to flip - no-op, no
    /// error. Idempotent by construction (setting the flag to true twice is the same as
    /// once), and only ever sets it - it never disables passenger firing.
    /// </summary>
    private void OnUpgradeTriggered()
    {
        var contain = GameObject.FindBehavior<OpenContainModule>();
        contain?.SetPassengersAllowedToFire(true);
    }

    // Field order = declaration order = OUR choice (F9). The only mutable sim field is the
    // upgrade mux triggered flag; the contain flag it sets is GameObject-owned state,
    // persisted by that contain module's own walk.
    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // ch.1: UpgradeTriggered, Tolerance.Exact
    }
}

/// <summary>
/// Contain modules should have the "PassengersAllowedToFire" parameter set to "No" in order
/// for this module to work.
/// </summary>
[AddedIn(SageGame.CncGeneralsZeroHour)]
[SimDataAudited]
public sealed class PassengersFireUpgradeModuleData : UpgradeModuleData
{
    internal static PassengersFireUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<PassengersFireUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<PassengersFireUpgradeModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new PassengersFireUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
