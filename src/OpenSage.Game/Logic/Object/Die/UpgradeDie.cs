using OpenSage.Content;
using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

public class UpgradeDieModule : DieModule
{
    private readonly UpgradeDieModuleData _moduleData;

    internal UpgradeDieModule(GameObject gameObject, IGameEngine gameEngine, UpgradeDieModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
        _moduleData = moduleData;
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }

    protected override void Die(in DamageInfoInput damageInput)
    {
        var parent = GameEngine.GameLogic.GetObjectById(GameObject.CreatedByObjectID);

        parent?.RemoveUpgrade(_moduleData.UpgradeToRemove.UpgradeName.Value);
    }
}

// ============================================================================
// PARSE SIDE - immutable flyweight (design-module-api §2.2). Audited: this class has no
// numeric, duration, angle, or vector fields at all, so the S5 quantizing vocabulary
// (ParseFix64 / ParseDurationLogicFrames / ParseAngleDegrees / ParseFixVector3) has nothing
// to convert here - the whole payload is an upgrade reference plus an identifier, and the
// inherited DieLogicData gate (DeathTypes / RequiredStatus / ExemptStatus). Recorded rather
// than assumed: an audit that touches nothing is still an audit.
// ============================================================================

/// <summary>
/// Frees the object-based upgrade for the producer object.
/// </summary>
[SimDataAudited]
public sealed class UpgradeDieModuleData : DieModuleData
{
    internal static UpgradeDieModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<UpgradeDieModuleData> FieldParseTable = DieModuleData.FieldParseTable
        .Concat(new IniParseTable<UpgradeDieModuleData>
        {
            { "UpgradeToRemove", (parser, x) => x.UpgradeToRemove = UpgradeToRemove.Parse(parser) }
        });

    /// <summary>The upgrade freed on the producer at death (plus its unconsumed BFME2 tag).</summary>
    public UpgradeToRemove UpgradeToRemove { get; private set; }

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new UpgradeDieModule(gameObject, gameEngine, this);
    }
}

/// <summary>
/// The <c>UpgradeToRemove</c> payload: an upgrade name, and - BFME2 only - an optional module
/// tag naming the module on the producer that granted it. ZH parses one token
/// (<c>INI::parseAsciiString</c>); AotR writes both forms, six object files with the bare
/// upgrade name and two with a trailing tag.
/// </summary>
public readonly struct UpgradeToRemove
{
    internal static UpgradeToRemove Parse(IniParser parser)
    {
        var upgradeName = parser.ParseUpgradeReference();

        // Optional (the ZH form ends the line here). A required token throws
        // "Expected a token" and takes the whole file down with it.
        var moduleTag = parser.GetNextTokenOptional();

        return new UpgradeToRemove(upgradeName, moduleTag?.Text);
    }

    internal UpgradeToRemove(LazyAssetReference<UpgradeTemplate> upgradeName, string moduleTag)
    {
        UpgradeName = upgradeName;
        ModuleTag = moduleTag;
    }

    public LazyAssetReference<UpgradeTemplate> UpgradeName { get; }

    /// <summary>
    /// BFME2-only second token. Parsed and stored; no GPL reference or Ghidra behavioral spec
    /// says what the original does with it, so nothing acts on it (behavior-fact gap).
    /// Null for the one-token ZH form.
    /// </summary>
    [AddedIn(SageGame.Bfme2)]
    public string ModuleTag { get; }
}
