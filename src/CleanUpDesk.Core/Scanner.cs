using System.Diagnostics;

namespace CleanUpDesk.Core;
public interface ITreeReader
{
    /// <summary>Enumerates immediate child paths for the traversal abstraction or its filesystem implementation.</summary>
    IEnumerable<string> Children(string path);
    /// <summary>Reads one entry's metadata through the traversal abstraction.</summary>
    Entry Read(string path, string volume);
    /// <summary>Enumerates child metadata, retaining isolated read failures so traversal can continue.</summary>
    IEnumerable<DirectoryRefreshItem> ReadChildren(string path, string volume)
    {
        foreach (var child in Children(path))
        {
            Entry? entry;
            try { entry = Read(child, volume); }
            catch (Exception e) when (Scanner.IsReadError(e)) { entry = null; }
            yield return new(child, entry);
        }
    }
}
public sealed class TreeReader : ITreeReader
{
    /// <summary>Enumerates immediate child paths for the traversal abstraction or its filesystem implementation.</summary>
    public IEnumerable<string> Children(string path) => Directory.EnumerateFileSystemEntries(path);
    /// <summary>Reads one entry's metadata through the traversal abstraction.</summary>
    public Entry Read(string path, string volume) => Paths.ReadEntry(path, volume);
    /// <summary>Enumerates child metadata, retaining isolated read failures so traversal can continue.</summary>
    public IEnumerable<DirectoryRefreshItem> ReadChildren(string path, string volume)
    {
        // Windows already returns size, attributes, and timestamps during enumeration.
        // FileSystemInfo caches this metadata, avoiding another query for every path.
        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            Entry? entry;
            try
            {
                var a = info.Attributes; bool dir = a.HasFlag(FileAttributes.Directory);
                entry = new(info.FullName, path, info.Name, dir ? "" : info.Extension.ToUpperInvariant(), dir,
                    dir ? 0 : ((FileInfo)info).Length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, (int)a, volume);
            }
            catch (Exception e) when (Scanner.IsReadError(e)) { entry = null; }
            yield return new(info.FullName, entry);
        }
    }
}
/// <summary>Creates a serialized index scanner with an injectable filesystem reader.</summary>
public sealed class Scanner(IndexStore store, ITreeReader? reader = null)
{
    readonly ITreeReader tree = reader ?? new TreeReader();
    readonly SemaphoreSlim gate = new(1);
    /// <summary>Runs one cancellable recursive scan behind a concurrency gate without blocking the UI.</summary>
    public async Task<ScanReport> ScanAsync(string root, string volume, bool hidden, IEnumerable<string> exclusions, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return await Task.Run(() => Scan(root, volume, hidden, exclusions, progress, token)); } finally { gate.Release(); }
    }
    /// <summary>Rebuilds the selected indexed scope in batches and records real omissions and partial cancellation coverage.</summary>
    ScanReport Scan(string root, string volume, bool hidden, IEnumerable<string> exclusions, IProgress<ScanProgress>? progress, CancellationToken token)
    {
        root = Paths.Normalize(root); Paths.NoReparseAncestors(root); var exclude = exclusions.Select(Paths.Normalize).ToArray();
        if (exclude.Any(e => Paths.Within(root, e))) throw new IOException("La carpeta está excluida del análisis.");
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("La carpeta no está disponible.");
        // Rebuild the scope so cancellation retains only entries that were actually visited.
        store.RemoveTree(root); var watch = Stopwatch.StartNew(); long done = 0, skipped = 0; var batch = new List<Entry>(512); var stack = new Stack<string>(); stack.Push(root); long last = 0;
        // Summary: Counts and records an isolated traversal omission within the bounded scan history.
        void Omit(string path, string reason) { skipped++; if (skipped <= 1000) store.Log(new(path, "Omitido en análisis", reason)); }
        while (stack.Count > 0 && !token.IsCancellationRequested)
        {
            var dir = stack.Pop();
            try
            {
                foreach (var path in tree.Children(dir))
                {
                    if (token.IsCancellationRequested) break;
                    try
                    {
                        if (exclude.Any(e => Paths.Within(path, e))) { Omit(path, "Carpeta excluida; no se visitan sus descendientes."); continue; }
                        var e = tree.Read(path, volume);
                        if (((FileAttributes)e.Attributes).HasFlag(FileAttributes.ReparsePoint) || (!hidden && ((FileAttributes)e.Attributes).HasFlag(FileAttributes.Hidden))) { Omit(path, "Oculto no solicitado o punto de reanálisis; no se visitan sus descendientes."); continue; }
                        batch.Add(e); done++; if (e.IsDirectory) stack.Push(path);
                        if (batch.Count == 512) { store.WriteBatch(batch); batch.Clear(); }
                    }
                    catch (Exception e) when (IsReadError(e)) { Omit(path, e.Message); }
                    if (watch.ElapsedMilliseconds - last >= 150) { progress?.Report(new(done, skipped, dir, watch.Elapsed)); last = watch.ElapsedMilliseconds; }
                }
            }
            catch (Exception e) when (IsReadError(e)) { Omit(dir, e.Message); }
        }
        if (batch.Count > 0) store.WriteBatch(batch);
        var report = new ScanReport(done, skipped, token.IsCancellationRequested, root, watch.Elapsed); store.ScanFinished(report); progress?.Report(new(done, skipped, root, watch.Elapsed)); return report;
    }
    /// <summary>Recognizes isolated filesystem/access exceptions that should not abort a whole scan.</summary>
    public static bool IsReadError(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException or System.ComponentModel.Win32Exception;
    /// <summary>Sums logical file sizes with cancellation and omission reporting without treating them as exact recoverable space.</summary>
    public static Task<(long Size, long Files, long Skipped)> FolderSize(string root, CancellationToken token) => Task.Run(() =>
    {
        Paths.NoReparseAncestors(root); long size = 0, files = 0, skipped = 0; var stack = new Stack<string>(); stack.Push(root);
        while (stack.TryPop(out var dir)) { token.ThrowIfCancellationRequested(); try { foreach (var p in Directory.EnumerateFileSystemEntries(dir)) { token.ThrowIfCancellationRequested(); try { var e = Paths.ReadEntry(p, ""); if (((FileAttributes)e.Attributes).HasFlag(FileAttributes.ReparsePoint)) { skipped++; continue; } if (e.IsDirectory) stack.Push(p); else { size += e.Size; files++; } } catch (Exception e) when (IsReadError(e)) { skipped++; } } } catch (Exception e) when (IsReadError(e)) { skipped++; } } return (size, files, skipped);
    }, token);
}
