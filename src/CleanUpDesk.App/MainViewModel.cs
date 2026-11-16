using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using CleanUpDesk.Core;

namespace CleanUpDesk.App;
public sealed class MainViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    /// <summary>Raises a property-change notification for bound presentation state.</summary>
    public void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    /// <summary>Initializes settings and collection notifications without starting filesystem work.</summary>
    public MainViewModel(Settings? settings = null)
    {
        Settings = settings ?? Settings.Load();
        Volumes.CollectionChanged += (_, _) => { Notify(nameof(DriveVolumes)); Notify(nameof(NoMediaSummary)); Notify(nameof(Scopes)); Notify(nameof(AdvancedScopes)); Notify(nameof(DriveSummary)); };
    }
    public Settings Settings { get; }
    public string AppTitle => AppBuild.Title;
    public string DataFolder => Core.Settings.DataFolder;
    public string UserTempLocation => new CleanupProvider().UserTemp;
    public string WindowsTempLocation => new CleanupProvider().WindowsTemp;
    public ObservableCollection<Volume> Volumes { get; } = [];
    public ObservableCollection<Entry> Results { get; } = [];
    public ObservableCollection<Entry> BrowserItems { get; } = [];
    public ObservableCollection<Breadcrumb> Breadcrumbs { get; } = [];
    public ObservableCollection<FilterChip> FilterChips { get; } = [];
    public IEnumerable<Volume> DriveVolumes => Volumes.Where(v => v.Connection != VolumeConnection.NoMedia);
    public string NoMediaSummary => string.Join(" · ", Volumes.Where(v => v.Connection == VolumeConnection.NoMedia).Select(v => $"{v.Root.TrimEnd('\\')} sin medio disponible"));
    public string DriveSummary => $"{Volumes.Count(v => v.Available)} unidades disponibles · selecciona una para ver sus carpetas";
    public IEnumerable<SearchScope> Scopes => new[] { new SearchScope("Todos los discos", ""), new SearchScope("Varios discos…", "*") }.Concat(DriveVolumes.Select(v => new SearchScope(v.Caption, v.Id)));
    Volume? selectedVolume;
    public Volume? SelectedVolume { get => selectedVolume; set { selectedVolume = value; Notify(); } }
    public string BrowserAddress { get; set; } = "";
    public string BrowserSummary { get; set; } = "Selecciona un disco o abre una carpeta para comenzar.";
    public string BrowserCount { get; set; } = "Exploración en directo";
    public string BrowserEmptyTitle { get; set; } = "Explora sin analizar todo el disco";
    public string BrowserEmptyHint { get; set; } = "Haz clic en una tarjeta o abre una carpeta. Doble clic para entrar; la flecha ↑ te lleva al nivel anterior.";
    public string BrowseName { get; set; } = "";
    bool browserEmpty = true;
    public bool BrowserEmpty { get => browserEmpty; set { browserEmpty = value; Notify(); } }
    bool searchEmpty = true;
    public bool SearchEmpty { get => searchEmpty; set { searchEmpty = value; Notify(); } }
    public string EmptyTitle { get; set; } = "Encuentra tus archivos, sin perderte";
    public string EmptyHint { get; set; } = "Buscar consulta el índice guardado. Si falta un archivo nuevo o movido, usa Revisar carpeta o Buscar en discos.";
    public string SearchCoverage { get; set; } = "Buscar consulta el índice guardado. Revisar carpeta o Buscar en discos incorpora archivos nuevos y movidos.";
    public string SearchCoverageDetails { get; set; } = "Todos los discos incluye las unidades locales disponibles, también externas. Se vigilan cambios mientras la aplicación está abierta y se reconcilian antes de buscar, incluidas las interrupciones detectadas. No es una garantía de actualización perfecta en tiempo real.";
    bool searchInProgress;
    public bool SearchInProgress { get => searchInProgress; set { searchInProgress = value; Notify(); Notify(nameof(CanStopSearch)); Notify(nameof(CanModifyFiles)); } }
    bool searchStopRequested;
    public bool SearchStopRequested { get => searchStopRequested; set { searchStopRequested = value; Notify(); Notify(nameof(CanStopSearch)); } }
    public bool CanStopSearch => SearchInProgress && !SearchStopRequested;
    string searchEtaText = "Restante estimado: calculando…";
    public string SearchEtaText { get => searchEtaText; set { searchEtaText = value; Notify(); } }
    string searchStateText = "Lista para buscar";
    public string SearchStateText { get => searchStateText; set { searchStateText = value; Notify(); } }
    public ObservableCollection<string> Scans { get; } = [];
    public ObservableCollection<string> History { get; } = [];
    public ObservableCollection<Entry> CleanupPaths { get; } = [];
    public ObservableCollection<Entry> BrowserCachePaths { get; } = [];
    bool hideWarnings;
    public bool HideWarnings { get => hideWarnings; set { hideWarnings = value; Notify(); } }
    public string WarningSummary { get; set; } = "";
    public ObservableCollection<ForgottenFile> ForgottenFiles { get; } = [];
    public ObservableCollection<AdvancedFinding> AdvancedRows { get; } = [];
    public IEnumerable<SearchScope> AdvancedScopes => new[] { new SearchScope("Todos los discos indexados", "") }.Concat(DriveVolumes.Select(v => new SearchScope(v.Caption, v.Id)));
    int advancedMode;
    public int AdvancedMode { get => advancedMode; set { advancedMode = value; Notify(); Notify(nameof(AdvancedDuplicates)); } }
    public bool AdvancedDuplicates => AdvancedMode == 0;
    public int DuplicateMode { get; set; }
    public int KeepMode { get; set; }
    public string AdvancedFolder { get; set; } = "";
    bool advancedBusy;
    public bool AdvancedBusy { get => advancedBusy; set { advancedBusy = value; Notify(); Notify(nameof(CanModifyFiles)); Notify(nameof(AdvancedCanConfigure)); } }
    public bool AdvancedCanConfigure => !AdvancedBusy;
    string advancedStatus = "Elige una función. Se usa el índice guardado; analiza o revisa primero las ubicaciones que falten.";
    public string AdvancedStatus { get => advancedStatus; set { advancedStatus = value; Notify(); } }
    string advancedCount = "Sin resultados";
    public string AdvancedCount { get => advancedCount; set { advancedCount = value; Notify(); } }
    string forgottenSummary = "Se revisará el índice al abrir. No se elimina nada automáticamente.";
    public string ForgottenSummary { get => forgottenSummary; set { forgottenSummary = value; Notify(); } }
    public string BrowserCacheSummary { get; set; } = "Cierra el navegador y analiza para obtener una vista previa.";
    string searchActivity = "";
    public string SearchActivity { get => searchActivity; set { searchActivity = value; Notify(); } }
    string status = "Listo. Añade una carpeta o selecciona unidades para comenzar.";
    public string Status { get => status; set { status = value; Notify(); } }
    bool busy; public bool Busy { get => busy; set { busy = value; Notify(); Notify(nameof(CanInteract)); Notify(nameof(CanModifyFiles)); } }
    public bool CanInteract => !Busy;
    public bool CanModifyFiles => !Busy && !SearchInProgress && !AdvancedBusy;
    int section;
    public int Section { get => section; set { section = value; Notify(); Notify(nameof(Selected)); } }
    public string Name { get; set; } = "";
    public string Extensions { get; set; } = "";
    public int Kind { get; set; }
    public string MinSize { get; set; } = "";
    public string MaxSize { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool Exact { get; set; }
    public bool Recursive { get; set; } = true;
    public int Hidden { get; set; }
    public int SystemFlag { get; set; }
    public DateTime? ModifiedFrom { get; set; }
    public DateTime? ModifiedTo { get; set; }
    public DateTime? CreatedFrom { get; set; }
    public DateTime? CreatedTo { get; set; }
    public string FavoriteName { get; set; } = "Mi búsqueda";
    public string Exclusions { get; set; } = "";
    Entry? browserSelected, resultSelected;
    public Entry? BrowserSelected { get => browserSelected; set { browserSelected = value; if (Section == 0) SelectionChanged(); Notify(); } }
    public Entry? ResultSelected { get => resultSelected; set { resultSelected = value; if (Section != 0) SelectionChanged(); Notify(); } }
    public Entry? Selected { get => Section == 0 ? BrowserSelected : ResultSelected; set { if (Section == 0) BrowserSelected = value; else ResultSelected = value; } }
    /// <summary>Refreshes details bindings when the active browser/search selection changes.</summary>
    void SelectionChanged() { FolderSizeText = ""; Notify(nameof(Selected)); Notify(nameof(FolderSizeText)); }
    public string FolderSizeText { get; set; } = "";
    public string ResultCount { get; set; } = "Índice local · listo para buscar";
    public string ActiveFilters { get; set; } = "Sin filtros · todas las unidades indexadas";
    public string CleanupSummary { get; set; } = "Aún no se ha analizado.";
    public string BinSummary { get; set; } = "Aún no se ha analizado.";
    /// <summary>Builds the immutable search filter from UI fields, validating sizes and inclusive date bounds.</summary>
    public SearchFilter Filter(IEnumerable<string> volumes)
    {
        // Summary: Parses an optional non-negative MiB input and rejects values that would overflow a byte count.
        long? Size(string s) { if (string.IsNullOrWhiteSpace(s)) return null; if (!decimal.TryParse(s, out var n) || n < 0 || n > long.MaxValue / 1048576m) throw new ArgumentException("Escribe un tamaño válido en MB."); return (long)(n * 1048576m); }
        // Summary: Converts an optional local start date to its inclusive UTC lower bound.
        long? From(DateTime? d) => d?.Date.ToUniversalTime().Ticks;
        // Summary: Converts an optional local end date to its inclusive UTC end-of-day bound.
        long? To(DateTime? d) => d?.Date.AddDays(1).AddTicks(-1).ToUniversalTime().Ticks;
        return new() { Name = Name.Trim(), Extensions = Extensions.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), Exact = Exact, IsDirectory = Kind == 0 ? null : Kind == 2, MinSize = Size(MinSize), MaxSize = Size(MaxSize), Folder = Folder.Trim(), Recursive = Recursive, Hidden = Hidden == 0 ? null : Hidden == 1, System = SystemFlag == 0 ? null : SystemFlag == 1, ModifiedFrom = From(ModifiedFrom), ModifiedTo = To(ModifiedTo), CreatedFrom = From(CreatedFrom), CreatedTo = To(CreatedTo), Volumes = volumes.ToArray() };
    }
    /// <summary>Restores a saved search filter into the editable presentation fields.</summary>
    public void Load(SearchFilter f)
    {
        Name = f.Name; Extensions = string.Join(", ", f.Extensions); Kind = f.IsDirectory.HasValue ? f.IsDirectory.Value ? 2 : 1 : 0; Exact = f.Exact; MinSize = f.MinSize.HasValue ? (f.MinSize.Value / 1048576m).ToString() : ""; MaxSize = f.MaxSize.HasValue ? (f.MaxSize.Value / 1048576m).ToString() : ""; Folder = f.Folder; Recursive = f.Recursive; Hidden = f.Hidden.HasValue ? f.Hidden.Value ? 1 : 2 : 0; SystemFlag = f.System.HasValue ? f.System.Value ? 1 : 2 : 0;
        // Summary: Converts stored UTC filter ticks back into an optional local calendar date.
        DateTime? Local(long? t) => t.HasValue ? new DateTime(t.Value, DateTimeKind.Utc).ToLocalTime().Date : null;
        ModifiedFrom = Local(f.ModifiedFrom); ModifiedTo = Local(f.ModifiedTo); CreatedFrom = Local(f.CreatedFrom); CreatedTo = Local(f.CreatedTo); NotifyFilterFields();
    }
    /// <summary>Refreshes all bound filter editors after loading or clearing a saved scope.</summary>
    public void NotifyFilterFields()
    {
        foreach (var name in new[] { nameof(Name), nameof(Extensions), nameof(Kind), nameof(Exact), nameof(MinSize), nameof(MaxSize), nameof(Folder), nameof(Recursive), nameof(Hidden), nameof(SystemFlag), nameof(ModifiedFrom), nameof(ModifiedTo), nameof(CreatedFrom), nameof(CreatedTo) }) Notify(name);
    }
}
/// <summary>Stores an ancestor label and path for explicit browser navigation.</summary>
public sealed record Breadcrumb(string Label, string Path) { 
    /// <summary>Returns the breadcrumb or scope label displayed to the user.</summary>
    public override string ToString() => Label; }
/// <summary>Stores one removable active-filter label and its explanatory tooltip.</summary>
public sealed record FilterChip(string Key, string Label, string Tooltip = "") { public string Description => Tooltip.Length == 0 ? Label : Tooltip; }
/// <summary>Stores a user-facing scope label and its stable volume-selection key.</summary>
public sealed record SearchScope(string Label, string VolumeId) { 
    /// <summary>Returns the breadcrumb or scope label displayed to the user.</summary>
    public override string ToString() => Label; }
