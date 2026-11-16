using System.Runtime.InteropServices;

namespace CleanUpDesk.Core;
public static class SearchLocations
{
    /// <summary>Obtains a Windows known-folder path without constructing a profile path manually.</summary>
    [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    /// <summary>Resolves supported personal folders through Windows APIs and ignores unavailable locations.</summary>
    public static IReadOnlyList<string> PersonalFolders()
    {
        var folders = new List<string>();
        foreach (var kind in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.MyVideos, Environment.SpecialFolder.CommonDesktopDirectory, Environment.SpecialFolder.CommonDocuments })
            folders.Add(Environment.GetFolderPath(kind));
        var downloads = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(ref downloads, 0, IntPtr.Zero, out var path) >= 0)
            try { folders.Add(Marshal.PtrToStringUni(path) ?? ""); } finally { Marshal.FreeCoTaskMem(path); }
        return folders.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    /// <summary>Deduplicates refresh roots without confusing shallow snapshots with recursive scopes.</summary>
    public static IReadOnlyList<SearchRefreshRoot> Collapse(IEnumerable<SearchRefreshRoot> roots)
    {
        var ordered = roots.Select(r => r with { Path = Paths.Normalize(r.Path) }).Distinct().OrderBy(r => r.Path.Length).ThenByDescending(r => r.Recursive);
        var result = new List<SearchRefreshRoot>();
        foreach (var root in ordered)
            if (!result.Any(r => r.VolumeId == root.VolumeId && (r.Path.Equals(root.Path, StringComparison.OrdinalIgnoreCase) && r.Recursive == root.Recursive || r.Recursive && Paths.Within(root.Path, r.Path)))) result.Add(root);
        return result;
    }
}
