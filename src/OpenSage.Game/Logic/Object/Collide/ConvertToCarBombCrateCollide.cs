using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

// R13.5 (crate-gate): the shared CrateCollide::isValidToExecute gate now lives on the
// CrateCollide base and this module inherits it; it has no OnCollide dispatch of its own
// yet, so the gate takes effect the moment the leaf's executeCrateBehavior is wired.
public sealed class ConvertToCarBombCrateCollide : CrateCollide
{
    public ConvertToCarBombCrateCollide(GameObject gameObject, IGameEngine gameEngine, ConvertToCarBombCrateCollideModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        base.Load(reader);
    }
}

/// <summary>
/// Triggers use of CARBOMB WeaponSet Condition of the hijacked object and turns it to a
/// suicide unit unless given with a different weapon.
/// </summary>
public sealed class ConvertToCarBombCrateCollideModuleData : CrateCollideModuleData
{
    internal static ConvertToCarBombCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<ConvertToCarBombCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<ConvertToCarBombCrateCollideModuleData>());

    internal override BehaviorModule CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ConvertToCarBombCrateCollide(gameObject, gameEngine, this);
    }
}
