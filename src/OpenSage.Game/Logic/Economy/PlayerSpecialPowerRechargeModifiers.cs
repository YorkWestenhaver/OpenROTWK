// mod/spellrechargemodifierupgrademoduledata (R13): the Player-side sink
// SpellRechargeModifierUpgrade writes to. Additive partial-class member only - no change to
// any existing Player member (merge hygiene). Mirrors Economy/PlayerProductionCostModifiers.cs
// exactly. The registry is transient derived sim state rebuilt on load (see
// SpecialPowerRechargeDiscountRegistry.cs), so it is deliberately absent from Player.Persist
// and the Players CRC channel.

namespace OpenSage.Logic;

public partial class Player
{
    /// <summary>
    /// Per-player special-power recharge-time discount modifiers registered by this
    /// player's triggered <c>SpellRechargeModifierUpgrade</c> modules. Query with
    /// <see cref="Economy.SpecialPowerRechargeDiscountRegistry.GetSpecialPowerRechargeDiscountFactor"/>
    /// when computing a special power's recharge time, gated by that power's own
    /// <see cref="Object.SpecialPowerFlag.RespectRechargeTimeDiscount"/> flag. Not yet
    /// wired into a landed recharge-timer path (none exists in this engine snapshot) - see
    /// the filed finding in
    /// research/modules-r13/specs/SpellRechargeModifierUpgradeModuleData.md.
    /// </summary>
    public Economy.SpecialPowerRechargeDiscountRegistry SpecialPowerRechargeDiscount { get; } = new();
}
