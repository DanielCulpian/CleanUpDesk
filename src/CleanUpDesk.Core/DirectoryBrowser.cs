namespace CleanUpDesk.Core;

/// <summary>Describes the actual coverage and omissions of one immediate-directory snapshot.</summary>
public sealed record BrowseReport(string Path, long Count, long Skipped);

/// <summary>A single-level snapshot independent of the recursive scan index.</summary>
public sealed class DirectoryBrowser(IndexStore cache, ITreeReader? reader = null)
{
    readonly ITreeReader tree = reader ?? new TreeReader();
    readonly SemaphoreSlim gate = new(1);
    string current = "";
    /// <summary>Reads one directory into an independent transactional browsing cache with exclusions and cancellation.</summary>
    public async Task<BrowseReport> LoadAsync(string path, string volume, bool includeHidden, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                path = Paths.Normalize(path); Paths.NoReparseAncestors(path);
                cache.ClearEntries(); current = "";
                long count = 0, skipped = 0; var batch = new List<Entry>(512);
                foreach (var p in tree.Children(path))
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        var entry = tree.Read(p, volume); var a = (FileAttributes)entry.Attributes;
                        if (a.HasFlag(FileAttributes.ReparsePoint) || (!includeHidden && a.HasFlag(FileAttributes.Hidden))) { skipped++; continue; }
                        batch.Add(entry); count++;
                        if (batch.Count == 512) { cache.WriteBatch(batch); batch.Clear(); }
                    }
                    catch (Exception e) when (Scanner.IsReadError(e)) { skipped++; }
                }
                token.ThrowIfCancellationRequested(); if (batch.Count > 0) cache.WriteBatch(batch);
                current = path; return new BrowseReport(path, count, skipped);
            }, token);
        }
        finally { gate.Release(); }
    }
    /// <summary>Queries a bounded sorted page of the currently browsed directory.</summary>
    public SearchPage Page(int page = 0, string name = "", string sort = "name", bool descending = false) => current == "" ? new([], 0) : cache.Search(new() { Folder = current, Recursive = false, Name = name }, sort, descending, page, 200, true);
    /// <summary>Clears the browsing snapshot without deleting original files.</summary>
    public void Reset() { current = ""; cache.ClearEntries(); }
}
