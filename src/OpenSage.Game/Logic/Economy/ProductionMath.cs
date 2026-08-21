// The deterministic cost/build-time formulas - GPL ThingTemplate::calcCostToBuild /
// calcTimeToBuild and Player's production-change modifiers (generals-gpl GeneralsMD
// Common/Thing/ThingTemplate.cpp:1532-1600, Common/RTS/Player.cpp
// getProductionCostChangePercent / getProductionTimeChangePercent /
// getProductionCostChangeBasedOnKindOf; semantics only, fresh code), rebuilt in Fix64
// with every float->int conversion pinned.
//
// ROUNDING PIN (recorded in research/systems/economy-production.md): every place the
// original assigns a Real product back into an Int (C++ float->int conversion), the
// conversion truncates toward zero. All inputs here are non-negative in practice, so
// truncation == floor; the helper truncates toward zero anyway so a data-driven negative
// percent stack cannot diverge from the original's conversion rule.
//
// The original computes the whole cost expression in float and converts ONCE at return;
// the time expression converts at EVERY `Int *=` / `Int /=` step. We mirror both shapes
// exactly: cost = one Fix64 product chain, one truncation; time = truncate after each
// step, exactly the C++ statement sequence.

using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Economy;

[SimState]
public static class ProductionMath
{
    /// <summary>Truncate a Fix64 toward zero into an int (the C++ float->int rule).</summary>
    public static int TruncateTowardZero(Fix64 value)
    {
        if (value < Fix64.Zero)
        {
            return (int)-(long)Fix64.Floor(-value);
        }
        return (int)(long)Fix64.Floor(value);
    }

    /// <summary>
    /// GPL <c>ThingTemplate::calcCostToBuild</c>:
    /// <c>cost = buildCost * (1 + costChangePercent) * kindOfCostMultiplier * handicap</c>,
    /// one float expression, one int conversion at return.
    /// </summary>
    /// <param name="buildCost">The template's BuildCost (int, F3).</param>
    /// <param name="costChangePercent">
    /// Player per-template change: "-0.2 equals 20% cheaper" (GPL comment). Zero when none.
    /// </param>
    /// <param name="kindOfCostMultiplier">
    /// The pre-stacked product of the player's KindOf cost changes
    /// (<see cref="StackKindOfCostChange"/>); One when none.
    /// </param>
    /// <param name="handicap">Handicap BUILDCOST multiplier; One by default.</param>
    public static int CalcCostToBuild(int buildCost, Fix64 costChangePercent, Fix64 kindOfCostMultiplier, Fix64 handicap)
    {
        var cost = new Fix64(buildCost)
            * (Fix64.One + costChangePercent)
            * kindOfCostMultiplier
            * handicap;
        return TruncateTowardZero(cost);
    }

    /// <summary>
    /// GPL <c>Player::getProductionCostChangeBasedOnKindOf</c>: each matching KindOf entry
    /// multiplies the running factor by (1 + percent). Call once per matching entry,
    /// starting from <see cref="Fix64.One"/>.
    /// </summary>
    public static Fix64 StackKindOfCostChange(Fix64 runningFactor, Fix64 percent)
        => runningFactor * (Fix64.One + percent);

    /// <summary>
    /// GPL <c>ThingTemplate::calcTimeToBuild</c>, the int-statement sequence with a
    /// truncation after every step:
    ///   1. frames = BuildTime (already quantized to logic frames at parse, S5);
    ///   2. frames = trunc(frames * handicap)                 (BUILDTIME handicap);
    ///   3. frames = trunc(frames * (1 + timeChangePercent))  (player per-template change);
    ///   4. frames = trunc(frames / energyPenaltyRate)        (low-power slowdown, ZH;
    ///      BFME2 has no power grid - pass One);
    ///   5. per EXTRA build facility of the same type: frames = trunc(frames * factoryMult)
    ///      (only when the template appears at its rally point, GPL BC_APPEARS_AT_RALLY_POINT).
    /// </summary>
    public static LogicFrameSpan CalcTimeToBuildFrames(
        LogicFrameSpan buildTime,
        Fix64 handicap,
        Fix64 timeChangePercent,
        Fix64 energyPenaltyRate,
        int extraFactoryCount = 0,
        Fix64 multipleFactoryMultiplier = default)
    {
        var frames = (int)buildTime.Value;

        if (handicap != Fix64.One)
        {
            frames = TruncateTowardZero(new Fix64(frames) * handicap);
        }

        if (timeChangePercent != Fix64.Zero)
        {
            frames = TruncateTowardZero(new Fix64(frames) * (Fix64.One + timeChangePercent));
        }

        if (energyPenaltyRate != Fix64.One && energyPenaltyRate != Fix64.Zero)
        {
            frames = TruncateTowardZero(new Fix64(frames) / energyPenaltyRate);
        }

        // GPL: `if (factoryMult > 0) for (i = 0; i < count - 1; i++) buildTime *= factoryMult;`
        if (multipleFactoryMultiplier > Fix64.Zero)
        {
            for (var i = 0; i < extraFactoryCount; i++)
            {
                frames = TruncateTowardZero(new Fix64(frames) * multipleFactoryMultiplier);
            }
        }

        if (frames < 0)
        {
            frames = 0;
        }

        return new LogicFrameSpan((uint)frames);
    }

    /// <summary>
    /// GPL calcTimeToBuild's energy-penalty rate (ZH power grid; BFME2 callers pass One).
    /// <c>short = (1 - min(ratio, 1)) * lowEnergyPenaltyModifier; rate = 1 - short;</c>
    /// clamped up to <paramref name="minProductionSpeed"/>, capped down to
    /// <paramref name="maxProductionSpeed"/> while underpowered, floored at 0.01.
    /// </summary>
    public static Fix64 CalcEnergyPenaltyRate(
        Fix64 energySupplyRatio,
        Fix64 lowEnergyPenaltyModifier,
        Fix64 minProductionSpeed,
        Fix64 maxProductionSpeed)
    {
        var ratio = energySupplyRatio;
        if (ratio > Fix64.One)
        {
            ratio = Fix64.One;
        }

        var shortfall = (Fix64.One - ratio) * lowEnergyPenaltyModifier;
        var penaltyRate = Fix64.One - shortfall;

        if (penaltyRate < minProductionSpeed)
        {
            penaltyRate = minProductionSpeed;
        }

        if (ratio < Fix64.One && penaltyRate > maxProductionSpeed)
        {
            penaltyRate = maxProductionSpeed;
        }

        if (penaltyRate <= Fix64.Zero)
        {
            // GPL: "Design won't make the minimum 0, they promise" - 0.01 floor.
            penaltyRate = Fix64.One / new Fix64(100);
        }

        return penaltyRate;
    }

    /// <summary>
    /// BFME2 ProductionUpdate <c>ProductionModifier</c> block (CostMultiplier /
    /// TimeMultiplier gated on RequiredUpgrade + ModifierFilter). No GPL reference:
    /// the application point and rounding are a recorded spec gap; pinned here as
    /// one multiply + truncate-toward-zero over the already-computed value.
    /// </summary>
    public static int ApplyProductionMultiplier(int value, Fix64 multiplier)
        => TruncateTowardZero(new Fix64(value) * multiplier);
}
