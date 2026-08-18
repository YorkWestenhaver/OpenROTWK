using OpenSage.Data.Ini;

namespace OpenSage.Logic.Object;

/// <summary>
/// Allows object to open and close like a gate when a friendly object approaches it. Requires 
/// <see cref="ModelConditionFlag.Door1Opening"/> and 
/// <see cref="ModelConditionFlag.Door1Closing"/> condition states.
/// </summary>
[ParseOnly("Round-4 backlog; census: Update")]
public sealed class CheckpointUpdateModuleData : UpdateModuleData
{
    internal static CheckpointUpdateModuleData Parse(IniParser parser) => parser.ParseBlock(FieldParseTable);

    private static readonly IniParseTable<CheckpointUpdateModuleData> FieldParseTable = new IniParseTable<CheckpointUpdateModuleData>();
}
