using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace CleanUpDesk.App;

public interface ICompletionAudioBackend : IDisposable
{
    /// <summary>Attempts one fresh notification without queuing late playback; the backend determines whether hardware is used.</summary>
    void Play();
    /// <summary>Stops current backend playback without scheduling a replacement sound.</summary>
    void Stop();
}

/// <summary>One preloaded native voice, with no managed queue or retries for expired notifications.</summary>
public sealed class CompletionSound : IDisposable
{
    static readonly System.Collections.Concurrent.ConcurrentDictionary<CompletionSound, byte> live = new();
    static int nativePlaybackAttempts;
    public static int NativePlaybackAttempts => Volatile.Read(ref nativePlaybackAttempts);
    /// <summary>Creates a no-device player for isolated UI tests.</summary>
    public static CompletionSound Silent() => new(new SilentAudio());
    /// <summary>Registers process-exit cleanup before any player can be created.</summary>
    static CompletionSound() => AppDomain.CurrentDomain.ProcessExit += (_, _) => StopAll();
    /// <summary>Disables and stops every currently registered player, including exceptional shutdown paths.</summary>
    public static void StopAll() { foreach (var player in live.Keys) player.Dispose(); }
    readonly object gate = new();
    readonly ICompletionAudioBackend backend;
    public bool IsSilent => backend is SilentAudio;
    readonly Func<long> clock;
    int disposed;
    /// <summary>Initializes the native or injected audio backend and registers it for process-wide shutdown.</summary>
    public CompletionSound() : this(new NativeAudio()) { }
    /// <summary>Initializes the native or injected audio backend and registers it for process-wide shutdown.</summary>
    public CompletionSound(ICompletionAudioBackend backend, Func<long>? clock = null) { this.backend = backend; this.clock = clock ?? Stopwatch.GetTimestamp; live.TryAdd(this, 0); }
    /// <summary>Attempts one fresh notification without queuing late playback; the backend determines whether hardware is used.</summary>
    public void Play(long completedAt)
    {
        lock (gate)
        {
            if (Volatile.Read(ref disposed) != 0 || Stopwatch.GetElapsedTime(completedAt, clock()) > TimeSpan.FromSeconds(1)) return;
            try { backend.Play(); AudioDiagnostics.Record("audio-request backend=" + backend.GetType().Name); } catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { AudioDiagnostics.Record("audio-error " + e.GetType().Name); }
        }
    }
    /// <summary>Permanently disables the player before stopping its backend and releasing resources.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lock (gate) { try { backend.Stop(); } finally { backend.Dispose(); } }
        live.TryRemove(this, out _); AudioDiagnostics.Record("audio-disabled");
    }
    sealed class SilentAudio : ICompletionAudioBackend
    {
        /// <summary>Accepts a test notification without accessing an audio device.</summary>
        public void Play() { }
        /// <summary>Provides the silent backend's no-op stop operation.</summary>
        public void Stop() { }
        /// <summary>Completes disposal without native resources to release.</summary>
        public void Dispose() { }
    }
    sealed class NativeAudio : ICompletionAudioBackend
    {
        /// <summary>Calls the Windows waveform API with explicit memory, asynchronous, no-fallback, and no-queue flags.</summary>
        [DllImport("winmm.dll", EntryPoint = "PlaySoundW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
        GCHandle pinned;
        /// <summary>Preloads and pins the bundled notification WAV so playback does not wait for file loading.</summary>
        public NativeAudio()
        {
            try
            {
                using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/CleanUpDesk.App;component/Assets/SearchComplete.wav"))!.Stream;
                using var memory = new MemoryStream(); stream.CopyTo(memory); pinned = GCHandle.Alloc(memory.ToArray(), GCHandleType.Pinned);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
        }
        /// <summary>Attempts one fresh notification without queuing late playback; the backend determines whether hardware is used.</summary>
        public void Play()
        {
            Interlocked.Increment(ref nativePlaybackAttempts);
            // ASYNC | MEMORY | NODEFAULT | NOSTOP: discard when the voice is busy; never queue.
            if (pinned.IsAllocated) PlaySound(pinned.AddrOfPinnedObject(), IntPtr.Zero, 0x1 | 0x4 | 0x2 | 0x10);
        }
        /// <summary>Stops current backend playback without scheduling a replacement sound.</summary>
        public void Stop() => PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
        /// <summary>Permanently disables the player before stopping its backend and releasing resources.</summary>
        public void Dispose() { if (pinned.IsAllocated) pinned.Free(); }
    }
}

/// <summary>Signals completion from the worker without waiting for the Dispatcher.</summary>
public sealed class CompletionSignal(Action<long> notify, Func<bool> allowed)
{
    int attempted;
    /// <summary>Observes successful task completion off the Dispatcher and emits at most one eligible notification.</summary>
    public Task<T> Watch<T>(Func<Task<T>> work, CancellationToken token, Func<T, bool>? success = null) => Task.Run(async () =>
    {
        var result = await work().ConfigureAwait(false); Notify(result, token, success); return result;
    }, token);
    /// <summary>Executes synchronous work on a worker and emits its notification before returning to the UI.</summary>
    public Task<T> Run<T>(Func<T> work, CancellationToken token) => Task.Run(() => { var result = work(); Notify(result, token, null); return result; }, token);
    /// <summary>Observes successful task completion off the Dispatcher and emits at most one eligible notification.</summary>
    public Task<T> Watch<T>(Task<T> work, CancellationToken token, Func<T, bool>? success = null) => work.ContinueWith(completed =>
    {
        var result = completed.GetAwaiter().GetResult(); Notify(result, token, success);
        return result;
    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    /// <summary>Emits one timestamped notification only for a successful, uncancelled, currently allowed operation.</summary>
    void Notify<T>(T result, CancellationToken token, Func<T, bool>? success)
    {
        var stamp = Stopwatch.GetTimestamp();
        if (!token.IsCancellationRequested && (success == null || success(result)) && Interlocked.Exchange(ref attempted, 1) == 0 && allowed()) notify(stamp);
    }
}
