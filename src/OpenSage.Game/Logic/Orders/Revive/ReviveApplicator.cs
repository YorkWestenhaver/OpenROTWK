// ReviveApplicator - the one place a revive purchase is applied (design-respawn-seam.md §5.4).
//
// Shape borrowed from the landed Logic/Orders/SpecialPower/*Applicator family: an order is
// unpacked into typed arguments, then a static applicator resolves objects and calls a named
// method on a named object's named module. Unlike those, this route is LIVE end to end - it is
// modeled on the OrderType.CreateUnit/CancelUnit pair, the only purchase -> queue -> module
// path that actually runs today.
//
// It is a separate, non-[SimState] file on purpose, and that is load-bearing rather than
// stylistic:
//
//   * The SPEND may not happen inside a module. BankAccount.Withdraw calls the audio system
//     directly, so it is not [SimState]-safe; the order side spends (with playSound:false)
//     before the module is reached, exactly as OrderType.CreateUnit already does.
//   * The PRICE cannot be computed inside a module either. ProductionModifier's
//     CostMultiplier/TimeMultiplier are still `float` on ProductionUpdate, so a [SimState]
//     module reading them would be a SIMCORE001 violation at the call site. The multiply
//     happens here, once, through the single landed float->Fix64 boundary
//     (CombatLegacyBridge.QuantizeFloat) and the single pinned rounding
//     (ProductionMath.ApplyProductionMultiplier: one multiply, truncate toward zero).
//
// Determinism: the order arrives on the same frame on every peer and dispatches in the frozen
// (playerIndex, submissionIndex) order; every step below is integer or an already-quantized
// Fix64 multiply; and the CanAfford guard turns a same-frame double-spend into a deterministic
// REFUSAL on every peer instead of a silent clamp (BankAccount.Withdraw clamps to the balance,
// which would otherwise hand out a free revive).

#nullable enable

using OpenSage.Logic.Economy;
using OpenSage.Logic.Object;

namespace OpenSage.Logic.Orders;

/// <summary>What a revive purchase attempt did. Every arm is deterministic on every peer.</summary>
public enum ReviveOrderResult
{
    /// <summary>Applied: the money was withdrawn and the countdown started.</summary>
    Started,

    /// <summary>The hero or the anchor did not resolve, or the hero has no RespawnUpdate.</summary>
    UnknownTarget,

    /// <summary>The hero is not currently awaiting a revive at that anchor (a stale order).</summary>
    NotRevivable,

    /// <summary>The owner cannot afford it right now.</summary>
    Unaffordable,
}

public static class ReviveApplicator
{
    /// <summary>
    /// Applies one <see cref="OrderType.Revive"/>. Ordering inside is fixed and matters:
    /// resolve, validate, price, check affordability, THEN withdraw, then start the countdown.
    /// Nothing is mutated before the last two steps, so every refusal arm leaves the world
    /// untouched.
    /// </summary>
    public static ReviveOrderResult Apply(IGame game, Player? player, ObjectId heroId, ObjectId anchorId)
    {
        var hero = game.GameLogic.GetObjectById(heroId);
        var anchor = game.GameLogic.GetObjectById(anchorId);
        var revive = hero?.FindBehavior<RespawnUpdate>();

        if (revive is null || anchor is null || player is null)
        {
            return ReviveOrderResult.UnknownTarget;
        }

        if (!revive.CanBeRevivedAt(anchor))
        {
            return ReviveOrderResult.NotRevivable;
        }

        var (cost, timeFrames) = Price(game, player!, hero!, anchor, revive);

        if (!game.GameEngine.SimContext.Players.CanAfford(player, (uint)cost))
        {
            return ReviveOrderResult.Unaffordable;
        }

        // playSound:false - the purchase sting is OQ-6 (ISimEvents has no general MiscAudio
        // member and this packet does not grow one for a single caller), and the withdraw's own
        // sound would also NRE on a headless host.
        player.BankAccount.Withdraw((uint)cost, playSound: false);

        revive.BeginRevive(anchor, cost, new LogicFrameSpan((uint)timeFrames));
        return ReviveOrderResult.Started;
    }

    /// <summary>
    /// The revive's cost and countdown after the anchor's applicable HeroRevive
    /// <c>ProductionModifier</c>s. D1: the fortress's ProductionUpdate is what prices a
    /// revival, so the modifiers are read off the ANCHOR, never off the hero.
    /// </summary>
    /// <remarks>
    /// A modifier applies when it is flagged <c>HeroRevive</c>, its <c>RequiredUpgrade</c> is
    /// owned (by the anchor or by its player - SAGE upgrades are held in both places), and its
    /// <c>ModifierFilter</c> matches the DEAD HERO. Multipliers compose in declaration order,
    /// each through the same truncating step, so the result is order-stable.
    /// <para>
    /// A non-positive multiplier is skipped rather than applied. <c>CostMultiplier</c> and
    /// <c>TimeMultiplier</c> are plain <c>float</c> fields defaulting to 0, so a block that
    /// declares only one of them would otherwise zero the other - a free or instant revive
    /// from a field the author never wrote.
    /// </para>
    /// </remarks>
    private static (int Cost, int TimeFrames) Price(
        IGame game,
        Player player,
        GameObject hero,
        GameObject anchor,
        RespawnUpdate revive)
    {
        var cost = revive.BaseReviveCost;
        var timeFrames = (int)revive.BaseReviveTime.Value;

        var production = anchor.FindBehavior<ProductionUpdate>();
        if (production is null)
        {
            return (cost, timeFrames);
        }

        foreach (var modifier in production.ProductionModifiers)
        {
            if (!modifier.HeroRevive)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(modifier.RequiredUpgrade))
            {
                var upgrade = game.AssetStore.Upgrades.GetByName(modifier.RequiredUpgrade);
                if (upgrade is null || !(anchor.HasUpgrade(upgrade) || player.HasUpgrade(upgrade)))
                {
                    continue;
                }
            }

            if (!modifier.ModifierFilter.Matches(hero))
            {
                continue;
            }

            if (modifier.CostMultiplier > 0f)
            {
                cost = ProductionMath.ApplyProductionMultiplier(
                    cost, CombatLegacyBridge.QuantizeFloat(modifier.CostMultiplier));
            }

            if (modifier.TimeMultiplier > 0f)
            {
                timeFrames = ProductionMath.ApplyProductionMultiplier(
                    timeFrames, CombatLegacyBridge.QuantizeFloat(modifier.TimeMultiplier));
            }
        }

        return (cost < 0 ? 0 : cost, timeFrames < 0 ? 0 : timeFrames);
    }
}
