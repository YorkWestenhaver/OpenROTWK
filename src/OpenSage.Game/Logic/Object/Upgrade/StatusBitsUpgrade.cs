// StatusBitsUpgrade - Round-4 module port (experiment-round-4 §4.1; template v1.1,
// pilot-autoheal.md §6 runbook). Behavioral reference: generals-gpl / BFME-RotWK
// GameEngine Module/StatusBitsUpgrade.cpp/.h (GPL semantics only; this is fresh code
// against the frozen contract).
//
// GPL behavior facts used (the whole state inventory):
//   - StatusBitsUpgrade is a pure upgrade module: NO update tick and NO mutable sim state
//     of its own. Its only lifecycle state is the shared upgrade-mux triggered flag
//     (UpgradeLogic), exactly as the AutoHealBehavior pilot owns it.
//   - upgradeImplementation(): loop the parsed StatusToSet bit set and set each named
//     ObjectStatus bit on the owning object (obj->setStatus(bit)). Those bits live on the
//     GameObject's own status BitArray, which the GameObject persists itself - they are
//     external effects of this module, not module state, so they are NOT in this module's
//     Xfer walk.
//
// This mirrors the pilot's contract shape (UpdateModule/BehaviorModule + IUpgradeableModule
// + own UpgradeLogic) rather than the legacy UpgradeModule base, which is still on the
// IGameEngine ctor with a private mux and no contract Xfer; going through it would force a
// shared-file edit for zero benefit on a stateless module. Every mutable sim field this
// module owns appears in Xfer exactly once (api-freeze-v1 S4 / §3): here only the mux flag.

using OpenSage.Data.Ini;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class StatusBitsUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly StatusBitsUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    public StatusBitsUpgrade(GameObject gameObject, ISimContext context, StatusBitsUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;

        // The mux fires OnUpgradeTriggered from its own ctor when StartsActive (GPL:
        // an initially-active upgrade applies immediately).
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    /// <summary>
    /// GPL upgradeImplementation(): set each named StatusToSet bit, then clear each named
    /// StatusToClear bit on the owning object (obj->setStatus(m_statusToSet);
    /// obj->clearStatus(m_statusToClear) - GPL StatusBitsUpgrade.cpp, set-then-clear order).
    /// Idempotent by construction. (StatusToClear restored in the R9 drift review: live AotR
    /// data drives it, e.g. aicoding/retreat.inc ModuleTag_Retreating.)
    /// </summary>
    private void OnUpgradeTriggered()
    {
        if (_data.StatusToSet != null)
        {
            foreach (var status in _data.StatusToSet.GetSetBits())
            {
                GameObject.SetObjectStatus(status, true);
            }
        }

        if (_data.StatusToClear != null)
        {
            foreach (var status in _data.StatusToClear.GetSetBits())
            {
                GameObject.SetObjectStatus(status, false);
            }
        }
    }

    // Field order = declaration order = OUR choice (F9). The only mutable sim field is the
    // upgrade mux triggered flag; the status bits it sets are GameObject-owned state,
    // persisted by GameObject's own walk.
    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // ch.1: UpgradeTriggered, Tolerance.Exact
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class StatusBitsUpgradeModuleData : UpgradeModuleData
{
    internal static StatusBitsUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<StatusBitsUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<StatusBitsUpgradeModuleData>
        {
            { "StatusToSet", (parser, x) => x.StatusToSet = parser.ParseEnumBitArray<ObjectStatus>() },
            { "StatusToClear", (parser, x) => x.StatusToClear = parser.ParseEnumBitArray<ObjectStatus>() }
        });

    [AddedIn(SageGame.Bfme2Rotwk)]
    public BitArray<ObjectStatus> StatusToSet { get; private set; }

    /// <summary>GPL m_statusToClear: bits cleared after StatusToSet is applied.</summary>
    [AddedIn(SageGame.Bfme2Rotwk)]
    public BitArray<ObjectStatus> StatusToClear { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new StatusBitsUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
