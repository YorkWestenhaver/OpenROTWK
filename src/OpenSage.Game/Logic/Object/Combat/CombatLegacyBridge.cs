// The ONE place the S1 combat system crosses the float substrate during migration
// (deliberately NOT [SimState]: this file's whole job is the crossing, D-7 pattern).
//
// - Float -> Fix64 enters through QuantizeFloat: the IEEE bit pattern is decomposed by
//   Fix64.FromWireFloat's integer path (the F4 wire boundary), so the same float value
//   quantizes to the same raw bits on every machine. Used for legacy DamageInfoInput
//   amounts and the float GameData thresholds.
// - Fix64 -> float leaves through ToFloatForDisplay (the F4 display boundary) to feed
//   unmigrated float consumers (legacy DamageInfo callbacks, retail .sav layout).
//
// Both directions die with the Body/Weapon module batch flag-day (api-freeze
// amendments A2); nothing else in the fork may convert combat quantities.

using System;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

internal static class CombatLegacyBridge
{
    /// <summary>Deterministic float -> Fix64 quantization via the wire-float integer path.</summary>
    public static Fix64 QuantizeFloat(float value)
    {
        return Fix64.FromWireFloat(BitConverter.SingleToUInt32Bits(value));
    }

    /// <summary>
    /// WeaponTemplate.AttackRange, quantized once at the substrate boundary (D-7). Grown for
    /// the PointDefenseLaserUpdate port (R12): its firing-range gate is Fix64 end to end on
    /// the module side, and the template's float AttackRange crosses exactly once, here.
    /// </summary>
    public static Fix64 QuantizeAttackRange(WeaponTemplate template)
    {
        return QuantizeFloat(template.AttackRange);
    }

    /// <summary>Legacy float damage request -> the Fix64 pipeline request.</summary>
    public static CombatDamageInput ToCombatInput(in DamageInfoInput legacy)
    {
        return new CombatDamageInput
        {
            SourceId = legacy.SourceID,
            DamageType = legacy.DamageType,
            DamageStatusType = legacy.DamageStatusType,
            DamageFXOverride = legacy.DamageFXOverride,
            DeathType = legacy.DeathType,
            Amount = QuantizeFloat(legacy.Amount),
            Kill = legacy.Kill,
        };
    }

    /// <summary>Fix64 pipeline request -> legacy float request (for unported Body overrides).</summary>
    public static DamageInfoInput ToLegacyInput(in CombatDamageInput input, GameObject source)
    {
        return new DamageInfoInput(source)
        {
            DamageType = input.DamageType,
            DamageStatusType = input.DamageStatusType,
            DamageFXOverride = input.DamageFXOverride,
            DeathType = input.DeathType,
            Amount = input.Amount.ToFloatForDisplay(),
            Kill = input.Kill,
        };
    }

    /// <summary>Fix64 result -> legacy float result (callback/persist view).</summary>
    public static DamageInfoOutput ToLegacyOutput(in CombatDamageOutput output)
    {
        return new DamageInfoOutput
        {
            ActualDamageDealt = output.ActualDamageDealt.ToFloatForDisplay(),
            ActualDamageClipped = output.ActualDamageClipped.ToFloatForDisplay(),
            NoEffect = output.NoEffect,
        };
    }

    /// <summary>Legacy float result -> Fix64 result (quantizing fallback for unported bodies).</summary>
    public static CombatDamageOutput ToCombatOutput(in DamageInfoOutput legacy)
    {
        return new CombatDamageOutput
        {
            ActualDamageDealt = QuantizeFloat(legacy.ActualDamageDealt),
            ActualDamageClipped = QuantizeFloat(legacy.ActualDamageClipped),
            NoEffect = legacy.NoEffect,
        };
    }
}
