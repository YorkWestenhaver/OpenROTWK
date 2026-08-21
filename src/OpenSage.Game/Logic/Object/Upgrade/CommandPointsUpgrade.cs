// CommandPointsUpgrade - R13 port (modules-r13/specs/CommandPointsUpgradeModuleData.md).
//
// No GPL reference exists for Command Points (BFME2-only population/army-cap mechanic; see
// Logic/Economy/ResourceBank.cs's CommandPointsBank header). This is data-derivable: the
// module applies a flat delta to Player.CommandPoints.Limit (CommandPointsBank.SetLimit),
// the same shape as GameData's GoodCommandPointsBonus/EvilCommandPointsBonus but applied
// per-upgrade instead of as a single game-data constant.
//
// Contract shape (why this subclasses BehaviorModule, not UpgradeModule): identical to
// CostModifierUpgrade (Upgrade/CostModifierUpgrade.cs) - UpgradeModule's only ctor is the
// legacy (GameObject, IGameEngine) path and its UpgradeLogic mux is private, so a subclass
// cannot produce the frozen-contract Xfer walk. Composes its own UpgradeLogic instead,
// copying CostModifierUpgrade's structure field-for-field.
//
// RequiredObject guard shape copied from the landed InheritUpgradeCreate idiom
// (Create/InheritUpgradeCreate.cs:106): unset (null) always matches; a non-null filter is
// tested against this module's own host GameObject (not a neighbour scan - this module has
// no radius field).
//
// The single Xfer walk carries every mutable sim field (the mux flag) exactly once.
// CommandPoints/RequiredObject are immutable data-table values; the Player-side running
// total lives on Player.CommandPoints (out of this module's Xfer - see F-CPU-1 in the spec).

using OpenSage.Data.Ini;
using OpenSage.SimCore;
using OpenSage.SimCore.Sync;

namespace OpenSage.Logic.Object;

[SimState]
public sealed class CommandPointsUpgrade : BehaviorModule, IUpgradeableModule
{
    private readonly CommandPointsUpgradeModuleData _data;
    private readonly UpgradeLogic _upgradeLogic;

    // ---- derived runtime state (NOT xfered: rebuilt from the mux flag on load) ----
    private bool _appliedToPlayer;

    public CommandPointsUpgrade(GameObject gameObject, ISimContext context, CommandPointsUpgradeModuleData data)
        : base(gameObject, context)
    {
        _data = data;
        _upgradeLogic = new UpgradeLogic(data.UpgradeData, OnUpgrade);
    }

    public bool CanUpgrade(UpgradeSet existingUpgrades) => _upgradeLogic.CanUpgrade(existingUpgrades);

    public void TryUpgrade(UpgradeSet completedUpgrades) => _upgradeLogic.TryUpgrade(completedUpgrades);

    public bool IsUpgraded => _upgradeLogic.Triggered;

    private void OnUpgrade()
    {
        ApplyToPlayer(GameObject.Owner);
    }

    /// <summary>Undo the cap addition. No landed lifecycle hook calls this yet (same posture
    /// as CostModifierUpgrade.RemoveFromPlayer - recorded finding, exposed for owner/tests).</summary>
    public void RemoveFromPlayer() => RemoveFromPlayer(GameObject.Owner);

    /// <summary>Move the cap addition between players on capture. Same posture/finding as
    /// CostModifierUpgrade.OnCapture.</summary>
    public void OnCapture(Player oldOwner, Player newOwner)
    {
        if (!_upgradeLogic.Triggered)
        {
            return;
        }

        RemoveFromPlayer(oldOwner);
        ApplyToPlayer(newOwner);
    }

    /// <summary>Post-load reconstruction of the transient _appliedToPlayer flag - same shape
    /// as CostModifierUpgrade.ReapplyAfterLoad (recorded finding: no landed post-load module
    /// pass calls this yet).</summary>
    public void ReapplyAfterLoad()
    {
        if (_upgradeLogic.Triggered && !_appliedToPlayer)
        {
            ApplyToPlayer(GameObject.Owner);
        }
    }

    private void ApplyToPlayer(Player player)
    {
        if (_appliedToPlayer || player is null)
        {
            return;
        }

        if (_data.RequiredObject != null && !_data.RequiredObject.Matches(GameObject))
        {
            return;
        }

        player.CommandPoints.SetLimit(player.CommandPoints.Limit + _data.CommandPoints);
        _appliedToPlayer = true;
    }

    private void RemoveFromPlayer(Player player)
    {
        if (!_appliedToPlayer || player is null)
        {
            return;
        }

        player.CommandPoints.SetLimit(player.CommandPoints.Limit - _data.CommandPoints);
        _appliedToPlayer = false;
    }

    internal override bool HasSimXfer => true;

    public override void Xfer(IXfer xfer)
    {
        xfer.XferVersion(1);
        _upgradeLogic.Xfer(xfer);   // UpgradeTriggered, Exact (XferBool)
    }
}

[AddedIn(SageGame.Bfme2)]
[SimDataAudited]
public sealed class CommandPointsUpgradeModuleData : UpgradeModuleData
{
    internal static CommandPointsUpgradeModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<CommandPointsUpgradeModuleData> FieldParseTable = UpgradeModuleData.FieldParseTable
        .Concat(new IniParseTable<CommandPointsUpgradeModuleData>
        {
            { "CommandPoints", (parser, x) => x.CommandPoints = parser.ParseInteger() },
            { "RequiredObject", (parser, x) => x.RequiredObject = ObjectFilter.Parse(parser) }
        });

    public int CommandPoints { get; private set; }
    public ObjectFilter RequiredObject { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new CommandPointsUpgrade(gameObject, gameEngine.SimContext, this);
    }
}
