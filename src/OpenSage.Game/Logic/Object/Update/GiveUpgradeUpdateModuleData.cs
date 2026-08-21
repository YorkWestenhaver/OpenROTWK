// GiveUpgradeUpdateModuleData - split out of GiveUpgradeUpdate.cs (R13 port; spec:
// bfme2-workbench/research/modules-r13/specs/GiveUpgradeUpdateModuleData.md).
//
// Kept in its own file, separate from the [SimState] GiveUpgradeUpdate class, specifically so
// that FadeOutSpeed can stay float: the SimCore scoped-analysis mode (docs/simcore-analyzer.md
// "Attachment modes") pulls in a whole file once it declares any [SimState] type, and this
// spec's §2.3 ruling is explicit that FadeOutSpeed must NOT become Fix64 - it is client
// presentation with no sim consumer, so converting it would imply a sim reader this port does
// not create. See the gap note in GiveUpgradeUpdate.cs for the other held fields.

using OpenSage.Data.Ini;
using OpenSage.SimCore.Numerics;
using OpenSage.SimCore.Ticking;

namespace OpenSage.Logic.Object;

[AddedIn(SageGame.Bfme)]
[SimDataAudited]
public sealed class GiveUpgradeUpdateModuleData : UpdateModuleData
{
    internal static GiveUpgradeUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<GiveUpgradeUpdateModuleData> FieldParseTable = new IniParseTable<GiveUpgradeUpdateModuleData>
    {
        { "SpecialPowerTemplate", (parser, x) => x.SpecialPowerTemplate = parser.ParseAssetReference() },
        { "StartAbilityRange", (parser, x) => x.StartAbilityRange = parser.ParseFix64() },
        { "UnpackTime", (parser, x) => x.UnpackTime = parser.ParseDurationLogicFrames() },
        { "PreparationTime", (parser, x) => x.PreparationTime = parser.ParseDurationLogicFrames() },
        { "PersistentPrepTime", (parser, x) => x.PersistentPrepTime = parser.ParseDurationLogicFrames() },
        { "PackTime", (parser, x) => x.PackTime = parser.ParseDurationLogicFrames() },
        { "ApproachRequiresLOS", (parser, x) => x.ApproachRequiresLos = parser.ParseBoolean() },
        { "SpawnOutFX", (parser, x) => x.SpawnOutFX = parser.ParseAssetReference() },
        { "DeliverUpgrade", (parser, x) => x.DeliverUpgrade = parser.ParseBoolean() },
        { "FadeOutSpeed", (parser, x) => x.FadeOutSpeed = parser.ParseFloat() },
    };

    public string SpecialPowerTemplate { get; private set; }
    public Fix64 StartAbilityRange { get; private set; }
    public LogicFrameSpan UnpackTime { get; private set; }
    public LogicFrameSpan PreparationTime { get; private set; }
    public LogicFrameSpan PersistentPrepTime { get; private set; }
    public LogicFrameSpan PackTime { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public bool ApproachRequiresLos { get; private set; }

    /// <summary>Parsed and held; not currently modeled - see the file-header gap note.</summary>
    public string SpawnOutFX { get; private set; }

    /// <summary>
    /// Parsed and held; not currently modeled - see the file-header gap note. Exposed
    /// read-only on the runtime module as <see cref="GiveUpgradeUpdate.DeliversUpgrade"/>.
    /// </summary>
    public bool DeliverUpgrade { get; private set; }

    /// <summary>
    /// Parsed and held; not currently modeled - see the file-header gap note. Stays float
    /// deliberately (spec §2.3): it is never read by sim code, so SIMCORE001 is not implicated,
    /// and converting it to Fix64 would imply a sim consumer this port does not create.
    /// </summary>
    public float FadeOutSpeed { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new GiveUpgradeUpdate(gameObject, gameEngine.SimContext, this);
    }
}
