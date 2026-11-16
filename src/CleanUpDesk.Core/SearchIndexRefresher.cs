namespace CleanUpDesk.Core;

/// <summary>Reconciles single-level metadata before an explicit search.</summary>
public sealed class SearchIndexRefresher(IndexStore store, ITreeReader? reader = null)
{
    readonly ITreeReader tree = reader ?? new TreeReader();
    readonly SemaphoreSlim gate = new(1);

    /// <summary>Returns known personal search locations retained by this compatibility entry point.</summary>
    public static IReadOnlyList<string> DesktopLocations() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
    }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>Reconciles requested directory snapshots without treating an ancestor snapshot as recursive coverage.</summary>
    public async Task<IReadOnlyList<SearchRefreshReport>> RefreshAsync(IEnumerable<SearchRefreshRoot> roots, bool includeHidden, IEnumerable<string> exclusions, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            return await Task.Run<IReadOnlyList<SearchRefreshReport>>(() =>
            {
                var reports = new List<SearchRefreshReport>();
                var excluded = exclusions.Select(Paths.Normalize).ToArray();
                // These snapshots are nonrecursive: a containing root does not replace its child scopes.
                foreach (var root in roots.DistinctBy(r => Paths.Normalize(r.Path), StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested(); var path = Paths.Normalize(root.Path); long updated = 0, skipped = 0;
                    if (excluded.Any(p => Paths.Within(path, p))) { reports.Add(new(path, 0, 0, false, "Carpeta excluida de la actualización.")); continue; }
                    try
                    {
                        Paths.NoReparseAncestors(path);
                        // Summary: Reads metadata and marks folders that must retain their indexed descendants.
                        IEnumerable<DirectoryRefreshItem> Read()
                        {
                            foreach (var item in tree.ReadChildren(path, root.VolumeId))
                            {
                                var p = item.Path;
                                token.ThrowIfCancellationRequested();
                                if (excluded.Any(e => Paths.Within(p, e))) { skipped++; continue; }
                                Entry? entry = item.Entry;
                                if (entry == null) skipped++;
                                if (entry != null)
                                {
                                    var a = (FileAttributes)entry.Attributes;
                                    if (a.HasFlag(FileAttributes.ReparsePoint) || (!includeHidden && a.HasFlag(FileAttributes.Hidden))) { skipped++; continue; }
                                    updated++;
                                }
                                yield return new(p, entry);
                            }
                        }
                        store.RefreshDirectory(path, root.VolumeId, Read(), token);
                        reports.Add(new(path, updated, skipped, true, "Nivel principal actualizado. Los descendientes conservan su último análisis."));
                    }
                    catch (Exception e) when (Scanner.IsReadError(e)) { reports.Add(new(path, 0, skipped, false, "No se pudo actualizar: " + e.Message)); }
                }
                return reports.AsReadOnly();
            }, token);
        }
        finally { gate.Release(); }
    }
}
