using System;

namespace OpenSage.Data.Ini;

internal sealed class IniParseException : Exception
{
    public IniTokenPosition Position { get; }

    public IniParseException(string message, in IniTokenPosition position)
        : base($"({position}): {message}")
    {
        Position = position;
    }
}
