using System.Security.Cryptography;
using System.Text;

namespace CleanUpDesk.Core;
public enum DeleteMode { Recycle, Permanent }
/// <summary>Captures one reviewed deletion target with its native identity and bounded tree stamp.</summary>
public sealed record ReviewedItem(string Path, string VolumeId, string Identity, string TreeStamp, long Size, bool IsDirectory);
public sealed class DeleteReview
{
    /// <summary>Captures the reviewed mode, target snapshots, and temporary-category authorization.</summary>
    internal DeleteReview(IReadOnlyList<ReviewedItem> items, DeleteMode mode) { Items = items; Mode = mode; }
    public IReadOnlyList<ReviewedItem> Items { get; }
    public DeleteMode Mode { get; }
    internal bool Reviewed;
    internal bool Consumed;
    internal DateTime Created = DateTime.UtcNow;
}
public sealed class DeletePermit
{
    /// <summary>Creates a single-use execution permit for a separately reviewed and explicitly confirmed selection.</summary>
    internal DeletePermit(DeleteReview review) { Review = review; }
    internal DeleteReview Review { get; }
}
public interface IDeleteBackend { 
    /// <summary>Deletes only the reviewed target in the supplied recoverable or permanent mode; implementations must never silently switch modes.</summary>
    void Delete(string path, bool directory, DeleteMode mode); }
public sealed class SafetyPolicy
{
    readonly string[] protectedRoots;
    readonly string[] authorizedTemp;
    /// <summary>Builds protected-location rules and any explicitly authorized temporary roots.</summary>
    public SafetyPolicy(IEnumerable<string>? extraProtected = null, IEnumerable<string>? tempRoots = null)
    {
        authorizedTemp = (tempRoots ?? []).Select(Paths.Normalize).ToArray();
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        protectedRoots = new[] { system, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), AppContext.BaseDirectory, Settings.DataFolder }
            .Concat(extraProtected ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Select(Paths.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    /// <summary>Rejects protected paths, unexpected reparse ancestors, and disallowed system attributes.</summary>
    public void Check(string path, bool temp = false)
    {
        path = Paths.Normalize(path);
        if (ProtectionReason(path, temp) is { } reason) throw new ProtectedDeletionException(path, reason);
        try
        {
            Paths.NoReparseAncestors(path);
            if ((File.GetAttributes(path) & FileAttributes.System) != 0) throw new ProtectedDeletionException(path, "Elemento marcado como sistema por Windows.");
        }
        catch (UnauthorizedAccessException) { throw new ProtectedDeletionException(path, "Windows denegó el acceso. CleanUpDesk no solicitará elevación ni cambiará permisos."); }
    }
    /// <summary>Explains why a root, system location, application path, or protected ancestor cannot be deleted.</summary>
    public string? ProtectionReason(string path, bool temp = false)
    {
        path = Paths.Normalize(path);
        if (path.Equals(System.IO.Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) return "Raíz de unidad protegida.";
        var root = System.IO.Path.GetPathRoot(path)!;
        string[] critical = ["Boot", "Recovery", "System Volume Information", "$RECYCLE.BIN", "EFI", "$WinREAgent", "Windows.old"];
        if (critical.Any(n => Paths.Within(path, System.IO.Path.Combine(root, n)) || Paths.Within(System.IO.Path.Combine(root, n), path))) return "Ubicación de arranque, recuperación o sistema protegida.";
        if (new[] { "bootmgr", "BOOTNXT", "pagefile.sys", "swapfile.sys", "hiberfil.sys", "boot.ini", "ntldr", "ntdetect.com" }.Any(n => path.Equals(System.IO.Path.Combine(root, n), StringComparison.OrdinalIgnoreCase))) return "Archivo del sistema protegido.";
        bool authorized = temp && authorizedTemp.Any(t => !path.Equals(t, StringComparison.OrdinalIgnoreCase) && Paths.Within(path, t));
        if (temp && !authorized) return "El elemento está fuera de las ubicaciones temporales autorizadas.";
        foreach (var p in protectedRoots)
        {
            // Only the explicit provider may exempt Windows\Temp; application files and data remain protected.
            bool windowsTemp = authorized && p.Equals(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase);
            if (!windowsTemp && (Paths.Within(path, p) || Paths.Within(p, path))) return "Ubicación de Windows, aplicaciones o datos protegidos; también se bloquean sus carpetas contenedoras.";
        }
        return null;
    }
}
/// <summary>Creates the shared deletion coordinator with index, volume, protection-policy, and backend dependencies.</summary>
public sealed class DeletionService(IndexStore store, VolumeService volumes, SafetyPolicy policy, IDeleteBackend backend, Func<string, DeleteAccess>? accessProbe = null)
{
    readonly FileRiskInspector riskInspector = new(policy, accessProbe);
    static readonly SemaphoreSlim destructiveGate = new(1);
    /// <summary>Collapses targets and snapshots their identity and contents for the first safety-review step.</summary>
    public Task<DeleteReview> PrepareAsync(IEnumerable<string> selection, DeleteMode mode, bool temporary = false, CancellationToken token = default) => Task.Run(() =>
    {
        var items = new List<ReviewedItem>(); var available = volumes.Detect();
        foreach (var path in Paths.Collapse(selection))
        {
            token.ThrowIfCancellationRequested(); policy.Check(path, temporary); var v = available.FirstOrDefault(v => v.Available && string.Equals(v.Root, System.IO.Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase)) ?? throw new IOException("Unidad no disponible.");
            var e = ReadForDeletion(path, v.Id); var stamp = Snapshot(path, temporary, token);
            items.Add(new(path, v.Id, NativeFiles.Identity(path), stamp.Stamp, stamp.Size, e.IsDirectory));
        }
        if (items.Count == 0) throw new ArgumentException("Selecciona al menos un elemento.");
        return new DeleteReview(items.AsReadOnly(), mode);
    }, token);
    /// <summary>Records the exact retained selection after the user inspects the review list.</summary>
    public void CompleteReview(DeleteReview review, IEnumerable<string> retained)
    {
        if (review.Consumed || review.Reviewed) throw new InvalidOperationException("La revisión ya ha terminado.");
        if (!review.Items.Select(i => i.Path).SequenceEqual(retained, StringComparer.OrdinalIgnoreCase)) throw new InvalidOperationException("La selección cambió: prepara una nueva revisión.");
        review.Reviewed = true;
    }
    /// <summary>Requires the separate final action and the exact permanent-deletion phrase before issuing a permit.</summary>
    public DeletePermit Confirm(DeleteReview review, bool explicitAction, string phrase = "")
    {
        if (!review.Reviewed || review.Consumed || !explicitAction || DateTime.UtcNow - review.Created > TimeSpan.FromMinutes(10)) throw new InvalidOperationException("Es necesaria una revisión vigente y una confirmación final.");
        if (review.Mode == DeleteMode.Permanent && phrase != "ELIMINAR DEFINITIVAMENTE") throw new InvalidOperationException("Escribe ELIMINAR DEFINITIVAMENTE.");
        review.Consumed = true; return new(review);
    }
    /// <summary>Serializes destructive execution, revalidates each target, and records completed, skipped, or failed outcomes.</summary>
    public async Task<IReadOnlyList<OperationResult>> ExecuteAsync(DeletePermit permit, bool temporary, CancellationToken token, IProgress<OperationResult>? progress = null)
    {
        if (!await destructiveGate.WaitAsync(0)) throw new IOException("Ya hay una operación destructiva en curso.");
        try
        {
            if (!usedPermits.Add(permit)) throw new InvalidOperationException("Esta confirmación ya se utilizó.");
            var results = new List<OperationResult>();
            foreach (var item in permit.Review.Items)
            {
                if (token.IsCancellationRequested) { var skipped = new OperationResult(item.Path, "Omitido", "Cancelado; los elementos completados no se revierten."); store.Log(skipped); results.Add(skipped); progress?.Report(skipped); continue; }
                OperationResult result;
                try
                {
                    await Task.Run(() =>
                    {
                        policy.Check(item.Path, temporary);
                        if (temporary && !item.IsDirectory) { using var probe = File.Open(item.Path, FileMode.Open, FileAccess.Read, FileShare.None); }
                        if (!volumes.Matches(item.Path, item.VolumeId)) throw new IOException("La unidad no está disponible o su identidad cambió.");
                        if (NativeFiles.Identity(item.Path) != item.Identity || Snapshot(item.Path, temporary, token).Stamp != item.TreeStamp) throw new IOException("El elemento cambió desde la revisión. Vuelve a revisarlo.");
                    }, token);
                    // Shell COM must run in an STA, never on the UI thread.
                    await Sta.Run(() => backend.Delete(item.Path, item.IsDirectory, permit.Review.Mode));
                    store.RemoveTree(item.Path); result = new(item.Path, "Eliminado", permit.Review.Mode == DeleteMode.Recycle ? "Enviado a la Papelera. Puedes abrirla desde Limpieza." : "Eliminación permanente.");
                }
                catch (Exception e)
                {
                    string? blocked = e is ProtectedDeletionException protectedError ? protectedError.TargetPath : e is UnauthorizedAccessException ? item.Path : null;
                    if (blocked == null && e is not OperationCanceledException)
                        try { if (riskInspector.Inspect(Paths.ReadEntry(item.Path, item.VolumeId), true, temporary).BlockDeletion) blocked = item.Path; }
                        catch (Exception read) when (Scanner.IsReadError(read)) { }
                    result = new(item.Path, e is OperationCanceledException ? "Omitido" : "Error", e.Message) { ProtectionPath = blocked };
                }
                store.Log(result); results.Add(result); progress?.Report(result);
            }
            return results;
        }
        finally { destructiveGate.Release(); }
    }
    readonly HashSet<DeletePermit> usedPermits = [];
    /// <summary>Reads target metadata while translating access denial into an actionable protection result.</summary>
    static Entry ReadForDeletion(string path, string volume)
    {
        try { return Paths.ReadEntry(path, volume); }
        catch (UnauthorizedAccessException) { throw new ProtectedDeletionException(path, "No se pueden comprobar los metadatos con los permisos actuales. No se fuerza el borrado."); }
    }
    /// <summary>Builds a bounded identity/metadata snapshot without traversing reparse points or unreadable descendants.</summary>
    (string Stamp, long Size) Snapshot(string path, bool temp, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long size = 0, count = 0; var stack = new Stack<string>(); stack.Push(path);
        while (stack.TryPop(out var p))
        {
            token.ThrowIfCancellationRequested(); if (++count > 100_000) throw new IOException("La selección supera 100.000 elementos. Revisa carpetas más pequeñas.");
            policy.Check(p, temp); var e = ReadForDeletion(p, "");
            var risk = riskInspector.Inspect(e, true, temp);
            if (risk.BlockDeletion) throw new ProtectedDeletionException(p, risk.Detail);
            hash.AppendData(Encoding.UTF8.GetBytes($"{p}\0{NativeFiles.Identity(p)}\0{e.Size}\0{e.Modified}\0{e.Attributes}\n")); size += e.Size;
            try { if (e.IsDirectory) foreach (var child in Directory.EnumerateFileSystemEntries(p).Order(StringComparer.OrdinalIgnoreCase).Reverse()) stack.Push(child); }
            catch (UnauthorizedAccessException) { throw new ProtectedDeletionException(p, "No se puede revisar el contenido de la carpeta con los permisos actuales. Se bloquea toda la selección."); }
        }
        return (Convert.ToHexString(hash.GetHashAndReset()), size);
    }
}
public static class Sta
{
    /// <summary>Executes the supplied shell operation on a dedicated STA thread rather than the UI thread.</summary>
    public static Task Run(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); tcs.SetResult(); } catch (Exception e) { tcs.SetException(e); } }); thread.SetApartmentState(ApartmentState.STA); thread.IsBackground = true; thread.Start(); return tcs.Task;
    }
}
