using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

// R13.5 (crate-gate): the shared CrateCollide::isValidToExecute gate now lives on the
// CrateCollide base and this module inherits it; it has no OnCollide dispatch of its own
// yet, so the gate takes effect the moment the leaf's executeCrateBehavior is wired.
public sealed class ConvertToHijackedVehicleCrateCollide : CrateCollide
{
    public ConvertToHijackedVehicleCrateCollide(GameObject gameObject, IGameEngine gameEngine, ConvertToHijackedVehicleCrateCollideModuleData moduleData)
        : base(gameObject, gameEngine, moduleData)
    {
    }

    internal override void Load(StatePersister reader)
    {
        reader.PersistVersion(1);

        reader.BeginObject("Base");
        base.Load(reader);
        reader.EndObject();
    }
}

/// <summary>
/// Hardcoded to play the HijackDriver sound definition when triggered and converts the unit to
/// your side.
/// </summary>
public sealed class ConvertToHijackedVehicleCrateCollideModuleData : CrateCollideModuleData
{
    internal static ConvertToHijackedVehicleCrateCollideModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static new readonly IniParseTable<ConvertToHijackedVehicleCrateCollideModuleData> FieldParseTable = CrateCollideModuleData.FieldParseTable
        .Concat(new IniParseTable<ConvertToHijackedVehicleCrateCollideModuleData>());

    internal override ConvertToHijackedVehicleCrateCollide CreateModule(GameObject gameObject, IGameEngine gameEngine)
    {
        return new ConvertToHijackedVehicleCrateCollide(gameObject, gameEngine, this);
    }
}
