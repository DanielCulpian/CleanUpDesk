namespace CleanUpDesk.Core;
public enum DeleteAccess { Allowed, Denied, InUse, Unavailable, Unknown }
/// <summary>Associates a protected path with the reason the application refuses to delete it.</summary>
public sealed record ProtectedTarget(string Path, string Reason);
/// <summary>Describes a non-destructive access/protection assessment without declaring an item unnecessary.</summary>
public sealed record FileRisk(string Label, string Detail, bool BlockDeletion)
{
    public static FileRisk None { get; } = new("", "Sin alertas detectadas; se verificará de nuevo antes de operar. No garantiza que el archivo sea prescindible.", false);
}
/// <summary>Carries the exact protected path so the UI can explain and copy the blocked destination.</summary>
public sealed class ProtectedDeletionException(string path, string reason) : IOException(reason)
{
    public string TargetPath { get; } = path;
}
/// <summary>Creates a protection classifier with an optional non-destructive access probe.</summary>
public sealed class FileRiskInspector(SafetyPolicy policy, Func<string, DeleteAccess>? accessProbe = null)
{
    readonly Func<string, DeleteAccess> probe = accessProbe ?? NativeFiles.ProbeDeleteAccess;
    /// <summary>Classifies protected, system, read-only, unavailable, or restricted entries using non-destructive access checks.</summary>
    public FileRisk Inspect(Entry entry, bool available, bool temporary = false)
    {
        if (policy.ProtectionReason(entry.Path, temporary) is { } reason) return new("Protegido", reason, true);
        if (!available && ((FileAttributes)entry.Attributes & FileAttributes.System) != 0) return new("Sistema", "El último índice lo marca como sistema; no se ha podido verificar su estado actual. Su eliminación está bloqueada.", true);
        if (!available) return new("Sin verificar", "La unidad o el elemento no están disponibles; no se pueden comprobar sus permisos actuales.", true);
        try
        {
            Paths.NoReparseAncestors(entry.Path); var attributes = File.GetAttributes(entry.Path);
            if ((attributes & FileAttributes.System) != 0) return new("Sistema", "Elemento marcado como sistema por Windows. Su eliminación está bloqueada.", true);
            if (!entry.IsDirectory && (attributes & FileAttributes.ReadOnly) != 0) return new("Solo lectura", "Archivo de solo lectura. CleanUpDesk no cambia sus atributos ni fuerza su eliminación.", true);
            return probe(entry.Path) switch
            {
                DeleteAccess.Allowed => FileRisk.None,
                DeleteAccess.Denied => new("Acceso restringido", "El proceso no dispone de acceso de borrado. Puede requerir permisos adicionales o estar protegido por una política. No se solicitará elevación ni se cambiarán permisos.", true),
                DeleteAccess.InUse => new("En uso", "Otro proceso impide comprobar el acceso de borrado. Esto no significa que se necesiten derechos de administrador.", true),
                DeleteAccess.Unavailable => new("No disponible", "El elemento o su unidad ya no están disponibles.", true),
                _ => new("Sin verificar", "No se pudo verificar el acceso de borrado; se bloquea por precaución.", true)
            };
        }
        catch (UnauthorizedAccessException) { return new("Acceso restringido", "Windows denegó la lectura de los metadatos o de la ruta. No se elevarán permisos.", true); }
        catch (FileNotFoundException) { return new("No disponible", "El archivo ya no existe o no está disponible.", true); }
        catch (DirectoryNotFoundException) { return new("No disponible", "La carpeta ya no existe o la unidad no está disponible.", true); }
        catch (Exception e) when (Scanner.IsReadError(e)) { return new("Sin verificar", "No se pudo comprobar de forma segura: " + e.Message, true); }
    }
}
