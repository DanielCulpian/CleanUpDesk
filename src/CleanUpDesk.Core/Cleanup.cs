using System.Runtime.InteropServices;

namespace CleanUpDesk.Core;
/// <summary>Carries eligible temporary paths, logical-size estimates, omissions, and truncation state.</summary>
public sealed record CleanupPreview(IReadOnlyList<string> Paths, long Size, long Skipped, bool Truncated);
public sealed class CleanupProvider
{
    public string UserTemp => Paths.Normalize(System.IO.Path.GetTempPath());
    public string WindowsTemp => Paths.Normalize(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"));
    public string[] Roots => [UserTemp, WindowsTemp];
    /// <summary>Previews explicitly selected user or Windows temporary categories using their known roots.</summary>
    public Task<CleanupPreview> AnalyzeAsync(bool user, bool windows, int ageDays, CancellationToken token) => AnalyzeRootsAsync((user ? new[] { UserTemp } : []).Concat(windows ? new[] { WindowsTemp } : []), ageDays, token);
    /// <summary>Collects a bounded set of old, accessible temporary files while excluding roots, links, and files in use.</summary>
    public static Task<CleanupPreview> AnalyzeRootsAsync(IEnumerable<string> authorizedRoots, int ageDays, CancellationToken token) => Task.Run(() =>
    {
        var files = new List<string>(); long bytes = 0, skipped = 0; bool truncated = false; var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(ageDays, 1, 3650));
        foreach (var root in authorizedRoots.Select(Paths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { Paths.NoReparseAncestors(root); }
            catch (Exception e) when (Scanner.IsReadError(e)) { skipped++; continue; }
            var stack = new Stack<string>(); stack.Push(root);
            while (stack.TryPop(out var dir))
            {
                token.ThrowIfCancellationRequested(); try
                {
                    foreach (var p in Directory.EnumerateFileSystemEntries(dir))
                    {
                        token.ThrowIfCancellationRequested(); try
                        {
                            Paths.NoReparseAncestors(p); var e = Paths.ReadEntry(p, ""); if (e.IsDirectory) { stack.Push(p); continue; }
                            if (((FileAttributes)e.Attributes & (FileAttributes.System | FileAttributes.ReadOnly)) != 0) { skipped++; continue; }
                            if (e.Modified > cutoff.Ticks || e.Created > cutoff.Ticks) { skipped++; continue; }
                            using (File.Open(p, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                            files.Add(p); bytes += e.Size; if (files.Count >= 20_000) { truncated = true; return new CleanupPreview(files.AsReadOnly(), bytes, skipped, truncated); }
                        }
                        catch (Exception e) when (Scanner.IsReadError(e)) { skipped++; }
                    }
                }
                catch (Exception e) when (Scanner.IsReadError(e)) { skipped++; }
            }
        }
        return new CleanupPreview(files.AsReadOnly(), bytes, skipped, truncated);
    }, token);
}
/// <summary>Captures the reviewed Recycle Bin drive identity, scope, item count, and size estimate.</summary>
public sealed record BinPreview(string Root, string VolumeId, long Count, long Size);
public static class RecycleBin
{
    [StructLayout(LayoutKind.Sequential)] struct Query { public uint SizeOf; public long Bytes, Items; }
    /// <summary>Queries Windows for the item count and estimated size of one drive's Recycle Bin.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHQueryRecycleBin(string root, ref Query query);
    /// <summary>Invokes the Windows Recycle Bin API for the specifically reviewed drive.</summary>
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);
    static readonly SemaphoreSlim gate = new(1);
    /// <summary>Reads a drive-scoped Recycle Bin summary without emptying it.</summary>
    public static BinPreview Preview(Volume v) { var q = new Query { SizeOf = (uint)Marshal.SizeOf<Query>() }; Marshal.ThrowExceptionForHR(SHQueryRecycleBin(v.Root, ref q)); return new(v.Root, v.Id, q.Items, q.Bytes); }
    /// <summary>Revalidates the reviewed drive and bin summary before the separately confirmed permanent empty operation.</summary>
    public static async Task EmptyAsync(BinPreview reviewed, bool reviewCompleted, string phrase, VolumeService volumes)
    {
        if (!reviewCompleted || phrase != "ELIMINAR DEFINITIVAMENTE") throw new InvalidOperationException("Se necesitan revisión y confirmación final.");
        if (!await gate.WaitAsync(0)) throw new IOException("Ya se está vaciando la Papelera.");
        try
        {
            var v = volumes.ForPath(reviewed.Root); if (v.Id != reviewed.VolumeId || !v.Available) throw new IOException("La unidad cambió o está desconectada.");
            var now = Preview(v); if (now.Count != reviewed.Count || now.Size != reviewed.Size) throw new IOException("La Papelera cambió. Analízala y revísala de nuevo.");
            await Sta.Run(() => Marshal.ThrowExceptionForHR(SHEmptyRecycleBin(IntPtr.Zero, reviewed.Root, 1 | 2 | 4)));
        }
        finally { gate.Release(); }
    }
}
