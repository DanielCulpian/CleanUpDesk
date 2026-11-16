using System.Text.Json;

namespace CleanUpDesk.Core;

public enum VolumeConnection { Ready, NoMedia, Disconnected, AccessDenied, Error }
/// <summary>Stores stable volume metadata separately from the currently detected availability state.</summary>
public sealed record Volume(string Id, string Root, string Label, long Total, long Free, bool Available)
{
    public VolumeConnection Connection { get; init; } = VolumeConnection.Ready;
    public string Reason { get; init; } = "";
    public string Title => string.IsNullOrWhiteSpace(Label) || Label == "Unidad no disponible" ? $"Disco ({Root.TrimEnd('\\')})" : $"{Label} ({Root.TrimEnd('\\')})";
    public string Caption => Title;
    public string Capacity => Available ? $"{Format.Bytes(Total - Free)} de {Format.Bytes(Total)}" : Total > 0 ? "Capacidad del último análisis" : "Sin acceso al contenido";
    public string FreeText => Available ? $"{Format.Bytes(Free)} libres" : State;
    public double UsedPercent => Total == 0 ? 0 : 100d * (Total - Free) / Total;
    public string State => Available ? "Disponible" : Connection switch { VolumeConnection.NoMedia => "Dispositivo extraíble sin medio disponible", VolumeConnection.AccessDenied => "Sin acceso · comprueba si está bloqueada", VolumeConnection.Error => "No se pudo leer la unidad", _ => "Desconectada · índice conservado" };
    /// <summary>Returns the user-facing label used by selectors rather than the record's implementation details.</summary>
    public override string ToString() => Caption;
}
/// <summary>Stores file metadata and transient presentation state without retaining file contents.</summary>
public sealed record Entry(string Path, string Parent, string Name, string Extension, bool IsDirectory,
    long Size, long Created, long Modified, int Attributes, string VolumeId)
{
    public string Type => IsDirectory ? "Carpeta" : string.IsNullOrEmpty(Extension) ? "Archivo" : Extension;
    public long? FolderLogicalSize { get; init; }
    public string FolderSizeDetail { get; init; } = "Selecciona esta carpeta y pulsa Calcular tamaño para sumar los archivos que contiene.";
    public bool FolderSizePartial { get; init; }
    public string SizeText => IsDirectory ? FolderLogicalSize.HasValue ? Format.Bytes(FolderLogicalSize.Value) + (FolderSizePartial ? " (parcial)" : "") : "Sin calcular" : Format.Bytes(Size);
    public string SizeHint => IsDirectory ? FolderSizeDetail : $"Tamaño lógico: {Size:N0} bytes";
    public string ModifiedText => new DateTime(Modified, DateTimeKind.Utc).ToLocalTime().ToString("g");
    public string Availability { get; init; } = "Sin comprobar";
    public FileRisk Risk { get; init; } = FileRisk.None;
    public bool HasWarning => Risk.BlockDeletion;
    public string Drive => System.IO.Path.GetPathRoot(Path) ?? "";
    /// <summary>Returns the user-facing label used by selectors rather than the record's implementation details.</summary>
    public override string ToString() => Name;
}
/// <summary>Carries actual scan counters, current path, and elapsed time without an invented percentage.</summary>
public sealed record ScanProgress(long Processed, long Skipped, string Current, TimeSpan Elapsed);
/// <summary>Records actual scan coverage, omissions, elapsed time, and partial-cancellation status.</summary>
public sealed record ScanReport(long Processed, long Skipped, bool Cancelled, string Root, TimeSpan Elapsed);
public sealed record SearchFilter
{
    public string Name { get; init; } = "";
    public bool Exact { get; init; }
    public string[] Extensions { get; init; } = [];
    public bool? IsDirectory { get; init; }
    public long? MinSize { get; init; }
    public long? MaxSize { get; init; }
    public long? ModifiedFrom { get; init; }
    public long? ModifiedTo { get; init; }
    public long? CreatedFrom { get; init; }
    public long? CreatedTo { get; init; }
    public string Folder { get; init; } = "";
    public bool Recursive { get; init; } = true;
    public bool? Hidden { get; init; }
    public bool? System { get; init; }
    public string[] Volumes { get; init; } = [];
}
/// <summary>Carries a bounded indexed result page and the full matching indexed count.</summary>
public sealed record SearchPage(IReadOnlyList<Entry> Items, long Total);
/// <summary>Stores a local named search filter for later explicit execution.</summary>
public sealed record Favorite(string Name, SearchFilter Filter) { 
    /// <summary>Returns the user-facing label used by selectors rather than the record's implementation details.</summary>
    public override string ToString() => Name; }
/// <summary>Records one deletion outcome and optional protected-path context without file contents.</summary>
public sealed record OperationResult(string Path, string Status, string Detail) { public string? ProtectionPath { get; init; } }
/// <summary>Identifies a search-preparation root, its stable volume, and requested recursion mode.</summary>
public sealed record SearchRefreshRoot(string Path, string VolumeId, bool Recursive = true);
/// <summary>Associates snapshot metadata with whether existing descendant records must be retained.</summary>
public sealed record DirectoryRefreshItem(string Path, Entry? Entry);
/// <summary>Describes one reconciled scope, real omissions, and whether full requested coverage completed.</summary>
public sealed record SearchRefreshReport(string Path, long Updated, long Skipped, bool Completed, string Detail);
/// <summary>Carries actual traversal work and optional historical estimates for search preparation.</summary>
public sealed record SearchPreparationProgress(string Path, long CompletedDirectories, long KnownPendingDirectories, long? HistoricalPendingDirectories);
public static class Format
{
    /// <summary>Formats a logical byte count using clear binary size units.</summary>
    public static string Bytes(long value) { string[] u = ["B", "KB", "MB", "GB", "TB"]; double n = value; int i = 0; while (n >= 1024 && i < 4) { n /= 1024; i++; } return $"{n:0.##} {u[i]}"; }
}
public sealed class Settings
{
    public bool Dark { get; set; } = true;
    public bool SearchCompletionSound { get; set; } = true;
    public bool IncludeHidden { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("RefreshDesktopOnSearch")]
    // Legacy preference retained for configuration compatibility; refreshing is now an explicit action.
    public bool RefreshSearchLocations { get; set; } = true;
    public int AgeDays { get; set; } = 14;
    public int ForgottenDays { get; set; } = 180;
    public int RetentionDays { get; set; } = 30;
    public List<string> Exclusions { get; set; } = [];
    public List<Favorite> Favorites { get; set; } = [];
    public static string DataFolder => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CleanUpDesk");
    /// <summary>Loads local settings, falling back to safe defaults if the file is missing, unreadable, or invalid.</summary>
    public static Settings Load() { try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(System.IO.Path.Combine(DataFolder, "settings.json"))) ?? new(); } catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); } }
    /// <summary>Persists settings in the current user's application-data directory without contacting external services.</summary>
    public void Save() { Directory.CreateDirectory(DataFolder); File.WriteAllText(System.IO.Path.Combine(DataFolder, "settings.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
}
