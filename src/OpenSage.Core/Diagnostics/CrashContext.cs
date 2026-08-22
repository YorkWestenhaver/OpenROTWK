#nullable enable

// OBS-2 (R15 R1-FIX2): ambient crash context.
//
// WHY: every crash the R1 sweep produced was anonymous. The stack named a *method*
// (DozerAndWorkerState.cs:70, HordeContainBehavior.Unpack, Locomotor ctor) but never the
// *subject* - which object, which template, which logic frame, which map object being
// loaded. Triage then costs a bisect run per crash.
//
// The pattern already proven in this codebase is OnDemandTextureLoader.cs's catch block,
// which can name the failing asset because the loader happens to hold `entry.FilePath` in
// a local. CrashContext generalizes that: hot paths push the identity they already hold
// onto a thread-local stack, and the unhandled-exception handler formats it.
//
// COST DISCIPLINE (the reason this is a struct-array and not a Stack<string>): pushes sit
// inside the per-frame per-object sim loop, so a push must not allocate. Entries store a
// string key (always a literal), plus EITHER a string value the caller already holds OR a
// long - never a boxed value, never an interpolated string. Formatting happens exactly
// once, in the crash handler. On the happy path a scope is two array writes.
//
// THREADING: the stack is [ThreadStatic]. AppDomain.CurrentDomain.UnhandledException runs
// on the faulting thread, and a try/catch at the loop site is on the faulting thread by
// construction, so Describe() sees the right stack in both crash paths. A handler running
// on some *other* thread sees an empty context, which formats as "(no context)" rather
// than lying.

using System;
using System.Text;

namespace OpenSage.Diagnostics;

/// <summary>
/// A thread-local stack of cheap key/value pairs describing what the engine is currently
/// working on, formatted only when a crash handler asks for it.
/// </summary>
public static class CrashContext
{
    /// <summary>The machine-readable marker the harness greps for. One line, one crash.</summary>
    public const string LineMarker = "CRASH-CONTEXT-V1";

    private const int MaxDepth = 32;

    private struct Entry
    {
        public string Key;
        public string? Text;
        public long Number;
        public bool HasNumber;
    }

    [ThreadStatic]
    private static Entry[]? _stack;

    [ThreadStatic]
    private static int _depth;

    /// <summary>Current nesting depth on this thread. Exposed for tests.</summary>
    public static int Depth => _depth;

    /// <summary>
    /// Pushes a string-valued frame. <paramref name="value"/> must be a string the caller
    /// already holds - do NOT interpolate one here, that would allocate per sim tick.
    /// </summary>
    public static Scope Push(string key, string? value)
    {
        var stack = EnsureStack();
        var index = _depth;
        if (index < MaxDepth)
        {
            stack[index].Key = key;
            stack[index].Text = value;
            stack[index].Number = 0;
            stack[index].HasNumber = false;
        }

        _depth = index + 1;
        return new Scope(index);
    }

    /// <summary>Pushes a numeric frame (frame counter, object id) with no boxing.</summary>
    public static Scope Push(string key, long value)
    {
        var stack = EnsureStack();
        var index = _depth;
        if (index < MaxDepth)
        {
            stack[index].Key = key;
            stack[index].Text = null;
            stack[index].Number = value;
            stack[index].HasNumber = true;
        }

        _depth = index + 1;
        return new Scope(index);
    }

    /// <summary>
    /// Pushes a frame carrying both a name and an id, the shape nearly every sim-side call
    /// site wants ("object #123 GondorFighter").
    /// </summary>
    public static Scope Push(string key, string? name, long id)
    {
        var stack = EnsureStack();
        var index = _depth;
        if (index < MaxDepth)
        {
            stack[index].Key = key;
            stack[index].Text = name;
            stack[index].Number = id;
            stack[index].HasNumber = true;
        }

        _depth = index + 1;
        return new Scope(index);
    }

    private static Entry[] EnsureStack() => _stack ??= new Entry[MaxDepth];

    private static void PopTo(int index)
    {
        // Unbalanced disposal (an exception unwinding past a scope whose Dispose already
        // ran) must never push the depth back up.
        if (index < _depth)
        {
            _depth = index;
        }
    }

    /// <summary>
    /// Clears the calling thread's context. Only for tests and for a crash handler that has
    /// finished formatting; normal code relies on <see cref="Scope"/> disposal.
    /// </summary>
    public static void Reset() => _depth = 0;

    /// <summary>
    /// Human-readable one-liner, outermost frame first:
    /// <c>frame=127 | object=#48 GondorWorker | module=DozerAndWorkerState</c>.
    /// Returns <c>"(no context)"</c> when nothing is pushed.
    /// </summary>
    public static string Describe()
    {
        var stack = _stack;
        var depth = Math.Min(_depth, MaxDepth);
        if (stack == null || depth <= 0)
        {
            return "(no context)";
        }

        var sb = new StringBuilder();
        for (var i = 0; i < depth; i++)
        {
            if (i > 0)
            {
                sb.Append(" | ");
            }

            sb.Append(stack[i].Key).Append('=');
            AppendValue(sb, stack[i]);
        }

        return sb.ToString();
    }

    private static void AppendValue(StringBuilder sb, in Entry entry)
    {
        if (entry.HasNumber && entry.Text != null)
        {
            sb.Append('#').Append(entry.Number).Append(' ').Append(entry.Text);
        }
        else if (entry.HasNumber)
        {
            sb.Append(entry.Number);
        }
        else
        {
            sb.Append(entry.Text ?? "(null)");
        }
    }

    /// <summary>
    /// The single machine-readable crash line: <c>CRASH-CONTEXT-V1 {json}</c>, newline-free
    /// so a log grep yields exactly one record per crash. The managed stack is embedded as a
    /// JSON string (escaped), because the wrapper log and the NLog file interleave lines from
    /// several sources and a multi-line stack cannot be attributed reliably.
    /// </summary>
    public static string FormatCrashLine(Exception? exception, string? phase = null)
    {
        var sb = new StringBuilder();
        sb.Append(LineMarker).Append(' ').Append('{');

        AppendJsonString(sb, "phase");
        sb.Append(':');
        AppendJsonString(sb, phase ?? "unknown");

        sb.Append(",\"exceptionType\":");
        AppendJsonString(sb, exception?.GetType().FullName ?? "(none)");

        sb.Append(",\"message\":");
        AppendJsonString(sb, exception?.Message ?? string.Empty);

        sb.Append(",\"context\":");
        AppendJsonString(sb, Describe());

        sb.Append(",\"frames\":[");
        AppendFramesArray(sb);
        sb.Append(']');

        sb.Append(",\"stack\":");
        AppendJsonString(sb, exception?.ToString() ?? string.Empty);

        sb.Append('}');
        return sb.ToString();
    }

    private static void AppendFramesArray(StringBuilder sb)
    {
        var stack = _stack;
        var depth = Math.Min(_depth, MaxDepth);
        if (stack == null)
        {
            return;
        }

        for (var i = 0; i < depth; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append('{');
            AppendJsonString(sb, "key");
            sb.Append(':');
            AppendJsonString(sb, stack[i].Key);
            sb.Append(",\"value\":");
            var valueBuilder = new StringBuilder();
            AppendValue(valueBuilder, stack[i]);
            AppendJsonString(sb, valueBuilder.ToString());
            sb.Append('}');
        }
    }

    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4"));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>
    /// The disposable returned by <see cref="Push(string, string?)"/>. A readonly struct, so
    /// <c>using var scope = CrashContext.Push(...)</c> in a hot loop boxes nothing.
    /// </summary>
    public readonly struct Scope : IDisposable
    {
        private readonly int _index;

        internal Scope(int index)
        {
            _index = index;
        }

        public void Dispose() => PopTo(_index);
    }
}
