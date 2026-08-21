using System.Collections.Generic;

namespace OpenSage.Data.Ini;

/// <summary>
/// The body of an INI <c>#define</c> macro: one or more tokens, stored verbatim
/// and expanded at each use site.
/// </summary>
internal sealed class IniMacroDefinition
{
    public IReadOnlyList<IniToken> Tokens { get; }

    public IniMacroDefinition(IReadOnlyList<IniToken> tokens)
    {
        Tokens = tokens;
    }
}
