using System;
using OpenSage.Data.Ini;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic.Object;

/// <summary>
/// An armor encapsulates a set of Fix64 multipliers for different types of damage taken,
/// in order to simulate different materials, and to help make game balance easier to
/// adjust.
///
/// S1 audit: coefficients are quantized ONCE at parse via the blessed integer text
/// boundary (ParseFix64Percentage, F4) and consumed by the Fix64 damage pipeline.
/// Behavioral reference: generals-gpl GeneralsMD GameLogic/Object/Armor.cpp
/// (ArmorTemplate::adjustDamage / parseArmorCoefficients - semantics only, fresh code).
/// </summary>
public sealed class ArmorTemplate : BaseAsset
{
    private static readonly int DamageTypeCount = Enum.GetValues(typeof(DamageType)).Length;

    internal static ArmorTemplate Parse(IniParser parser)
    {
        return parser.ParseNamedBlock(
            (x, name) => x.SetNameAndInstanceId("Armor", name),
            FieldParseTable);
    }

    private static readonly IniParseTable<ArmorTemplate> FieldParseTable = new IniParseTable<ArmorTemplate>
    {
        { "DamageScalar", (parser, x) => x.DamageScalar = parser.ParseFix64Percentage() },
        {
            "Armor",
            (parser, x) =>
            {
                var damageTypeString = parser.ParseString();
                var percent = parser.ParseFix64Percentage();

                if (string.Equals(damageTypeString, "DEFAULT", StringComparison.InvariantCultureIgnoreCase))
                {
                    // GPL parseArmorCoefficients: Default fills every coefficient.
                    for (var i = 0; i < x.Values.Length; i++)
                    {
                        x.Values[i] = percent;
                    }
                }
                else
                {
                    var damageType = IniParser.ParseEnum<DamageType>(damageTypeString);
                    x.Values[(int)damageType] = percent;
                }

            }
        },
        { "FlankedPenalty", (parser, x) => x.FlankedPenalty = parser.ParseFix64Percentage() }
    };

    private static Fix64[] CreateDefaultValues()
    {
        // GPL ArmorTemplate::clear(): every coefficient defaults to 1.0 (100%).
        var values = new Fix64[DamageTypeCount];
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = Fix64.One;
        }
        return values;
    }

    /// <summary>
    /// Scales all damage done to this unit. PARSED but not yet applied: BFME-only field
    /// with no GPL reference; where it enters the resolution order is a Ghidra gap
    /// (research/systems/weapon-damage-armor.md).
    /// </summary>
    [AddedIn(SageGame.Bfme)]
    public Fix64 DamageScalar { get; private set; } = Fix64.One;

    /// <summary>Per-damage-type multipliers, Q31.32, default 100%.</summary>
    public Fix64[] Values { get; } = CreateDefaultValues();

    /// <summary>
    /// PARSED but not yet applied: BFME2-only flanking multiplier, no GPL reference
    /// (Ghidra gap, see design note).
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public Fix64 FlankedPenalty { get; private set; } = Fix64.One;

    /// <summary>
    /// Given a damage type and amount, adjusts the damage and returns the amount that
    /// should be dealt. GPL adjustDamage: UNRESISTABLE and SUBDUAL_UNRESISTABLE bypass
    /// armor entirely; the result is clamped at zero.
    /// </summary>
    internal Fix64 AdjustDamage(DamageType damageType, Fix64 damage)
    {
        if (damageType == DamageType.Unresistable ||
            damageType == DamageType.SubdualUnresistable)
        {
            return damage;
        }

        damage *= Values[(int)damageType];

        if (damage < Fix64.Zero)
        {
            damage = Fix64.Zero;
        }

        return damage;
    }
}

internal readonly struct Armor(ArmorTemplate template)
{
    public static readonly Armor NoArmor = new Armor(null);

    public Fix64 AdjustDamage(DamageType type, Fix64 damage)
    {
        return template?.AdjustDamage(type, damage) ?? damage;
    }
}
