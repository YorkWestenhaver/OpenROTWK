// mod/costmodifierupgrademoduledata (R9): the Player-side sink CostModifierUpgrade writes
// to. Additive partial-class member only - no change to any existing Player member (merge
// hygiene). GPL parallel: Player::m_kindOfPercentProductionChangeList + its add/remove/get
// methods (Common/RTS/Player.cpp). The registry is transient derived sim state rebuilt on
// load (see KindOfProductionCostRegistry.cs), so it is deliberately absent from
// Player.Persist and the Players CRC channel.

using OpenSage.Logic.Economy;

namespace OpenSage.Logic;

public partial class Player
{
    /// <summary>
    /// Per-KindOf production-cost-change modifiers registered by this player's triggered
    /// <c>CostModifierUpgrade</c> modules. Query it with
    /// <see cref="KindOfProductionCostRegistry.GetProductionCostChangeBasedOnKindOf"/> when
    /// computing build cost (GPL <c>ThingTemplate::calcCostToBuild</c>). Not yet wired into
    /// the landed production-cost path - see the finding in
    /// research/modules-r9/CostModifierUpgradeModuleData.md.
    /// </summary>
    public KindOfProductionCostRegistry ProductionCostModifiers { get; } = new();
}
