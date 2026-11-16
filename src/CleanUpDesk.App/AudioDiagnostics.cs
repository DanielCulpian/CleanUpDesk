using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace CleanUpDesk.App;
public static class AppBuild
{
    public static string Version => typeof(AppBuild).Assembly.GetName().Version?.ToString(3) ?? "desconocida";
    public static string Title => "CleanUpDesk";
}

/// <summary>Bounded local diagnostics; the audio path only queues text and never waits for disk writes.</summary>
public static class AudioDiagnostics
{
    static readonly ConcurrentQueue<(string Path, string Line)> pending = new();
    static readonly ManualResetEventSlim drained = new(true);
    static string? destination;
    static int pumping;
    static long operation;
    /// <summary>Allocates a process-local identifier for tracing an explicitly started operation.</summary>
    public static long NextOperation() => Interlocked.Increment(ref operation);
    /// <summary>Enables bounded local diagnostics and records the actual loaded assembly identity.</summary>
    public static void Start(string folder)
    {
        Volatile.Write(ref destination, Path.Combine(folder, "audio-diagnostics.log"));
        Record($"start version={AppBuild.Version} module={typeof(AppBuild).Module.ModuleVersionId} assembly={typeof(AppBuild).Assembly.Location}");
    }
    /// <summary>Enqueues a short diagnostic without waiting for filesystem writes on the audio path.</summary>
    public static void Record(string message)
    {
        var path = Volatile.Read(ref destination); if (path == null) return;
        while (pending.Count >= 128) pending.TryDequeue(out _);
        pending.Enqueue((path, $"{DateTimeOffset.Now:O} pid={Environment.ProcessId} {message}"));
        Schedule();
    }
    /// <summary>Starts at most one background diagnostic drain at a time.</summary>
    static void Schedule()
    {
        if (Interlocked.CompareExchange(ref pumping, 1, 0) != 0) return;
        drained.Reset(); ThreadPool.QueueUserWorkItem(_ => Drain());
    }
    /// <summary>Writes queued diagnostics with a bounded log size while tolerating unavailable storage.</summary>
    static void Drain()
    {
        try
        {
            while (pending.TryDequeue(out var record))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(record.Path)!);
                    var line = record.Line[..Math.Min(record.Line.Length, 2000)] + Environment.NewLine;
                    if (File.Exists(record.Path) && new FileInfo(record.Path).Length + Encoding.UTF8.GetByteCount(line) > 65_536) File.WriteAllText(record.Path, "", Encoding.UTF8);
                    File.AppendAllText(record.Path, line, Encoding.UTF8);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        finally { Volatile.Write(ref pumping, 0); drained.Set(); if (!pending.IsEmpty) Schedule(); }
    }
    /// <summary>Waits up to the supplied timeout for the bounded diagnostic queue to drain.</summary>
    public static bool Flush(TimeSpan timeout)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        do
        {
            var remaining = timeout - watch.Elapsed;
            if (!drained.Wait(remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero)) return false;
            if (pending.IsEmpty && Volatile.Read(ref pumping) == 0) return true;
            Thread.Yield();
        } while (watch.Elapsed < timeout);
        return false;
    }
    /// <summary>Records application exit, rejects new diagnostics, and makes a short best-effort flush.</summary>
    public static void Stop()
    {
        Record("application-exit"); Volatile.Write(ref destination, null); Flush(TimeSpan.FromMilliseconds(200));
    }
}
