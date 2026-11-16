using System.Diagnostics;

namespace CleanUpDesk.Core;
/// <summary>Describes one known browser HTTP-cache root and the process that must be closed.</summary>
public sealed record BrowserCacheTarget(string Browser, string Root, string Process);
/// <summary>Associates authorized browser-cache roots with their bounded candidate preview.</summary>
public sealed record BrowserCachePreview(CleanupPreview Files, IReadOnlyList<BrowserCacheTarget> Targets, string Detail);

/// <summary>Only HTTP caches in known local profiles; excludes cookies, sessions, history, and passwords.</summary>
public sealed class BrowserCacheProvider(string? localData = null, Func<string, bool>? isRunning = null)
{
    readonly string local = localData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    readonly Func<string, bool> running = isRunning ?? ProcessRunning;
    /// <summary>Checks whether the corresponding browser process is running before allowing cache inspection or deletion.</summary>
    public static bool ProcessRunning(string name)
    {
        var processes = Process.GetProcessesByName(name);
        try { return processes.Length > 0; } finally { foreach (var process in processes) process.Dispose(); }
    }
    /// <summary>Discovers supported HTTP-cache roots in known local browser profiles without including personal browser data.</summary>
    public IReadOnlyList<BrowserCacheTarget> Discover()
    {
        var targets = new List<BrowserCacheTarget>();
        foreach (var (label, relative, process) in new[] {
            ("Chrome", @"Google\Chrome\User Data", "chrome"), ("Edge", @"Microsoft\Edge\User Data", "msedge"),
            ("Brave", @"BraveSoftware\Brave-Browser\User Data", "brave"), ("Vivaldi", @"Vivaldi\User Data", "vivaldi") })
        {
            var profiles = Path.Combine(local, relative);
            try
            {
                Paths.NoReparseAncestors(profiles);
                foreach (var profile in Directory.EnumerateDirectories(profiles))
                {
                    var name = Path.GetFileName(profile);
                    if (name != "Default" && !(name.StartsWith("Profile ", StringComparison.Ordinal) && int.TryParse(name[8..], out _))) continue;
                    Add(label, Path.Combine(profile, "Cache", "Cache_Data"), process);
                }
            }
            catch (Exception e) when (Scanner.IsReadError(e)) { }
        }
        try
        {
            var profiles = Path.Combine(local, "Mozilla", "Firefox", "Profiles"); Paths.NoReparseAncestors(profiles);
            foreach (var profile in Directory.EnumerateDirectories(profiles)) Add("Firefox", Path.Combine(profile, "cache2", "entries"), "firefox");
        }
        catch (Exception e) when (Scanner.IsReadError(e)) { }
        return targets.AsReadOnly();
        // Summary: Adds a known, existing cache location without accepting arbitrary folders as browser caches.
        void Add(string label, string root, string process)
        {
            try { Paths.NoReparseAncestors(root); if (Directory.Exists(root)) targets.Add(new(label, Paths.Normalize(root), process)); }
            catch (Exception e) when (Scanner.IsReadError(e)) { }
        }
    }
    /// <summary>Previews old, accessible HTTP-cache files only while the supported browsers are closed.</summary>
    public async Task<BrowserCachePreview> AnalyzeAsync(int ageDays, CancellationToken token)
    {
        var all = await Task.Run(Discover, token);
        var targets = all.Where(t => !running(t.Process)).ToArray();
        var omitted = all.Except(targets).Select(t => t.Browser).Distinct().ToArray();
        var files = await CleanupProvider.AnalyzeRootsAsync(targets.Select(t => t.Root), ageDays, token);
        return new(files, targets, (all.Count == 0 ? "No se detectaron cachés compatibles en perfiles locales predeterminados." : "Solo caché HTTP; conserva perfiles, cookies, historial y contraseñas.")
            + (omitted.Length > 0 ? " Cierra para analizar: " + string.Join(", ", omitted) + "." : ""));
    }
}
/// <summary>Creates an execution guard for the reviewed browser-cache roots and deletion backend.</summary>
public sealed class BrowserCacheBackend(IReadOnlyList<BrowserCacheTarget> targets, IDeleteBackend backend, Func<string, bool>? isRunning = null) : IDeleteBackend
{
    /// <summary>Rechecks the authorized cache root and browser state before delegating the reviewed file deletion.</summary>
    public void Delete(string path, bool directory, DeleteMode mode)
    {
        var target = targets.FirstOrDefault(t => Paths.Within(path, t.Root) && !path.Equals(t.Root, StringComparison.OrdinalIgnoreCase));
        if (target == null || directory) throw new IOException("Destino fuera de la caché revisada o carpeta raíz protegida.");
        Paths.NoReparseAncestors(path);
        if ((isRunning ?? BrowserCacheProvider.ProcessRunning)(target.Process)) throw new IOException("El navegador está abierto. Ciérralo y vuelve a analizar su caché.");
        backend.Delete(path, false, mode);
    }
}
