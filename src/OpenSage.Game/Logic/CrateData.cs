// CrateData - the crate-template flyweight CreateCrateDie tests and draws against
// (generals-gpl GeneralsMD CrateSystem.h/.cpp `CrateTemplate`; GPL semantics reference only).
//
// Audited to the quantized sim vocabulary for the Round-5 Die batch: the two probabilities are
// the operands of logic-RNG comparisons inside a [SimState] module, so they are Fix64 parsed
// through the S5 integer-only boundary (api-freeze-v1 F4/S5) - a float here would be a float
// crossing at the very heart of a draw. The file carries [SimState] so the analyzer wall
// (SIMCORE001-007) actually polices what [SimDataAudited] promises.
//
// Two parse-shape corrections against the GPL reference, both behaviour-visible:
//   - KilledByType is a KindOf MASK (`KindOfMaskType::parseFromINI`), and the killer must have
//     ALL its bits; it was a single ObjectKinds here.
//   - VeterancyLevel defaults to LEVEL_INVALID = "do not test", which nullable models exactly.

#nullable enable

using System.Collections.Generic;
using OpenSage.Content;
using OpenSage.Data.Ini;
using OpenSage.Logic.Object;
using OpenSage.Mathematics;
using OpenSage.SimCore;
using OpenSage.SimCore.Numerics;

namespace OpenSage.Logic;

[SimState]
[SimDataAudited]
public sealed class CrateData : BaseAsset
{
    internal static CrateData Parse(IniParser parser)
    {
        return parser.ParseNamedBlock(
            (x, name) => x.SetNameAndInstanceId("CrateData", name),
            FieldParseTable);
    }

    private static readonly IniParseTable<CrateData> FieldParseTable = new IniParseTable<CrateData>
    {
        { "CreationChance", (parser, x) => x.CreationChance = parser.ParseFix64() },
        { "KilledByType", (parser, x) => x.KilledByType = parser.ParseEnumBitArray<ObjectKinds>() },
        { "KillerScience", (parser, x) => x.KillerScience = parser.ParseScienceReference() },
        { "VeterancyLevel", (parser, x) => x.VeterancyLevel = parser.ParseEnum<VeterancyLevel>() },
        { "OwnedByMaker", (parser, x) => x.OwnedByMaker = parser.ParseBoolean() },
        { "CrateObject", (parser, x) => x.CrateObjects.Add(CrateObject.Parse(parser)) },
    };

    /// <summary>
    /// Probability in [0, 1] that this crate template fires at all, compared against one
    /// logic-RNG draw per death (quantized Q31.32). GPL default 0 = never.
    /// </summary>
    public Fix64 CreationChance { get; private set; }

    /// <summary>
    /// Kinds the killer must have - ALL of them (GPL <c>isKindOfMulti</c>). No bits set means
    /// the test is not run at all.
    /// </summary>
    public BitArray<ObjectKinds>? KilledByType { get; private set; }

    /// <summary>
    /// Science required by the killer's controlling player; null = not tested.
    /// </summary>
    public LazyAssetReference<Science>? KillerScience { get; private set; }

    /// <summary>
    /// The <b>victim</b> must have exactly this veterancy level; null (GPL LEVEL_INVALID) =
    /// not tested.
    /// </summary>
    public VeterancyLevel? VeterancyLevel { get; private set; }

    /// <summary>
    /// "To have the Crate assigned to the default team of the dead guy's player for scripting."
    /// </summary>
    public bool OwnedByMaker { get; private set; }

    /// <summary>
    /// One-of-n weighted crates picked by a second draw once the template's conditions pass.
    /// Declaration order is the weighting order (contiguous-percentage walk), so it is
    /// load-bearing and never sorted.
    /// </summary>
    public List<CrateObject> CrateObjects { get; } = [];
}

/// <summary>
/// One weighted entry of <see cref="CrateData.CrateObjects"/>: GPL <c>crateCreationEntry</c>.
/// </summary>
public readonly record struct CrateObject(LazyAssetReference<ObjectDefinition>? Object, Fix64 Probability)
{
    internal static CrateObject Parse(IniParser parser)
    {
        return new CrateObject(parser.ParseObjectReference(), parser.ParseFix64());
    }
}
