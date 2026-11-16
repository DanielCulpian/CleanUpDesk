namespace CleanUpDesk.Core;

public static class Paths
{
    /// <summary>Validates a fully qualified local filesystem path and rejects device, network, wildcard, and alternate-stream syntax.</summary>
    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path) || path.StartsWith(@"\\?\") || path.StartsWith(@"\\.\") || path.Contains('*') || path.Contains('?')) throw new ArgumentException("Se necesita una ruta local absoluta.");
        var p = System.IO.Path.GetFullPath(path);
        if (p.StartsWith(@"\\") || p[2..].Contains(':')) throw new ArgumentException("Las rutas de red, dispositivos y flujos alternativos no están admitidos.");
        return System.IO.Path.TrimEndingDirectorySeparator(p);
    }
    /// <summary>Checks a case-insensitive path boundary without confusing neighboring prefix names.</summary>
    public static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.EndsWith('\\') ? root : root + "\\", StringComparison.OrdinalIgnoreCase);
    /// <summary>Normalizes targets and removes duplicates or descendants already covered by a selected ancestor.</summary>
    public static string[] Collapse(IEnumerable<string> paths)
    {
        var ordered = paths.Select(Normalize).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(p => p.Length).ToArray();
        var result = new List<string>(); foreach (var p in ordered) if (!result.Any(r => Within(p, r))) result.Add(p); return result.ToArray();
    }
    /// <summary>Rejects a target if it or any ancestor is a symbolic link, junction, or other reparse point.</summary>
    public static void NoReparseAncestors(string path)
    {
        for (string? p = Normalize(path); p != null; p = System.IO.Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("Se ha omitido un enlace o punto de reanálisis.");
    }
    /// <summary>Reads basic filesystem metadata without opening or hashing file contents.</summary>
    public static Entry ReadEntry(string path, string volumeId)
    {
        var a = File.GetAttributes(path); bool dir = a.HasFlag(FileAttributes.Directory);
        FileSystemInfo info = dir ? new DirectoryInfo(path) : new FileInfo(path);
        return new(path, System.IO.Path.GetDirectoryName(path) ?? path, info.Name, dir ? "" : info.Extension.ToUpperInvariant(), dir, dir ? 0 : ((FileInfo)info).Length, info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks, (int)a, volumeId);
    }
    /// <summary>Checks only the first directory entry, including hidden/system entries and propagating access errors.</summary>
    public static bool IsDirectoryEmpty(string path)
    {
        // Do not ignore hidden/system entries or access denial; do not descend into subfolders or links.
        using var entries = Directory.EnumerateFileSystemEntries(path, "*", new EnumerationOptions
        {
            RecurseSubdirectories = false, ReturnSpecialDirectories = false,
            AttributesToSkip = 0, IgnoreInaccessible = false
        }).GetEnumerator();
        return !entries.MoveNext();
    }
}
