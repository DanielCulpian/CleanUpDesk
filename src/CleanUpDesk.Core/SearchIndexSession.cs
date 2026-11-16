using System.Collections.Concurrent;

namespace CleanUpDesk.Core;
/// <summary>Describes a changed path, optional old path, and whether its subtree needs reconciliation.</summary>
public sealed record SearchChange(string Path, bool Subtree = false, string? OldPath = null);
public interface ISearchChangeFeed : IDisposable
{
    event Action<SearchChange>? Changed;
    event Action? Interrupted;
}
public sealed class FileSystemChangeFeed : ISearchChangeFeed
{
    readonly FileSystemWatcher watcher;
    public event Action<SearchChange>? Changed;
    public event Action? Interrupted;
    /// <summary>Creates a directory change feed that also reports watcher interruptions.</summary>
    public FileSystemChangeFeed(string path, bool recursive)
    {
        watcher = new(path) { IncludeSubdirectories = recursive, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes, InternalBufferSize = 65536 };
        watcher.Created += (_, e) => Changed?.Invoke(new(e.FullPath, true));
        watcher.Changed += (_, e) => Changed?.Invoke(new(e.FullPath));
        watcher.Deleted += (_, e) => Changed?.Invoke(new(e.FullPath));
        watcher.Renamed += (_, e) => Changed?.Invoke(new(e.FullPath, true, e.OldFullPath));
        watcher.Error += (_, _) => Interrupted?.Invoke(); watcher.EnableRaisingEvents = true;
    }
    /// <summary>Releases the watcher or scope-owned feed so no further change events remain active.</summary>
    public void Dispose() => watcher.Dispose();
}

/// <summary>Prepares complete scopes and reconciles changes before querying SQLite.</summary>
public sealed class SearchIndexSession(IndexStore store, ITreeReader? reader = null, Func<string, bool, ISearchChangeFeed>? feeds = null) : IDisposable
{
    readonly SearchIndexRefresher refresher = new(store, reader);
    readonly Func<string, bool, ISearchChangeFeed> feedFactory = feeds ?? ((p, r) => new FileSystemChangeFeed(p, r));
    readonly Dictionary<SearchRefreshRoot, State> sessions = [];
    readonly SemaphoreSlim gate = new(1);
    string policy = "";
    sealed class Work { public long Done; public long Pending = 1; public long? Previous; public bool Finished; }
    /// <summary>Creates per-scope watcher and dirty-path state with the active exclusion policy.</summary>
    sealed class State(SearchRefreshRoot root, string[] exclusions) : IDisposable
    {
        public ISearchChangeFeed? Feed;
        public int Baseline = 1, Interrupted;
        public readonly ConcurrentDictionary<string, bool> Dirty = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Marks affected parent directories or subtrees dirty and requests a baseline when the dirty set grows too large.</summary>
        public void Change(SearchChange change)
        {
            // Summary: Adds an in-scope, non-excluded dirty path while preserving whether subtree reconciliation is required.
            void Add(string? path, bool tree = false)
            {
                if (path == null || !Paths.Within(path, root.Path) || exclusions.Any(e => Paths.Within(path, e))) return;
                if (!root.Recursive && !path.Equals(root.Path, StringComparison.OrdinalIgnoreCase)) return;
                Dirty.AddOrUpdate(path, tree, (_, previous) => previous || tree);
            }
            Add(Path.GetDirectoryName(change.Path)); Add(Path.GetDirectoryName(change.OldPath ?? change.Path));
            if (change.Subtree && root.Recursive) Add(change.Path, true);
            if (Dirty.Count > 10000) { Interlocked.Exchange(ref Baseline, 1); Dirty.Clear(); }
        }
        /// <summary>Releases the watcher or scope-owned feed so no further change events remain active.</summary>
        public void Dispose() => Feed?.Dispose();
    }
    /// <summary>Seeds each selected scope fairly and reconciles dirty or interrupted traversals with limited concurrency.</summary>
    public async Task<IReadOnlyList<SearchRefreshReport>> RefreshAsync(IEnumerable<SearchRefreshRoot> roots, bool hidden, IEnumerable<string> exclusions, CancellationToken token, IProgress<string>? progress = null, IProgress<SearchPreparationProgress>? workProgress = null)
    {
        await gate.WaitAsync(token);
        try
        {
            var excluded = exclusions.Select(Paths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            string nextPolicy = hidden + "\0" + string.Join('\0', excluded);
            if (policy != nextPolicy) { Reset(); policy = nextPolicy; }
            string policyKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(policy)));
            var ordered = SearchLocations.Collapse(roots); var planned = ordered.ToHashSet();
            foreach (var key in sessions.Keys.Where(k => !planned.Contains(k)).ToArray()) { sessions[key].Dispose(); sessions.Remove(key); }
            var reports = new ConcurrentDictionary<SearchRefreshRoot, SearchRefreshReport>();
            var work = ordered.ToDictionary(r => r, _ => new Work()); var baselines = new Dictionary<SearchRefreshRoot, bool>();
            var successfulBaselines = new ConcurrentDictionary<SearchRefreshRoot, long>();
            var seeds = new ConcurrentDictionary<SearchRefreshRoot, SearchRefreshReport>();
            var clock = System.Diagnostics.Stopwatch.StartNew(); long lastProgress = -150; object progressLock = new();
            // Summary: Throttles synchronized progress snapshots across concurrent root traversals.
            void Publish(string path, bool force = false)
            {
                SearchPreparationProgress sample;
                lock (progressLock)
                {
                    if (!force && clock.ElapsedMilliseconds - lastProgress < 150) return;
                    lastProgress = clock.ElapsedMilliseconds;
                    var remaining = work.Values.Where(w => !w.Finished).ToArray();
                    long? historical = remaining.All(w => w.Previous.HasValue) ? remaining.Sum(w => Math.Max(w.Pending, w.Previous!.Value - w.Done)) : null;
                    sample = new(path, work.Values.Sum(w => w.Done), remaining.Sum(w => w.Pending), historical);
                }
                progress?.Report(path); workProgress?.Report(sample);
            }
            foreach (var root in ordered)
            {
                token.ThrowIfCancellationRequested();
                if (excluded.Any(e => Paths.Within(root.Path, e)))
                {
                    reports[root] = new(root.Path, 0, 0, false, "Ubicación excluida."); work[root].Finished = true; work[root].Pending = 0; continue;
                }
                if (!sessions.TryGetValue(root, out var state)) { state = new(root, excluded); sessions.Add(root, state); }
                if (Interlocked.Exchange(ref state.Interrupted, 0) != 0) { state.Feed?.Dispose(); state.Feed = null; }
                if (state.Feed == null)
                {
                    try
                    {
                        Paths.NoReparseAncestors(root.Path); state.Feed = feedFactory(root.Path, root.Recursive); state.Feed.Changed += state.Change;
                        state.Feed.Interrupted += () => { Interlocked.Exchange(ref state.Baseline, 1); Interlocked.Exchange(ref state.Interrupted, 1); };
                    }
                    catch (Exception e) when (Scanner.IsReadError(e) || e is ArgumentException) { }
                }
                bool baseline = Interlocked.Exchange(ref state.Baseline, 0) != 0 || state.Feed == null; baselines[root] = baseline;
                work[root].Previous = baseline ? await Task.Run(() => store.PreviousSearchWork(root, policyKey), token) : state.Dirty.IsEmpty ? 0 : null;
                // Review each volume root first so a large volume cannot hide files on another volume.
                if (baseline || state.Dirty.ContainsKey(root.Path))
                {
                    try
                    {
                        Publish(root.Path, true);
                        var seed = (await refresher.RefreshAsync([root], hidden, excluded, token)).Single();
                        seeds[root] = seed;
                    }
                    catch { Interlocked.Exchange(ref state.Baseline, 1); throw; }
                }
                else if (state.Dirty.IsEmpty) work[root].Pending = 0;
            }
            using var workers = new SemaphoreSlim(2);
            // Summary: Reconciles one directory and reports its actual work and omissions.
            async Task Prepare(SearchRefreshRoot root)
            {
                if (reports.ContainsKey(root)) return;
                await workers.WaitAsync(token);
                var state = sessions[root]; long updated = 0, skipped = 0; int errors = 0; bool rootOk = true;
                var failedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // Summary: Traverses one planned scope incrementally, checking cancellation and respecting configured recursion.
                async Task Walk(string path, bool recursive)
                {
                    var pending = new Stack<string>(); pending.Push(path);
                    while (pending.TryPop(out var dir))
                    {
                        token.ThrowIfCancellationRequested(); lock (progressLock) work[root].Pending = pending.Count + 1;
                        Publish(dir); state.Dirty.TryRemove(dir, out _);
                        var report = dir.Equals(root.Path, StringComparison.OrdinalIgnoreCase) && seeds.TryRemove(root, out var seed)
                            ? seed : (await refresher.RefreshAsync([new(dir, root.VolumeId)], hidden, excluded, token)).Single();
                        updated += report.Updated; skipped += report.Skipped;
                        lock (progressLock) work[root].Done++;
                        if (!report.Completed) { errors++; if (dir.Equals(root.Path, StringComparison.OrdinalIgnoreCase)) rootOk = false; else failedDirectories.Add(dir); continue; }
                        if (recursive)
                            for (string after = ""; ; )
                            {
                                var children = await Task.Run(() => store.ChildDirectories(dir, root.VolumeId, after), token);
                                foreach (var child in children) if (!excluded.Any(e => Paths.Within(child, e))) pending.Push(child);
                                if (children.Count < 500) break;
                                after = children[^1];
                            }
                        lock (progressLock) work[root].Pending = pending.Count;
                        Publish(dir);
                    }
                }
                try
                {
                    if (baselines[root]) await Walk(root.Path, root.Recursive);
                    for (int round = 0; round < 3 && !state.Dirty.IsEmpty; round++)
                        foreach (var pair in state.Dirty.ToArray())
                        {
                            token.ThrowIfCancellationRequested(); if (!state.Dirty.TryRemove(pair.Key, out var subtree)) continue;
                            if (Directory.Exists(pair.Key)) await Walk(pair.Key, subtree && root.Recursive);
                            else if (pair.Key.Equals(root.Path, StringComparison.OrdinalIgnoreCase)) { rootOk = false; Interlocked.Exchange(ref state.Baseline, 1); }
                        }
                    if (!rootOk) Interlocked.Exchange(ref state.Baseline, 1);
                    foreach (var dir in failedDirectories) state.Dirty.TryAdd(dir, root.Recursive);
                    string detail = baselines[root] ? root.Recursive ? "Ubicación y subcarpetas reconciliadas." : "Nivel actual reconciliado." : "Índice reutilizado; cambios detectados reconciliados.";
                    if (errors > 0) detail += $" {errors} carpetas omitidas; se reintentará su reconciliación.";
                    if (!state.Dirty.IsEmpty || Volatile.Read(ref state.Baseline) != 0) detail += " Hay reconciliación pendiente; la vista puede estar incompleta.";
                    if (state.Feed == null) detail += " Vigilancia no disponible; se revisará de nuevo al buscar.";
                    if (baselines[root] && rootOk && Volatile.Read(ref state.Baseline) == 0) successfulBaselines[root] = work[root].Done;
                    reports[root] = new(root.Path, updated, skipped, rootOk, detail);
                    lock (progressLock) { work[root].Finished = true; work[root].Pending = 0; }
                    Publish(root.Path, true);
                }
                catch { Interlocked.Exchange(ref state.Baseline, 1); throw; }
                finally { workers.Release(); }
            }
            try { await Task.WhenAll(ordered.Select(Prepare)); }
            catch
            {
                foreach (var root in baselines.Keys) Interlocked.Exchange(ref sessions[root].Baseline, 1);
                throw;
            }
            // Persist statistics only after completion to avoid competing with an active folder write.
            foreach (var item in successfulBaselines) { token.ThrowIfCancellationRequested(); await Task.Run(() => store.RememberSearchWork(item.Key, policyKey, item.Value), token); }
            return ordered.Select(r => reports[r]).ToArray();
        }
        catch { foreach (var state in sessions.Values) Interlocked.Exchange(ref state.Baseline, 1); throw; }
        finally { gate.Release(); }
    }
    /// <summary>Disposes scope watchers and forgets pending in-memory reconciliation state.</summary>
    public void Reset() { foreach (var state in sessions.Values) state.Dispose(); sessions.Clear(); }
    /// <summary>Releases the watcher or scope-owned feed so no further change events remain active.</summary>
    public void Dispose() => Reset();
}
