using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace OpenSage.Diagnostics;

public static class GameTrace
{
    private static ObjectPool<DurationEventDisposable> DurationEvents;
    private static int ProcessId;
    private static StreamWriter Output;
    private static bool WrittenFirstEntry;

    public static void Start(string outputPath)
    {
        DurationEvents = new ObjectPool<DurationEventDisposable>(() => new DurationEventDisposable());
        ProcessId = Process.GetCurrentProcess().Id;
        Output = new StreamWriter(outputPath, false);
        Output.WriteLine("[");
        WrittenFirstEntry = false;
    }

    public static void Stop()
    {
        Output.WriteLine("]");
        Output.Close();

        // Restores the pre-Start state (IsTracing false, no stale closed writer around to
        // throw ObjectDisposedException out of a post-Stop WriteEvent call). Previously
        // harmless because the one production caller (the launcher) calls Stop() once at
        // process exit and never again; SimHeartbeatTests starts and stops a session per test.
        Output = null;
    }

    /// <summary>
    /// True once <see cref="Start"/> has been called and before <see cref="Stop"/>. Lets a
    /// caller skip formatting a trace payload (e.g. the periodic sim heartbeat) when no trace
    /// session is active, rather than relying on <see cref="TraceInstantEvent"/>'s internal
    /// no-op to absorb the cost of building the string.
    /// </summary>
    public static bool IsTracing => Output != null;

    public static IDisposable TraceDurationEvent(string name)
    {
        if (Output == null)
        {
            return null;
        }

        WriteEvent("B", name);

        return DurationEvents.Rent();
    }

    private static void WriteEvent(
        string type,
        string name)
    {
        if (Output == null)
        {
            return;
        }

        var cat = "-"; // TODO
        var timestamp = Stopwatch.GetTimestamp();
        var threadId = Thread.CurrentThread.ManagedThreadId;

        name = name.Replace("\\", "\\\\");

        if (WrittenFirstEntry)
        {
            Output.Write(",");
        }

        Output.WriteLine($"{{\"name\": \"{name}\", \"cat\": \"{cat}\", \"ph\": \"{type}\", \"ts\": {timestamp}, \"pid\": {ProcessId}, \"tid\": {threadId}, \"s\": \"g\"}}");

        WrittenFirstEntry = true;
    }

    private sealed class DurationEventDisposable : IDisposable
    {
        public void Dispose()
        {
            WriteEvent("E", string.Empty);
        }
    }

    public static void TraceInstantEvent(string name)
    {
        WriteEvent("i", name);
    }
}

internal sealed class ObjectPool<T>
{
    private readonly ConcurrentBag<T> _objects;
    private readonly Func<T> _objectGenerator;

    public ObjectPool(Func<T> objectGenerator)
    {
        _objects = new ConcurrentBag<T>();
        _objectGenerator = objectGenerator ?? throw new ArgumentNullException(nameof(objectGenerator));
    }

    public T Rent()
    {
        if (_objects.TryTake(out var item))
        {
            return item;
        }
        return _objectGenerator();
    }

    public void Release(T item)
    {
        _objects.Add(item);
    }
}
