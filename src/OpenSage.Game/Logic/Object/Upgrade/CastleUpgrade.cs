// CastleUpgrade - fortress module distributing an upgrade to every castle member
// (R9 castles task).
//
// Behavioral reference: spec-castles.md §3.3: when the trigger upgrade (TriggeredBy)
// completes on the castle, the named Upgrade is passed out to ALL castle members (e.g.
// Upgrade_NumenorStonework granted to every wall/tower member). Purchased via
// CommandButton Command = CASTLE_UPGRADE. WallUpgradeRadius is parsed vocabulary; its
// consumption (radius-limited wall grants) is unrecovered and deferred (finding F-CAS-11).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CastleUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly CastleUpgradeModuleData _moduleData;
    private readonly UpgradeLogic _upgradeLogic;

    internal CastleUpgrade(GameObject gameObject, IGameEngine gameEngine, CastleUpgradeModuleData moduleData)
        : base(gameObject, gameEngine)
    {
        _moduleData = moduleData;
        _upgradeLogic = new UpgradeLogic(moduleData.UpgradeData, OnUpgradeTriggered);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    private void OnUpgradeTriggered()
    {
        if (string.IsNullOrEmpty(_moduleData.Upgrade))
        {
            return;
        }

        var upgradeTemplate = GameEngine.AssetLoadContext.AssetStore.Upgrades.GetByName(_moduleData.Upgrade);
        if (upgradeTemplate == null)
        {
            return;
        }

        // Distribution set = the castle's owned member list. The castle behavior sits on
        // this same object (fortress anchor); a CastleUpgrade on a non-castle object
        // distributes to nobody.
        var castle = GameObject.FindBehavior<CastleBehavior>();
        if (castle == null)
        {
            return;
        }

        foreach (var memberId in castle.MemberIds)
        {
            var member = GameEngine.GameLogic.GetObjectById(memberId);
            if (member != null && !member.IsDestroyed)
            {
                member.Upgrade(upgradeTemplate);
            }
        }
    }

    // ---- the single walk: the trigger flag is the only mutable state ----

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);
    }
}

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class CastleUpgradeModuleData : UpgradeModuleData
{
    internal static CastleUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<CastleUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<CastleUpgradeModuleData>
        {
            { "Upgrade", (parser, x) => x.Upgrade = parser.ParseAssetReference() },
            { "WallUpgradeRadius", (parser, x) => x.WallUpgradeRadius = parser.ParseFix64() },
        });

    /// <summary>The upgrade granted to every castle member when TriggeredBy completes.</summary>
    public string Upgrade { get; private set; }

    /// <summary>Quantized Q31.32; consumption unrecovered (F-CAS-11).</summary>
    [AddedIn(SageGame.Bfme2)]
    public Fix64 WallUpgradeRadius { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CastleUpgrade(gameObject, gameEngine, this);
    }
}
