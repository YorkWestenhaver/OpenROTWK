// ToolTipUpgrade - R12 port. BFME-only client-UI module (census: no generals-gpl sibling
// under this name) that stores a localized DisplayName string key and swaps it in as the
// object's active tooltip name once its upgrade trigger fires (e.g. garrison unlock,
// weapon-platform addition). No update tick and no mutable sim field of its own beyond the
// shared upgrade-mux triggered flag, following the StatusBitsUpgrade contract shape
// (BehaviorModule/IUpgradeableModule + own UpgradeLogic) rather than the legacy
// UpgradeModule base.
//
// The retail feature repaints a client-side tooltip string; OpenSage's UI layer is not
// sim-visible (S8: no UI host in ISimContext), so this module models the sim-visible half
// only - which DisplayName is active for the object right now - as a value derived from
// the mux flag rather than stored separately, so there is nothing beyond the flag to Xfer.
// A UI host reads CurrentDisplayName to paint the tooltip; the paint itself is not modeled
// here (matches the LargeGroupAudioUpdate parked-runtime precedent for client-audio/UI
// features with no sim-visible tick behavior).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class ToolTipUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly ToolTipUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public ToolTipUpgrade(GameObject gameObject, ISimContext context, ToolTipUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // The mux fires OnUpgradeTriggered from its own ctor when StartsActive (an
        // initially-active upgrade applies immediately), same as StatusBitsUpgrade.
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    /// <summary>
    /// The tooltip DisplayName that should be shown for this object right now: the
    /// upgraded name once the trigger has fired, otherwise null (a UI host falls back to
    /// the object's base template name). Derived from the mux flag on every read, so it is
    /// not itself separate mutable sim state.
    /// </summary>
    public string CurrentDisplayName => _upgradeLogic.Triggered ? _data.DisplayName : null;

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>
    /// Client-UI-only effect: the tooltip repaint happens above the sim boundary (no UI
    /// host in ISimContext). The sim-visible half of the effect - CurrentDisplayName
    /// flipping to the upgraded name - is a pure function of the mux flag this callback
    /// sets, so there is no additional side effect to perform here.
    /// </summary>
    private void OnUpgradeTriggered()
    {
    }

    // Field order = declaration order (F9). The only mutable sim field is the upgrade mux
    // triggered flag; DisplayName is immutable parsed INI data, not sim state.
    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // ch.1: UpgradeTriggered, Tolerance.Exact
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class ToolTipUpgradeModuleData : UpgradeModuleData
{
    internal static ToolTipUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<ToolTipUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<ToolTipUpgradeModuleData>
        {
            { "DisplayName", (parser, x) => x.DisplayName = parser.ParseLocalizedStringKey() }
        });

    public string DisplayName { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ToolTipUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
