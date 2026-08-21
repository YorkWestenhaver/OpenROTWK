using System;

namespace OpenSage.Data.Ini;

/// <summary>
/// A parse error that was contained by <see cref="IniParser.ParseFile"/>:
/// the enclosing top-level block was skipped and parsing continued with
/// the next block instead of aborting the whole file.
/// </summary>
internal sealed class IniParseError
{
    public Exception Exception { get; }
    public IniTokenPosition Position { get; }

    public string Message => Exception.Message;

    public IniParseError(Exception exception, in IniTokenPosition position)
    {
        Exception = exception;
        Position = position;
    }
}
