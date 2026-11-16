namespace CleanUpDesk.Core;
/// <summary>Associates an old indexed candidate with its advisory recorded access time.</summary>
public sealed record ForgottenFile(Entry File, DateTime? RecordedAccess)
{
    public bool HasWarning => File.HasWarning;
    public FileRisk Risk => File.Risk;
    public string Name => File.Name;
    public string Path => File.Path;
    public string SizeText => File.SizeText;
    public string ModifiedText => File.ModifiedText;
    public string AccessText => RecordedAccess?.ToLocalTime().ToString("g") ?? "Sin dato fiable";
}
/// <summary>Describes bounded old-file review results, omissions, and the checked coverage.</summary>
public sealed record ForgottenPreview(IReadOnlyList<ForgottenFile> Files, long Checked, long Skipped, bool Limited, DateTime CheckedAt);
/// <summary>Creates a bounded old-file reviewer backed by the local index.</summary>
public sealed class ForgottenFileService(IndexStore store)
{
    /// <summary>Checks a bounded set of old indexed candidates and treats recorded access times as advisory rather than proof of disuse.</summary>
    public Task<ForgottenPreview> ReviewAsync(int days, IEnumerable<string> exclusions, CancellationToken token) => Task.Run(() =>
    {
        var cutoff = DateTime.UtcNow.AddDays(-Math.Clamp(days, 30, 3650)); var excluded = exclusions.Select(Paths.Normalize).ToArray();
        var files = new List<ForgottenFile>(); long examined = 0, skipped = 0, total = 0; var policy = new SafetyPolicy();
        for (int page = 0; page < 5 && files.Count < 200; page++)
        {
            token.ThrowIfCancellationRequested();
            var result = store.Search(new() { IsDirectory = false, MinSize = 1, ModifiedTo = cutoff.Ticks, CreatedTo = cutoff.Ticks, Hidden = false, System = false }, "size", true, page); total = result.Total;
            foreach (var entry in result.Items)
            {
                token.ThrowIfCancellationRequested(); examined++;
                try
                {
                    if (excluded.Any(e => Paths.Within(entry.Path, e))) { skipped++; continue; }
                    policy.Check(entry.Path); var info = new FileInfo(entry.Path);
                    if (!info.Exists || (info.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint)) != 0
                        || info.LastWriteTimeUtc > cutoff || info.CreationTimeUtc > cutoff) { skipped++; continue; }
                    DateTime? access = info.LastAccessTimeUtc.Year >= 1980 ? info.LastAccessTimeUtc : null;
                    if (access > cutoff) { skipped++; continue; }
                    files.Add(new(entry with { Size = info.Length, Modified = info.LastWriteTimeUtc.Ticks }, access));
                    if (files.Count == 200) break;
                }
                catch (Exception e) when (Scanner.IsReadError(e)) { skipped++; }
            }
            if ((page + 1L) * 200 >= total) break;
        }
        return new ForgottenPreview(files.OrderByDescending(f => f.File.Size).ToArray(), examined, skipped, examined < total, DateTime.Now);
    }, token);
}
