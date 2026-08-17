using System.Collections.Generic;

namespace OpenSage.Data.Ini;

public sealed class IniDataContext
{
    internal Dictionary<string, IniMacroDefinition> Defines { get; } = new Dictionary<string, IniMacroDefinition>();
}
