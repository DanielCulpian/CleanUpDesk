using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CleanUpDesk.Core;
using Microsoft.Win32;

namespace CleanUpDesk.App;
public partial class MainWindow : Window
{
    readonly MainViewModel vm;
    readonly bool persistSettings;
    readonly CompletionSound sound;
    readonly Action<long> completionSound;
    readonly Action<IReadOnlyList<ProtectedTarget>> protectionNotice;
    readonly FileRiskInspector riskInspector = new(new SafetyPolicy());
    readonly VolumeService volumeService = new();
    readonly CleanupProvider cleanup = new();
    readonly IndexStore store;
    readonly Scanner scanner;
    readonly DirectoryBrowser browser;
    readonly SearchIndexSession searchSession;
    SearchTimeEstimator? searchEstimator;
    readonly Dictionary<string, Entry> measuredFolders = new(StringComparer.OrdinalIgnoreCase);
    string browsePath = "", browseVolume = "";
    bool browseOffline;
    int browsePage;
    long browseTotal;
    string browseSort = "name";
    bool browseDescending;
    bool syncingScope;
    readonly DeletionService deletion;
    CancellationTokenSource? operation;
    CancellationTokenSource? searchOperation;
    readonly CancellationTokenSource lifetime = new();
    readonly BrowserCacheProvider browserCache = new();
    BrowserCachePreview? browserPreview;
    int browserAge;
    SearchFilter current = new(); int page; long total; string sort = "name"; bool descending;
    CleanupPreview? cleanupPreview; int previewAge; BinPreview? binPreview;
    /// <summary>Connects the window to production services or explicit isolated fixtures, with silent audio by default for tests.</summary>
    public MainWindow() : this(new MainViewModel(), null, true) { }
    // Allows presentation tests with synthetic data and a temporary database, without scanning user disks.
    /// <summary>Connects the window to production services or explicit isolated fixtures, with silent audio by default for tests.</summary>
    public MainWindow(MainViewModel model, IndexStore? index, bool connectToMachine, IndexStore? browseCache = null, SearchIndexSession? session = null, Action? onSearchCompleted = null, Action<IReadOnlyList<ProtectedTarget>>? onProtectedDeletion = null, AdvancedResultStore? advancedCache = null, Func<bool>? onCloseWithProcesses = null)
    {
        vm = model; persistSettings = connectToMachine;
        sound = connectToMachine ? new CompletionSound() : CompletionSound.Silent();
        completionSound = onSearchCompleted == null ? sound.Play : _ => onSearchCompleted();
        confirmCloseWithProcesses = onCloseWithProcesses ?? (() => new CloseProcessesWindow(this).ShowDialog() == true);
        protectionNotice = items => { if (closeRequested) return; if (onProtectedDeletion != null) onProtectedDeletion(items); else new ProtectionNoticeWindow(this, items).ShowDialog(); };
        advancedResults = advancedCache;
        InitializeComponent(); DataContext = vm;
        Title = AppBuild.Title;
        Closed += (_, _) => { sound.Dispose(); AudioDiagnostics.Record("window-closed"); };
        store = index ?? new(System.IO.Path.Combine(Settings.DataFolder, "index.db")); scanner = new(store);
        browser = new(browseCache ?? new IndexStore(Path.Combine(Settings.DataFolder, "browser.db")));
        searchSession = session ?? new(store);
        deletion = new(store, volumeService, new SafetyPolicy(tempRoots: cleanup.Roots), new WindowsDeleteBackend());
        vm.Exclusions = string.Join(Environment.NewLine, vm.Settings.Exclusions); ApplyTheme();
        ConfigureWarningViews();
        if (connectToMachine) Loaded += async (_, _) => { await Run(async token => { await RefreshVolumesAsync(); store.Prune(vm.Settings.RetentionDays); RefreshLogs(); vm.Status = "Selecciona un disco para explorar o abre una carpeta. Buscar utiliza el índice de tus análisis."; }); await RefreshForgottenAsync(false); };
    }
    /// <summary>Tracks one foreground operation, enforces operation-lane guards, and reports cancellation or failure safely.</summary>
    async Task Run(Func<CancellationToken, Task> action, bool allowDuringSearch = false)
    {
        if (closeRequested || resourcesClosed) return;
        if (vm.Busy || ((vm.SearchInProgress || vm.AdvancedBusy) && !allowDuringSearch)) { vm.Status = "Espera o para el análisis antes de modificar archivos o reconstruir el índice. Puedes seguir explorando y calcular tamaños."; return; }
        using var process = TrackProcess();
        vm.Busy = true; operation = new();
        try { await action(operation.Token); }
        catch (OperationCanceledException) { vm.Status = "Operación cancelada. Las acciones ya completadas se conservan."; }
        catch (Exception e) { vm.Status = "No se pudo completar: " + e.Message; }
        finally { vm.Busy = false; operation.Dispose(); operation = null; }
    }
    /// <summary>Merges current device detection with remembered indexed volumes on background workers.</summary>
    async Task RefreshVolumesAsync()
    {
        var live = await Task.Run(() => volumeService.Detect());
        var all = await Task.Run(() => { var saved = store.Volumes(); var merged = VolumeService.Merge(live, saved, store.IndexedVolumes()); store.RememberVolumes(live.Where(v => v.Available)); return merged; });
        var selectedId = vm.SelectedVolume?.Id;
        vm.Volumes.Clear(); foreach (var v in all) vm.Volumes.Add(v);
        vm.SelectedVolume = all.FirstOrDefault(v => v.Id == selectedId);
    }
    /// <summary>Refreshes the bounded history and scan-summary collections from local storage.</summary>
    void RefreshLogs() { vm.Scans.Clear(); foreach (var s in store.ScanHistory()) vm.Scans.Add(s); vm.History.Clear(); foreach (var s in store.History()) vm.History.Add(s); }
    /// <summary>Refreshes detected devices without rebuilding their file indexes.</summary>
    async void RefreshDrives(object s, RoutedEventArgs e) => await Run(async _ => { await RefreshVolumesAsync(); vm.Status = "Unidades actualizadas."; });
    /// <summary>Prompts for explicit scan roots and starts only the user-approved analysis scopes.</summary>
    async void ChooseFolder(object s, RoutedEventArgs e)
    {
        if (vm.Busy) return; var dialog = new OpenFolderDialog { Title = "Selecciona las carpetas que deseas analizar", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await ScanRoots(dialog.FolderNames);
    }
    /// <summary>Starts analysis for the selected available drive cards.</summary>
    async void ScanSelected(object s, RoutedEventArgs e) => await ScanRoots(DriveList.SelectedItems.Cast<Volume>().Where(v => v.Available).Select(v => v.Root).ToArray());
    /// <summary>Runs the selected root scan through the tracked foreground-operation lane.</summary>
    async Task ScanRoots(string[] roots) => await Run(token => ScanRootsCore(roots, token));
    /// <summary>Rebuilds collapsed scan scopes in batches and emits a single completion notification for the whole batch.</summary>
    async Task ScanRootsCore(string[] roots, CancellationToken token)
    {
        SaveConfiguration(); if (roots.Length == 0) { vm.Status = "Selecciona al menos una unidad disponible."; return; }
        var notice = BeginProcess(0); searchSession.Reset();
        var collapsed = Paths.Collapse(roots);
        foreach (var root in collapsed)
        {
            if (token.IsCancellationRequested) break; var volume = volumeService.ForPath(root); store.RememberVolumes([volume]);
            var progress = new Progress<ScanProgress>(p => vm.Status = $"Analizando · {p.Processed:N0} elementos · {p.Skipped:N0} omitidos · {p.Elapsed:hh\\:mm\\:ss} · {p.Current}");
            var hidden = vm.Settings.IncludeHidden; var excluded = vm.Settings.Exclusions.Append(Settings.DataFolder).Append(AppContext.BaseDirectory).ToArray();
            var report = root == collapsed[^1] ? await notice.Watch(() => scanner.ScanAsync(root, volume.Id, hidden, excluded, progress, token), token, r => !r.Cancelled)
                : await scanner.ScanAsync(root, volume.Id, hidden, excluded, progress, token);
            vm.Status = $"{(report.Cancelled ? "Cancelado: índice parcial" : "Análisis finalizado")} · {report.Processed:N0} elementos · {report.Skipped:N0} omitidos (ocultos, enlaces, exclusiones o errores). Alcance: {report.Root}";
        }
        await RefreshVolumesAsync(); RefreshLogs();
        if (browsePath.Length > 0 && !token.IsCancellationRequested) { var summary = vm.Status; await LoadBrowseAsync(browsePath, token, browseVolume); vm.Status = summary; }
        if (!token.IsCancellationRequested) CompleteProcess(0);
    }
    /// <summary>Combines the editable search fields with the selected volume identities.</summary>
    SearchFilter ReadFilter() => vm.Filter(SearchDrives.SelectedItems.Cast<Volume>().Select(v => v.Id));
    /// <summary>Tracks one explicit quick search or disk refresh and rejects overlapping search requests.</summary>
    public async Task SearchCurrentAsync(bool refreshDisks = false)
    {
        if (closeRequested || resourcesClosed) return;
        if (vm.Busy || searchOperation != null || (refreshDisks && vm.AdvancedBusy)) { vm.Status = "Ya hay una operación en curso; puedes explorar mientras buscas."; return; }
        using var process = TrackProcess();
        using var cancellation = new CancellationTokenSource(); searchOperation = cancellation;
        try { await SearchAsync(cancellation.Token, refreshDisks); }
        catch (Exception e) { vm.SearchStateText = "La búsqueda no pudo completarse"; SearchStatus("No se pudo completar: " + e.Message); }
        finally { searchOperation = null; }
    }
    /// <summary>Updates search-specific status without overwriting an unrelated foreground operation.</summary>
    void SearchStatus(string text) { vm.SearchActivity = text; if (vm.Section == 1 && !vm.Busy) vm.Status = text; }
    /// <summary>Loads an immediate folder snapshot in the browsing lane while allowing an independent search to continue.</summary>
    public Task ExploreFolderAsync(string path) => Run(token => LoadBrowseAsync(path, token), true);
    /// <summary>Starts the explicitly requested indexed search.</summary>
    async void Search(object s, RoutedEventArgs e) => await SearchCurrentAsync();
    /// <summary>Starts the explicitly requested disk-scope reconciliation followed by a search.</summary>
    async void SearchDisks(object s, RoutedEventArgs e) => await SearchCurrentAsync(true);
    /// <summary>Coordinates provisional results, real progress, cancellation, final querying, and one completion signal.</summary>
    async Task SearchAsync(CancellationToken token, bool refreshDisks)
    {
        var notice = BeginProcess(1);
        current = ReadFilter(); page = 0; vm.ResultSelected = null; vm.Results.Clear(); vm.SearchEmpty = true;
        vm.SearchCoverage = refreshDisks ? "Revisando archivos nuevos y movidos en el alcance elegido…" : "Consulta rápida del índice guardado; no se recorren los discos.";
        vm.SearchCoverageDetails = "La cobertura se actualizará al terminar la revisión actual.";
        vm.Notify(nameof(vm.SearchCoverage)); vm.Notify(nameof(vm.SearchCoverageDetails));
        vm.SearchStopRequested = false; vm.SearchInProgress = true; vm.SearchStateText = refreshDisks ? "Revisando discos · preparando ubicaciones…" : "Buscando en el índice…";
        searchEstimator = new(); vm.SearchEtaText = refreshDisks ? "Restante estimado: calculando…" : "Consultando el índice; sin recorrido del disco";
        vm.EmptyTitle = "Búsqueda en curso"; vm.EmptyHint = "Los resultados son provisionales mientras se prepara el índice. Cero coincidencias todavía no significa que el archivo no exista.";
        vm.Notify(nameof(vm.EmptyTitle)); vm.Notify(nameof(vm.EmptyHint)); UpdateChips();
        var elapsed = Stopwatch.StartNew(); Task preview = Task.CompletedTask; double nextPreview = 1;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            if (vm.SearchStopRequested) return;
            vm.SearchStateText = $"{(refreshDisks ? "Revisando discos" : "Consultando índice")} · {elapsed.Elapsed:hh\\:mm\\:ss}";
            if (refreshDisks) vm.SearchEtaText = searchEstimator.RemainingText(elapsed.Elapsed);
            if (refreshDisks && preview.IsCompleted && !token.IsCancellationRequested && elapsed.Elapsed.TotalSeconds >= nextPreview) { nextPreview = elapsed.Elapsed.TotalSeconds + 3; preview = PreviewSearch(token); }
        };
        timer.Start();
        try
        {
            if (refreshDisks)
            {
                await Query(token, preparing: true);
                await RefreshSearchLocations(token);
                timer.Stop(); await preview; token.ThrowIfCancellationRequested();
            }
            else
            {
                vm.SearchCoverage = "Índice guardado: puede no incluir archivos nuevos o movidos. Usa Buscar en discos o Revisar carpeta para actualizarlos.";
                vm.SearchCoverageDetails = "Buscar consulta únicamente SQLite. Buscar en discos revisa el alcance del selector y los filtros. Revisar carpeta permite elegir una ubicación concreta. Si no se completó una revisión, la cobertura es parcial.";
                vm.Notify(nameof(vm.SearchCoverage)); vm.Notify(nameof(vm.SearchCoverageDetails));
            }
            await Query(token, completion: notice);
            if (!refreshDisks)
            {
                vm.EmptyTitle = "No hay coincidencias en el índice guardado";
                vm.EmptyHint = "¿El archivo es nuevo o lo has movido? Pulsa Revisar carpeta para elegir dónde buscar, o Buscar en discos para revisar el alcance del selector. El índice puede estar incompleto.";
                vm.Notify(nameof(vm.EmptyTitle)); vm.Notify(nameof(vm.EmptyHint));
            }
            vm.SearchStateText = $"Búsqueda terminada{(refreshDisks ? " · discos revisados" : " · índice guardado")} · {elapsed.Elapsed:hh\\:mm\\:ss} · {total:N0} {(total == 1 ? "coincidencia" : "coincidencias")}";
            vm.SearchEtaText = "Restante estimado: 0 s";
            CompleteProcess(1);
        }
        catch (OperationCanceledException)
        {
            timer.Stop(); await preview;
            searchSession.Reset(); // Stopping also releases watchers and their queues without clearing results.
            vm.SearchStateText = $"Búsqueda detenida · {vm.Results.Count:N0} filas conservadas";
            vm.SearchEtaText = "Detenida";
            vm.EmptyTitle = "Búsqueda detenida"; vm.EmptyHint = "No se terminó de revisar el alcance. Se conservan los resultados ya encontrados y el índice parcial.";
            vm.SearchCoverage = "Revisión detenida por el usuario. Los resultados son parciales y se han liberado los recursos de vigilancia.";
            vm.Notify(nameof(vm.EmptyTitle)); vm.Notify(nameof(vm.EmptyHint)); vm.Notify(nameof(vm.SearchCoverage));
            vm.SearchActivity = "Búsqueda detenida. Puedes usar los resultados conservados o iniciar otra búsqueda.";
        }
        catch
        {
            timer.Stop(); await preview;
            vm.SearchStateText = "La búsqueda no pudo completarse";
            vm.EmptyTitle = "No se pudo completar la búsqueda"; vm.EmptyHint = "Consulta el aviso inferior. No se puede concluir que el archivo no exista.";
            vm.Notify(nameof(vm.EmptyTitle)); vm.Notify(nameof(vm.EmptyHint)); throw;
        }
        finally { timer.Stop(); vm.SearchInProgress = false; searchEstimator = null; }
    }
    /// <summary>Refreshes provisional indexed results without treating them as final or playing completion audio.</summary>
    async Task PreviewSearch(CancellationToken token)
    {
        try { await Query(token, preparing: true); }
        catch (OperationCanceledException) { }
        catch (Exception e) { vm.SearchActivity = "La consulta provisional no está disponible; la preparación sigue en curso. " + e.Message; }
    }
    /// <summary>Plans and reconciles the requested connected volume/folder scopes before searching.</summary>
    async Task RefreshSearchLocations(CancellationToken token)
    {
        vm.SearchActivity = "Actualizando las ubicaciones de búsqueda…";
        var unavailable = new List<string>();
        var roots = await Task.Run(() =>
        {
            token.ThrowIfCancellationRequested(); var live = volumeService.Detect();
            foreach (var v in live.Where(v => !v.Available && v.Connection != VolumeConnection.NoMedia && (current.Volumes.Length == 0 || current.Volumes.Contains(v.Id)))) unavailable.Add(v.Root + ": " + v.State);
            store.RememberVolumes(live.Where(v => v.Available));
            return SearchScopePlanner.Plan(current, live);
        }, token);
        var reports = await searchSession.RefreshAsync(roots, vm.Settings.IncludeHidden, vm.Settings.Exclusions.Append(Settings.DataFolder).Append(AppContext.BaseDirectory), token,
            new Progress<string>(path => { if (!token.IsCancellationRequested && vm.SearchInProgress) vm.SearchActivity = "Preparando búsqueda y reconciliando cambios · " + path; }),
            new Progress<SearchPreparationProgress>(p => { if (!token.IsCancellationRequested && vm.SearchInProgress) searchEstimator?.Observe(p); }));
        int completed = reports.Count(r => r.Completed); int failed = reports.Count(r => !r.Completed) + unavailable.Count;
        vm.SearchCoverage = completed > 0
            ? $"Índice preparado en {completed} ubicaciones; cambios reconciliados y profundidad según filtros. Resto: último análisis."
            : "Solo índice del último análisis. No se ha actualizado ninguna ubicación dentro de los discos y filtros elegidos.";
        if (failed > 0) vm.SearchCoverage += $" No se pudieron actualizar {failed} ubicaciones.";
        if (reports.Sum(r => r.Skipped) > 0) vm.SearchCoverage += $" {reports.Sum(r => r.Skipped):N0} elementos omitidos.";
        vm.SearchCoverageDetails = string.Join(Environment.NewLine, reports.Select(r => $"{r.Path}: {r.Detail} · {r.Updated:N0} actualizados · {r.Skipped:N0} omitidos").Concat(unavailable));
        if (vm.SearchCoverageDetails.Length == 0) vm.SearchCoverageDetails = "Actualiza una carpeta para incluir sus archivos y descendientes. Las unidades desconectadas conservan solo los datos del índice.";
        vm.Notify(nameof(vm.SearchCoverage)); vm.Notify(nameof(vm.SearchCoverageDetails));
    }
    /// <summary>Lets the user explicitly rebuild a chosen folder scope and then query its descendants.</summary>
    async void UpdateSearchFolder(object s, RoutedEventArgs e)
    {
        if (vm.Busy) return;
        var picker = new OpenFolderDialog { Title = "Actualizar una carpeta y buscar en ella" };
        if (vm.Folder.Length > 0 && Directory.Exists(vm.Folder)) picker.InitialDirectory = vm.Folder;
        if (picker.ShowDialog(this) != true) return;
        await Run(async token =>
        {
            var notice = BeginProcess(1); SaveConfiguration(); var root = Paths.Normalize(picker.FolderName); var volume = volumeService.ForPath(root);
            searchSession.Reset();
            var progress = new Progress<ScanProgress>(p => vm.Status = $"Actualizando búsqueda · {p.Processed:N0} elementos · {p.Skipped:N0} omitidos · {p.Current}");
            store.RememberVolumes([volume]);
            var report = await scanner.ScanAsync(root, volume.Id, vm.Settings.IncludeHidden, vm.Settings.Exclusions.Append(Settings.DataFolder).Append(AppContext.BaseDirectory), progress, token);
            RefreshLogs(); if (report.Cancelled) throw new OperationCanceledException(token);
            vm.Folder = root; vm.Recursive = true; vm.NotifyFilterFields(); SelectSearchVolumes([]);
            current = ReadFilter(); page = 0;
            vm.SearchCoverage = $"Carpeta y subcarpetas actualizadas: {root}. {report.Skipped:N0} omisiones.";
            vm.SearchCoverageDetails = "La actualización recursiva terminó para esta carpeta. Las omisiones de permisos, enlaces y exclusiones se mantienen registradas.";
            vm.Notify(nameof(vm.SearchCoverage)); vm.Notify(nameof(vm.SearchCoverageDetails)); await Query(token, completion: notice); CompleteProcess(1);
        });
    }
    /// <summary>Loads one indexed result page, checks current risks/availability, and updates filters and coverage labels.</summary>
    async Task Query(CancellationToken token, bool preparing = false, CompletionSignal? completion = null)
    {
        vm.SearchActivity = "Consultando el índice…";
        var result = await Task.Run(() => store.Search(current, sort, descending, page), token); total = result.Total;
        Func<Entry[]> inspect = () =>
        {
            var live = volumeService.Detect();
            return result.Items.Select(e =>
            {
                token.ThrowIfCancellationRequested();
                bool online = live.Any(v => v.Available && v.Id == e.VolumeId && Paths.Within(e.Path, v.Root));
                bool exists = online && (File.Exists(e.Path) || Directory.Exists(e.Path));
                return e with { Availability = !online ? "Unidad no disponible" : exists ? "Disponible al consultar" : "Ya no existe o acceso denegado", Risk = riskInspector.Inspect(e, online) };
            }).ToArray();
        };
        var items = await (completion == null ? Task.Run(inspect, token) : completion.Run(inspect, token));
        token.ThrowIfCancellationRequested();
        vm.Results.Clear(); foreach (var item in items) vm.Results.Add(item);
        vm.SearchEmpty = !items.Any(e => !vm.HideWarnings || !e.HasWarning);
        vm.EmptyTitle = preparing ? "Búsqueda en curso" : "No hay coincidencias en el alcance consultado";
        vm.EmptyHint = preparing ? "El índice se está preparando. Aquí aparecerán los resultados disponibles, sin esperar a que termine toda la revisión." : "Comprueba las etiquetas de carpeta, disco, extensiones y nombre exacto. Consulta la cobertura y sus omisiones: cero resultados no garantiza que el archivo no exista fuera del alcance revisado.";
        vm.Notify(nameof(vm.EmptyTitle)); vm.Notify(nameof(vm.EmptyHint));
        vm.ResultCount = $"{total:N0} {(total == 1 ? "resultado" : "resultados")}{(preparing ? " provisionales" : "")} · página {page + 1} de {Math.Max(1, (total + 199) / 200)}"; vm.Notify(nameof(vm.ResultCount));
        var active = new List<string>(); if (current.Name != "") active.Add("Nombre: " + current.Name); if (current.Extensions.Length > 0) active.Add("Extensiones: " + string.Join(" O ", current.Extensions)); if (current.IsDirectory.HasValue) active.Add(current.IsDirectory.Value ? "Carpetas" : "Archivos"); if (current.MinSize.HasValue) active.Add("≥ " + Format.Bytes(current.MinSize.Value)); if (current.MaxSize.HasValue) active.Add("≤ " + Format.Bytes(current.MaxSize.Value)); if (current.Folder != "") active.Add(current.Folder + (current.Recursive ? " y subcarpetas" : " sin subcarpetas")); if (current.Volumes.Length > 0) active.Add(current.Volumes.Length + " unidades (OR)"); if (current.ModifiedFrom.HasValue || current.ModifiedTo.HasValue) active.Add("Fecha de modificación"); if (current.CreatedFrom.HasValue || current.CreatedTo.HasValue) active.Add("Fecha de creación"); if (current.Hidden.HasValue) active.Add("Ocultos: " + current.Hidden); if (current.System.HasValue) active.Add("Sistema: " + current.System); if (current.Exact) active.Add("Nombre exacto");
        vm.ActiveFilters = active.Count == 0 ? "Sin filtros · todas las unidades indexadas" : string.Join("  Y  ", active); vm.Notify(nameof(vm.ActiveFilters));
        UpdateChips();
        vm.SearchActivity = preparing ? "Búsqueda en curso. Los resultados mostrados todavía son provisionales." : total == 0 ? "Búsqueda terminada sin coincidencias en el alcance consultado. Comprueba la cobertura y los filtros." : "Consulta terminada. La disponibilidad se comprueba de nuevo antes de cada operación.";
    }
    /// <summary>Loads the previous search-results page without rescanning disks.</summary>
    async void PreviousPage(object s, RoutedEventArgs e) => await Run(async token => { if (page > 0) { page--; await Query(token); } });
    /// <summary>Loads the next bounded search-results page when indexed results remain.</summary>
    async void NextPage(object s, RoutedEventArgs e) => await Run(async token => { if ((page + 1L) * 200 < total) { page++; await Query(token); } });
    /// <summary>Changes the global SQL sort order and returns to the first result page.</summary>
    async void ResultSort(object s, DataGridSortingEventArgs e) { e.Handled = true; await Run(async token => { if (sort == e.Column.SortMemberPath) descending = !descending; else { sort = e.Column.SortMemberPath; descending = false; } foreach (var c in Results.Columns) c.SortDirection = null; e.Column.SortDirection = descending ? ListSortDirection.Descending : ListSortDirection.Ascending; page = 0; await Query(token); }); }
    /// <summary>Treats Enter in the search editor as an explicit search request.</summary>
    void SearchKey(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; Search(s, e); } }
    /// <summary>Clears search filters and explicitly queries the resulting scope.</summary>
    void Reset(object s, RoutedEventArgs e) { if (vm.Busy) return; vm.Load(new()); SelectSearchVolumes([]); Search(s, e); }
    /// <summary>Saves the current filter as a local named favorite.</summary>
    void SaveFavorite(object s, RoutedEventArgs e) { try { var f = ReadFilter(); vm.Settings.Favorites.RemoveAll(x => x.Name == vm.FavoriteName); vm.Settings.Favorites.Add(new(vm.FavoriteName, f)); vm.Settings.Save(); Favorites.Items.Refresh(); vm.Status = "Búsqueda guardada."; } catch (Exception ex) { vm.Status = ex.Message; } }
    /// <summary>Restores and explicitly executes the selected local favorite.</summary>
    void LoadFavorite(object s, RoutedEventArgs e) { if (Favorites.SelectedItem is not Favorite f || vm.Busy) return; vm.Load(f.Filter); SelectSearchVolumes(f.Filter.Volumes); FavoritePopup.IsOpen = false; Search(s, e); }
    /// <summary>Returns the selected entries from the currently active browser or search table.</summary>
    Entry[] Selection() => (vm.Section == 0 ? BrowserResults : Results).SelectedItems.Cast<Entry>().ToArray();
    /// <summary>Copies selected filesystem paths, one per line, without executing them.</summary>
    void CopyPaths(object s, RoutedEventArgs e) { try { var a = Selection(); if (a.Length > 0) { Clipboard.SetText(string.Join(Environment.NewLine, a.Select(i => i.Path))); vm.Status = $"{a.Length} rutas copiadas."; } } catch (Exception ex) { vm.Status = ex.Message; } }
    /// <summary>Rejects an operation if the selected item or its expected volume is no longer available.</summary>
    void EnsureAvailable(Entry entry) { if (!volumeService.Matches(entry.Path, entry.VolumeId) || (!File.Exists(entry.Path) && !Directory.Exists(entry.Path))) throw new IOException("El elemento ya no está disponible."); Paths.NoReparseAncestors(entry.Path); }
    /// <summary>Reveals the selected available item using the Explorer shell API wrapper.</summary>
    void ShowExplorer(object s, RoutedEventArgs e) { try { if (vm.Selected is not { } item) return; EnsureAvailable(item); Explorer.Select(item.Path); } catch (Exception ex) { vm.Status = ex.Message; } }
    /// <summary>Handles a primary-button double click by navigating a folder or showing file details, never deleting.</summary>
    async void ResultDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is not DependencyObject source || FindRow(source)?.Item is not Entry item) return;
        e.Handled = true;
        await OpenSearchResultAsync(item);
    }
    /// <summary>Opens a result folder in Disks while preserving independent search state.</summary>
    public Task OpenSearchResultAsync(Entry item)
    {
        if (!item.IsDirectory) { vm.Status = "Los detalles del archivo aparecen debajo de la tabla."; return Task.CompletedTask; }
        return Run(async token =>
        {
            vm.Section = 0;
            await LoadBrowseAsync(item.Path, token, item.VolumeId);
        }, true);
    }
    /// <summary>Moves the search-folder filter to its parent and explicitly searches that scope.</summary>
    void ParentFolder(object s, RoutedEventArgs e) { if (vm.Busy || string.IsNullOrEmpty(vm.Folder)) return; vm.Folder = System.IO.Path.GetDirectoryName(vm.Folder) ?? ""; vm.Notify(nameof(vm.Folder)); Search(s, e); }
    /// <summary>Routes keyboard actions through safe navigation or the shared deletion review instead of direct deletion.</summary>
    async void ResultKey(object s, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) { e.Handled = true; DeleteSelected(s, e); }
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; CopyPaths(s, e); }
        else if (e.Key == Key.Enter && vm.Selected is { IsDirectory: true } item) { e.Handled = true; await OpenSearchResultAsync(item); }
        else if (e.Key == Key.Back && vm.Section == 0) { e.Handled = true; BrowseParent(s, e); }
    }
    /// <summary>Starts an explicit logical folder-size calculation for the selected folder.</summary>
    async void FolderSize(object s, RoutedEventArgs e) => await CalculateSelectedFolderAsync();
    /// <summary>Computes logical size in a cancellable worker and updates cached presentation metadata.</summary>
    public Task CalculateSelectedFolderAsync() => Run(async token =>
    {
        if (vm.Selected is not { IsDirectory: true } item) { vm.Status = "Selecciona una carpeta para calcular la suma de sus archivos. Los archivos ya muestran su tamaño lógico en la tabla."; return; }
        var owner = vm.Section; var notice = BeginProcess(owner);
        EnsureAvailable(item); vm.Status = "Calculando el contenido de la carpeta…";
        var r = await notice.Watch(() => Scanner.FolderSize(item.Path, token), token);
        var measured = item with { FolderLogicalSize = r.Size, FolderSizePartial = r.Skipped > 0, FolderSizeDetail = $"Calculado {DateTime.Now:g}: {r.Files:N0} archivos, {r.Skipped:N0} omitidos. Tamaño lógico; no equivale al espacio recuperable y puede cambiar fuera de la aplicación." };
        measuredFolders[item.Path] = measured;
        foreach (var rows in new[] { vm.BrowserItems, vm.Results })
            for (int i = 0; i < rows.Count; i++) if (rows[i].Path.Equals(item.Path, StringComparison.OrdinalIgnoreCase)) rows[i] = measured;
        if (vm.Selected?.Path == item.Path) vm.Selected = measured;
        vm.FolderSizeText = $"{Format.Bytes(r.Size)} lógicos · {r.Files:N0} archivos · {r.Skipped:N0} omitidos";
        vm.Notify(nameof(vm.FolderSizeText)); vm.Status = "Tamaño calculado y actualizado en la fila de la carpeta.";
        token.ThrowIfCancellationRequested(); CompleteProcess(owner);
    }, true);
    /// <summary>Routes the current visible selection into the shared deletion request workflow.</summary>
    async void DeleteSelected(object s, RoutedEventArgs e) => await RequestDeletionAsync(Selection());
    /// <summary>Rechecks selected volume identities before preparing and presenting the common deletion review.</summary>
    public Task RequestDeletionAsync(IEnumerable<Entry> selection) => Run(async token =>
    {
        var rows = selection.ToArray();
        await Task.Run(() =>
        {
            var live = volumeService.Detect();
            if (rows.Any(row => !live.Any(v => v.Available && v.Id == row.VolumeId && Paths.Within(row.Path, v.Root)))) throw new IOException("La unidad no está disponible o ya no coincide con la del resultado. No se ha autorizado el borrado.");
        }, token);
        await DeleteFlow(rows.Select(i => i.Path), false, token);
        var summary = vm.Status;
        if (vm.Section == 0 && browsePath != "") await LoadBrowseAsync(browsePath, CancellationToken.None, browseVolume); else await Query(CancellationToken.None);
        vm.Status = summary;
    });
    /// <summary>Performs review, retained-target revalidation, separate final confirmation, execution, and per-target reporting.</summary>
    async Task<IReadOnlyList<OperationResult>?> DeleteFlow(IEnumerable<string> paths, bool temporary, CancellationToken token, DeletionService? service = null, Func<IReadOnlyList<string>, CancellationToken, Task<IDisposable>>? acquireLease = null, bool requirePermanent = false)
    {
        var selectedPaths = paths.ToArray();
        if (selectedPaths.Length == 0) { vm.Status = "No hay elementos visibles seleccionados para revisar."; return null; }
        try
        {
            var fileService = service ?? deletion;
            vm.Status = "Preparando revisión de seguridad…";
            var review = await fileService.PrepareAsync(selectedPaths, DeleteMode.Recycle, temporary, token); token.ThrowIfCancellationRequested(); var dialog = new ReviewWindow(this, review, requirePermanent); if (dialog.ShowDialog() != true) { vm.Status = "Revisión cancelada. No se eliminó nada."; return null; }
            var retained = dialog.Retained; var mode = dialog.Mode;
            // Changing the mode or removing targets creates a separate plan and never alters a confirmed plan.
            var finalReview = await fileService.PrepareAsync(retained, mode, temporary, token);
            token.ThrowIfCancellationRequested();
            foreach (var i in finalReview.Items) { var old = review.Items.Single(x => x.Path == i.Path); if (old.Identity != i.Identity || old.TreeStamp != i.TreeStamp) throw new IOException("La selección cambió durante la revisión. Inicia de nuevo."); }
            fileService.CompleteReview(finalReview, retained);
            var final = new FinalWindow(this, $"{retained.Length:N0} elementos principales · {Format.Bytes(finalReview.Items.Sum(i => i.Size))} lógicos estimados.\n" + (mode == DeleteMode.Recycle ? "Si no se puede usar la Papelera, la operación se detendrá." : "No se podrá restaurar desde la Papelera."), mode == DeleteMode.Permanent);
            if (final.ShowDialog() != true) { vm.Status = "Confirmación cancelada. No se eliminó nada."; return null; }
            var permit = fileService.Confirm(finalReview, true, final.Phrase);
            using var retentionLease = acquireLease == null ? null : await acquireLease(retained, token);
            var results = await fileService.ExecuteAsync(permit, temporary, token, new Progress<OperationResult>(r => vm.Status = $"{r.Status} · {r.Path} · {r.Detail}"));
            var blocked = results.Where(r => r.ProtectionPath != null).Select(r => new ProtectedTarget(r.ProtectionPath!, r.Detail)).ToArray();
            if (blocked.Length > 0) protectionNotice(blocked);
            vm.Status = $"Operación terminada: {results.Count(r => r.Status == "Eliminado")} eliminados, {results.Count(r => r.Status == "Omitido")} omitidos, {results.Count(r => r.Status == "Error")} errores. Consulta el historial para ver cada resultado."; RefreshLogs();
            return results;
        }
        catch (ProtectedDeletionException ex)
        {
            protectionNotice([new(ex.TargetPath, ex.Message)]);
            vm.Status = "Eliminación bloqueada. No se eliminó ningún elemento de esta selección; puedes copiar las rutas del aviso.";
        }
        catch (UnauthorizedAccessException)
        {
            protectionNotice(selectedPaths.Select(p => new ProtectedTarget(p, "Acceso denegado. No se solicitarán derechos de administrador.")).ToArray());
            vm.Status = "Eliminación bloqueada por permisos. Revisa los elementos manualmente.";
        }
        return null;
    }
    /// <summary>Previews selected temporary categories and risk-checks their bounded candidate list.</summary>
    async void AnalyzeCleanup(object s, RoutedEventArgs e) => await Run(async token =>
    {
        var notice = BeginProcess(2);
        cleanupPreview = null; vm.CleanupPaths.Clear(); previewAge = Math.Clamp(vm.Settings.AgeDays, 1, 3650); vm.Status = "Analizando temporales; los archivos en uso se omiten…";
        var preview = await cleanup.AnalyzeAsync(UserTemp.IsChecked == true, WindowsTemp.IsChecked == true, previewAge, token); cleanupPreview = preview;
        foreach (var entry in await notice.Watch(() => InspectPreviewAsync(preview.Paths, cleanup.Roots, token), token)) vm.CleanupPaths.Add(entry); vm.CleanupSummary = $"{preview.Paths.Count:N0} archivos · {Format.Bytes(preview.Size)} estimados · {preview.Skipped:N0} omitidos · antigüedad ≥ {previewAge} días" + (preview.Truncated ? " · Vista limitada a 20.000 candidatos; repite el análisis después." : ""); vm.Notify(nameof(vm.CleanupSummary)); vm.Status = "Vista previa preparada. Revisa antes de limpiar.";
        token.ThrowIfCancellationRequested(); CompleteProcess(2);
    }, true);
    /// <summary>Previews supported closed-browser HTTP caches and prepares authorized candidate metadata.</summary>
    async void AnalyzeBrowserCache(object s, RoutedEventArgs e) => await Run(async token =>
    {
        var notice = BeginProcess(2);
        browserPreview = null; vm.BrowserCachePaths.Clear(); browserAge = Math.Clamp(vm.Settings.AgeDays, 1, 3650);
        var preview = await browserCache.AnalyzeAsync(browserAge, token); browserPreview = preview;
        foreach (var entry in await notice.Watch(() => InspectPreviewAsync(preview.Files.Paths, preview.Targets.Select(t => t.Root), token), token)) vm.BrowserCachePaths.Add(entry);
        vm.BrowserCacheSummary = $"{preview.Files.Paths.Count:N0} archivos · {Format.Bytes(preview.Files.Size)} estimados · {preview.Files.Skipped:N0} omitidos · antigüedad ≥ {browserAge} días. " + preview.Detail
            + (preview.Files.Truncated ? " Vista limitada a 20.000 archivos." : ""); vm.Notify(nameof(vm.BrowserCacheSummary));
        token.ThrowIfCancellationRequested(); CompleteProcess(2);
    }, true);
    /// <summary>Revalidates browser state and file age before reviewing only the visible authorized cache targets.</summary>
    async void DeleteBrowserCache(object s, RoutedEventArgs e) => await Run(async token =>
    {
        if (browserPreview is not { Files.Paths.Count: > 0 } preview) { vm.Status = "Analiza primero la caché con el navegador cerrado."; return; }
        var cutoff = DateTime.UtcNow.AddDays(-browserAge);
        foreach (var target in preview.Targets) if (BrowserCacheProvider.ProcessRunning(target.Process)) throw new IOException("El navegador está abierto. Ciérralo y analiza de nuevo.");
        foreach (var path in vm.BrowserCachePaths.Where(e => !vm.HideWarnings || !e.HasWarning).Select(e => e.Path)) { var info = new FileInfo(path); if (info.LastWriteTimeUtc > cutoff || info.CreationTimeUtc > cutoff) throw new IOException("La caché cambió; analiza de nuevo."); }
        var service = new DeletionService(store, volumeService, new SafetyPolicy(tempRoots: preview.Targets.Select(t => t.Root)), new BrowserCacheBackend(preview.Targets, new WindowsDeleteBackend()));
        await DeleteFlow(vm.BrowserCachePaths.Where(e => !vm.HideWarnings || !e.HasWarning).Select(e => e.Path), true, token, service);
        browserPreview = null; vm.BrowserCachePaths.Clear(); vm.BrowserCacheSummary = "Analiza de nuevo para actualizar la estimación."; vm.Notify(nameof(vm.BrowserCacheSummary));
    });
    bool forgottenBusy;
    int forgottenGeneration;
    /// <summary>Reviews bounded indexed old-file candidates, suppressing stale generations and automatic-startup audio.</summary>
    public async Task RefreshForgottenAsync(bool notifyCompletion = true)
    {
        if (closeRequested || resourcesClosed) return;
        using var process = TrackProcess();
        if (forgottenBusy) return; forgottenBusy = true; var generation = forgottenGeneration; vm.ForgottenSummary = "Revisando archivos antiguos del índice…"; var notice = notifyCompletion ? BeginProcess(2) : null;
        try
        {
            var days = Math.Clamp(vm.Settings.ForgottenDays, 30, 3650);
            var preview = await new ForgottenFileService(store).ReviewAsync(days, vm.Settings.Exclusions.Concat(cleanup.Roots), lifetime.Token);
            if (generation != forgottenGeneration) return;
            Func<ForgottenFile[]> inspect = () => preview.Files.Select(file => { lifetime.Token.ThrowIfCancellationRequested(); return file with { File = file.File with { Risk = riskInspector.Inspect(file.File, true) } }; }).ToArray();
            var inspected = await (notice == null ? Task.Run(inspect, lifetime.Token) : notice.Run(inspect, lifetime.Token));
            if (generation != forgottenGeneration) return;
            vm.ForgottenFiles.Clear(); foreach (var file in inspected) vm.ForgottenFiles.Add(file);
            vm.ForgottenSummary = $"Actualizado {preview.CheckedAt:g} · umbral {days} días · {preview.Files.Count:N0} candidatos · {preview.Checked:N0} comprobados · {preview.Skipped:N0} omitidos. "
                + (preview.Limited ? "Vista limitada: hasta 200 candidatos entre 1.000 entradas revisadas. " : "")
                + "Solo archivos indexados. Las fechas de acceso pueden estar desactivadas o retrasadas; no prueban que un archivo no se use.";
            if (notifyCompletion) CompleteProcess(2);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { vm.ForgottenSummary = "No se pudo revisar el índice: " + ex.Message; }
        finally { forgottenBusy = false; }
    }
    /// <summary>Starts an explicit review of old-file candidates from Cleanup.</summary>
    async void RefreshForgotten(object s, RoutedEventArgs e) => await RefreshForgottenAsync();
    /// <summary>Navigates to the parent of the selected old-file candidate without deleting it.</summary>
    async void OpenForgotten(object s, RoutedEventArgs e)
    {
        if (ForgottenResults.SelectedItem is not ForgottenFile file) return;
        vm.Section = 0; await ExploreFolderAsync(file.File.Parent);
    }
    /// <summary>Revalidates candidate ages and sends only visible temporary targets through the common safety review.</summary>
    async void DeleteCleanup(object s, RoutedEventArgs e) => await Run(async token =>
    {
        if (cleanupPreview == null || cleanupPreview.Paths.Count == 0) { vm.Status = "Analiza los temporales primero."; return; }
        var cutoff = DateTime.UtcNow.AddDays(-previewAge); foreach (var p in vm.CleanupPaths.Where(e => !vm.HideWarnings || !e.HasWarning).Select(e => e.Path)) { var info = new FileInfo(p); if (info.LastWriteTimeUtc > cutoff || info.CreationTimeUtc > cutoff) throw new IOException("Un temporal cambió. Analiza de nuevo."); }
        await DeleteFlow(vm.CleanupPaths.Where(e => !vm.HideWarnings || !e.HasWarning).Select(e => e.Path), true, token); cleanupPreview = null; vm.CleanupPaths.Clear(); vm.CleanupSummary = "Analiza de nuevo para actualizar la estimación."; vm.Notify(nameof(vm.CleanupSummary));
    });
    /// <summary>Queries the selected drive's Recycle Bin summary without emptying it.</summary>
    async void AnalyzeBin(object s, RoutedEventArgs e) => await Run(async token => { var notice = BeginProcess(2); binPreview = null; if (BinDrive.SelectedItem is not Volume { Available: true } v) throw new IOException("Selecciona una unidad disponible."); binPreview = await notice.Run(() => RecycleBin.Preview(v), token); vm.BinSummary = $"{binPreview.Root} · TODO el contenido de esta unidad: {binPreview.Count:N0} elementos · {Format.Bytes(binPreview.Size)} estimados."; vm.Notify(nameof(vm.BinSummary)); vm.Status = "Papelera analizada; no se ha eliminado nada."; token.ThrowIfCancellationRequested(); CompleteProcess(2); });
    /// <summary>Requires two separate confirmations before emptying the exact reviewed drive's Recycle Bin.</summary>
    async void EmptyBin(object s, RoutedEventArgs e) => await Run(async _ =>
    {
        if (binPreview == null) throw new IOException("Analiza la Papelera antes de continuar."); var p = binPreview;
        var review = MessageBox.Show(this, $"1 de 2 · Revisión\nVaciar TODO el contenido de la Papelera en {p.Root}\n{p.Count:N0} elementos · {Format.Bytes(p.Size)} estimados.\n\nLa operación es permanente. Abre la Papelera si necesitas inspeccionar o retirar elementos antes de continuar.", "Revisión del alcance", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (review != MessageBoxResult.OK) return;
        var final = new FinalWindow(this, $"Vaciar toda la Papelera de {p.Root}: {p.Count:N0} elementos. La API de Windows opera sobre esta unidad completa.", true); if (final.ShowDialog() != true) return;
        try { await RecycleBin.EmptyAsync(p, true, final.Phrase, volumeService); store.Log(new(p.Root, "Eliminado", "Papelera vaciada permanentemente en la unidad revisada.")); vm.Status = "Papelera vaciada. El espacio recuperado puede diferir de la estimación."; }
        catch (Exception ex) { store.Log(new(p.Root, "Error", ex.Message)); throw; }
        finally { binPreview = null; RefreshLogs(); }
    });
    /// <summary>Opens the Windows Recycle Bin for manual inspection or restoration.</summary>
    void OpenBin(object s, RoutedEventArgs e) { try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "shell:RecycleBinFolder" }); } catch (Exception ex) { vm.Status = ex.Message; } }
    /// <summary>Opens the fixed project-author GitHub profile only after the user clicks the credits link.</summary>
    void OpenCredits(object s, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("https://github.com/DanielCulpian") { UseShellExecute = true }); }
        catch (Exception) { vm.Status = "No se pudo abrir el navegador. Créditos: github.com/DanielCulpian"; }
    }
    /// <summary>Builds the search-column visibility menu without changing stored file metadata.</summary>
    void Columns(object s, RoutedEventArgs e) { var menu = new ContextMenu(); foreach (var c in Results.Columns) { var item = new MenuItem { Header = c.Header, IsCheckable = true, IsChecked = c.Visibility == Visibility.Visible }; item.Click += (_, _) => c.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed; menu.Items.Add(item); } menu.IsOpen = true; }
    /// <summary>Builds explicit filter-removal actions and re-queries only after the user chooses one.</summary>
    void RemoveFilter(object s, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        // Summary: Adds a filter action or visible filter chip to the current presentation collection.
        void Add(string title, Action clear) { var item = new MenuItem { Header = title }; item.Click += (_, _) => { if (vm.Busy) return; clear(); vm.Notify(""); Search(s, e); }; menu.Items.Add(item); }
        Add("Nombre y coincidencia exacta", () => { vm.Name = ""; vm.Exact = false; }); Add("Extensiones", () => vm.Extensions = ""); Add("Tipo", () => vm.Kind = 0); Add("Tamaños", () => { vm.MinSize = ""; vm.MaxSize = ""; }); Add("Carpeta", () => vm.Folder = ""); Add("Fechas de modificación", () => { vm.ModifiedFrom = null; vm.ModifiedTo = null; }); Add("Fechas de creación", () => { vm.CreatedFrom = null; vm.CreatedTo = null; }); Add("Ocultos y sistema", () => { vm.Hidden = 0; vm.SystemFlag = 0; }); Add("Unidades", () => SearchDrives.UnselectAll()); menu.IsOpen = true;
    }
    /// <summary>Minimizes the custom-chrome application window.</summary>
    void MinimizeWindow(object s, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    /// <summary>Acknowledges the selected section's pending completion marker without starting work or playing audio.</summary>
    void SectionChanged(object s, SelectionChangedEventArgs e) { if (IsLoaded && ReferenceEquals(e.OriginalSource, Sections)) { if (Sections.SelectedIndex >= 0) AcknowledgeSection(Sections.SelectedIndex); vm.Notify(nameof(vm.Selected)); } }
    /// <summary>Toggles the custom-chrome window between maximized and normal states.</summary>
    void MaximizeWindow(object s, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    /// <summary>Requests the common tracked-work-aware window-closing workflow.</summary>
    void CloseWindow(object s, RoutedEventArgs e) => Close();
    /// <summary>Opens the local saved-search popup.</summary>
    void ShowFavorites(object s, RoutedEventArgs e) => FavoritePopup.IsOpen = !FavoritePopup.IsOpen;
    /// <summary>Synchronizes scope selection without starting searches for internal binding/list refresh events.</summary>
    void ScopeChanged(object s, SelectionChangedEventArgs e)
    {
        if (syncingScope || SearchDrives == null || QuickScope.SelectedItem is not SearchScope scope) return;
        if (scope.VolumeId == "*") { FiltersPanel.IsExpanded = true; return; }
        SelectSearchVolumes(scope.VolumeId.Length == 0 ? [] : [scope.VolumeId]);
        // SelectionChanged also fires when rebuilding the volume list.
        // Only explicit search or filter actions should start another task.
    }
    /// <summary>Updates selected volume identities while suppressing recursive selector-change handling.</summary>
    void SelectSearchVolumes(IEnumerable<string> volumes)
    {
        var ids = volumes.ToHashSet(); syncingScope = true;
        try { SearchDrives.UnselectAll(); foreach (var v in vm.DriveVolumes.Where(v => ids.Contains(v.Id))) SearchDrives.SelectedItems.Add(v); SyncQuickScope(); }
        finally { syncingScope = false; }
    }
    /// <summary>Reflects the multi-drive selection in the compact single/multiple scope selector.</summary>
    void SyncQuickScope()
    {
        var ids = SearchDrives.SelectedItems.Cast<Volume>().Select(v => v.Id).ToArray();
        var key = ids.Length == 0 ? "" : ids.Length == 1 ? ids[0] : "*";
        QuickScope.SelectedItem = QuickScope.Items.OfType<SearchScope>().FirstOrDefault(s => s.VolumeId == key);
    }
    /// <summary>Synchronizes the compact scope label after an explicit multi-drive selection change.</summary>
    void SearchDrivesChanged(object s, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || syncingScope || vm.Busy) return;
        syncingScope = true; try { SyncQuickScope(); } finally { syncingScope = false; }
    }
    /// <summary>Builds removable, human-readable labels for the currently applied search filter.</summary>
    void UpdateChips()
    {
        vm.FilterChips.Clear(); 
            // Summary: Adds a filter action or visible filter chip to the current presentation collection.
            void Add(string key, string label, string tooltip = "") => vm.FilterChips.Add(new(key, label, tooltip));
        // Summary: Formats optional UTC date bounds as a local-language filter-chip description.
        string Dates(long? from, long? to) { 
            // Summary: Formats one stored UTC tick value as a local calendar date.
            string Date(long ticks) => new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("dd/MM/yyyy"); return (from.HasValue ? "desde " + Date(from.Value) : "") + (to.HasValue ? " hasta " + Date(to.Value) : ""); }
        if (current.Name.Length > 0) Add("name", (current.Exact ? "Nombre exacto: " : "Nombre: ") + current.Name);
        if (current.Extensions.Length > 0) Add("extensions", string.Join(" o ", current.Extensions.Select(e => "." + e.TrimStart('.'))));
        if (current.IsDirectory.HasValue) Add("kind", current.IsDirectory.Value ? "Carpetas" : "Archivos");
        if (current.MinSize.HasValue || current.MaxSize.HasValue) Add("size", (current.MinSize.HasValue ? "Desde " + Format.Bytes(current.MinSize.Value) : "") + (current.MaxSize.HasValue ? " Hasta " + Format.Bytes(current.MaxSize.Value) : ""));
        if (current.Folder.Length > 0) Add("folder", (Path.GetFileName(current.Folder.TrimEnd('\\')) is { Length: > 0 } folder ? folder : current.Folder) + (current.Recursive ? " y subcarpetas" : " · solo este nivel"), current.Folder);
        if (current.ModifiedFrom.HasValue || current.ModifiedTo.HasValue) Add("modified", "Modificado " + Dates(current.ModifiedFrom, current.ModifiedTo));
        if (current.CreatedFrom.HasValue || current.CreatedTo.HasValue) Add("created", "Creado " + Dates(current.CreatedFrom, current.CreatedTo));
        if (current.Hidden.HasValue || current.System.HasValue) Add("attributes", string.Join(" · ", new[] { current.Hidden.HasValue ? current.Hidden.Value ? "Solo ocultos" : "Sin ocultos" : "", current.System.HasValue ? current.System.Value ? "De sistema" : "Sin sistema" : "" }.Where(s => s.Length > 0)));
        if (current.Volumes.Length > 0) Add("volumes", string.Join(" o ", vm.Volumes.Where(v => current.Volumes.Contains(v.Id)).Select(v => v.Root.TrimEnd('\\'))));
    }
    /// <summary>Clears the requested filter category and explicitly searches with the remaining filters.</summary>
    void RemoveChip(object s, RoutedEventArgs e)
    {
        if (vm.Busy || s is not Button { Tag: string key }) return;
        switch (key)
        {
            case "name": vm.Name = ""; vm.Exact = false; break;
            case "extensions": vm.Extensions = ""; break;
            case "kind": vm.Kind = 0; break;
            case "size": vm.MinSize = ""; vm.MaxSize = ""; break;
            case "folder": vm.Folder = ""; break;
            case "modified": vm.ModifiedFrom = vm.ModifiedTo = null; break;
            case "created": vm.CreatedFrom = vm.CreatedTo = null; break;
            case "attributes": vm.Hidden = vm.SystemFlag = 0; break;
            case "volumes": SelectSearchVolumes([]); break;
        }
        vm.NotifyFilterFields(); Search(s, e);
    }
    /// <summary>Opens the selected drive's immediate folder view without recursively indexing it.</summary>
    async void DriveSelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || vm.Busy || DriveList.SelectedItem is not Volume v) return;
        await Run(token => LoadBrowseAsync(v.Root, token, v.Id), true);
    }
    /// <summary>Prompts for a folder to browse without treating it as a recursive scan.</summary>
    async void OpenBrowseFolder(object s, RoutedEventArgs e)
    {
        if (vm.Busy) return; var picker = new OpenFolderDialog { Title = "Abrir una carpeta para explorar" };
        if (picker.ShowDialog(this) == true) await Run(token => LoadBrowseAsync(picker.FolderName, token), true);
    }
    /// <summary>Navigates to the explicitly entered browser address through the tracked read lane.</summary>
    async void BrowseGo(object s, RoutedEventArgs e) => await Run(token => LoadBrowseAsync(vm.BrowserAddress.Trim(), token), true);
    /// <summary>Treats Enter in the browser address as explicit navigation.</summary>
    void BrowseAddressKey(object s, KeyEventArgs e) { if (e.Key == Key.Enter) { e.Handled = true; BrowseGo(s, e); } }
    /// <summary>Navigates to the current folder's parent when one exists.</summary>
    async void BrowseParent(object s, RoutedEventArgs e)
    {
        if (browsePath == "") return; var parent = Path.GetDirectoryName(browsePath); if (parent != null) await Run(token => LoadBrowseAsync(parent, token, browseVolume), true);
    }
    /// <summary>Navigates to the ancestor represented by the clicked breadcrumb.</summary>
    async void BrowseCrumb(object s, RoutedEventArgs e) { if (s is Button { Tag: string p }) await Run(token => LoadBrowseAsync(p, token, browseVolume), true); }
    /// <summary>Navigates an available folder from the browser's double-click action without deleting or executing a file.</summary>
    async void BrowseDoubleClick(object s, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source || FindRow(source) == null) return;
        if (vm.Selected is { IsDirectory: true } item) await Run(token => LoadBrowseAsync(item.Path, token, item.VolumeId), true);
    }
    /// <summary>Finds the result row owning a routed mouse event so empty-space clicks do not act on stale selections.</summary>
    static DataGridRow? FindRow(DependencyObject? p) { while (p != null) { if (p is DataGridRow row) return row; p = p is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(p) : LogicalTreeHelper.GetParent(p); } return null; }
    /// <summary>Validates a folder and volume, loads its immediate snapshot, and updates breadcrumbs and availability state.</summary>
    async Task LoadBrowseAsync(string path, CancellationToken token, string? expectedVolume = null)
    {
        path = Paths.Normalize(path);
        var v = vm.Volumes.FirstOrDefault(v => Paths.Within(path, v.Root) && (expectedVolume == null || v.Id == expectedVolume));
        v ??= volumeService.ForPath(path);
        bool online = v.Available && volumeService.Matches(path, v.Id);
        measuredFolders.Clear(); vm.BrowserItems.Clear(); vm.BrowserEmpty = true; vm.BrowserSelected = null;
        vm.Status = online ? "Leyendo esta carpeta…" : "Abriendo la carpeta desde el último índice…";
        long skipped = 0;
        if (online) { var report = await browser.LoadAsync(path, v.Id, vm.Settings.IncludeHidden, token); skipped = report.Skipped; }
        browsePath = path; browseVolume = v.Id; browseOffline = !online; browsePage = 0;
        vm.SelectedVolume = v; vm.BrowserAddress = path; vm.Notify(nameof(vm.BrowserAddress));
        vm.Breadcrumbs.Clear();
        var ancestors = new Stack<string>(); for (string? p = path; p != null; p = Path.GetDirectoryName(p)) ancestors.Push(p);
        foreach (var p in ancestors) vm.Breadcrumbs.Add(new(Path.GetFileName(p) is { Length: > 0 } n ? n + "  ›" : p + "  ›", p));
        vm.BrowserSummary = online ? $"En directo · {v.FreeText} · {skipped:N0} omitidos (ocultos, enlaces o sin acceso)" : "Unidad desconectada · vista del último índice, sin acceso a los archivos";
        vm.BrowserEmptyTitle = online ? "No hay elementos visibles en esta carpeta" : "No hay datos indexados de esta carpeta";
        vm.BrowserEmptyHint = online ? "Puedes subir un nivel o incluir los elementos ocultos desde Ajustes. Los enlaces y elementos inaccesibles se omiten." : "La unidad no está disponible. Conéctala y analiza esta carpeta para consultar su contenido.";
        vm.Notify(nameof(vm.BrowserEmptyTitle)); vm.Notify(nameof(vm.BrowserEmptyHint));
        vm.Notify(nameof(vm.BrowserSummary));
        await BrowseQuery(token);
        vm.Status = online ? "Carpeta abierta. Doble clic para entrar. Analizar esta carpeta permite buscar en sus descendientes." : "Vista del índice. Conecta la unidad para abrir o modificar sus archivos.";
    }
    /// <summary>Reads and risk-checks one bounded browsing page while retaining measured folder-size annotations.</summary>
    async Task BrowseQuery(CancellationToken token)
    {
        var result = await Task.Run(() => browseOffline ? store.Search(new() { Folder = browsePath, Recursive = false, Volumes = [browseVolume] }, browseSort, browseDescending, browsePage, 200, true) : browser.Page(browsePage, "", browseSort, browseDescending), token);
        var inspected = await Task.Run(() => result.Items.Select(e => { token.ThrowIfCancellationRequested(); return e with { Risk = riskInspector.Inspect(e, !browseOffline) }; }).ToArray(), token);
        browseTotal = result.Total; vm.BrowserItems.Clear();
        foreach (var item in inspected)
        {
            var display = item with { Availability = browseOffline ? "Unidad desconectada" : "En directo" };
            if (item.IsDirectory && measuredFolders.TryGetValue(item.Path, out var measured)) display = display with { FolderLogicalSize = measured.FolderLogicalSize, FolderSizePartial = measured.FolderSizePartial, FolderSizeDetail = measured.FolderSizeDetail };
            vm.BrowserItems.Add(display);
        }
        vm.BrowserEmpty = !inspected.Any(e => !vm.HideWarnings || !e.HasWarning);
        vm.BrowserCount = $"{result.Total:N0} {(result.Total == 1 ? "elemento" : "elementos")} · página {browsePage + 1} de {Math.Max(1, (result.Total + 199) / 200)}"; vm.Notify(nameof(vm.BrowserCount));
    }
    /// <summary>Loads the previous immediate-directory result page.</summary>
    async void BrowsePrevious(object s, RoutedEventArgs e) => await Run(async token => { if (browsePage > 0) { browsePage--; await BrowseQuery(token); } }, true);
    /// <summary>Loads the next immediate-directory result page.</summary>
    async void BrowseNext(object s, RoutedEventArgs e) => await Run(async token => { if ((browsePage + 1L) * 200 < browseTotal) { browsePage++; await BrowseQuery(token); } }, true);
    /// <summary>Changes the browsing page's stable server-side sort order.</summary>
    async void BrowseSort(object s, DataGridSortingEventArgs e)
    {
        e.Handled = true; await Run(async token => { if (browseSort == e.Column.SortMemberPath) browseDescending = !browseDescending; else { browseSort = e.Column.SortMemberPath; browseDescending = false; } foreach (var c in BrowserResults.Columns) c.SortDirection = null; e.Column.SortDirection = browseDescending ? ListSortDirection.Descending : ListSortDirection.Ascending; browsePage = 0; await BrowseQuery(token); }, true);
    }
    /// <summary>Explicitly indexes the open folder and descendants rather than merely browsing it.</summary>
    async void ScanCurrentFolder(object s, RoutedEventArgs e) { if (browsePath == "") { vm.Status = "Abre una carpeta antes de analizarla."; return; } await ScanRoots([browsePath]); }
    /// <summary>Copies the current browser scope into Search and explicitly refreshes/searches it.</summary>
    void SearchHere(object s, RoutedEventArgs e)
    {
        if (vm.Busy || browsePath == "") return; vm.Load(new() { Folder = browsePath, Recursive = true });
        SelectSearchVolumes([browseVolume]);
        vm.Section = 1; SearchDisks(s, e);
    }
    /// <summary>Reloads local history when no foreground operation is using the lane.</summary>
    void RefreshHistory(object s, RoutedEventArgs e) { if (!vm.Busy) RefreshLogs(); }
    /// <summary>Confirms and clears local index/cache metadata without deleting original filesystem items.</summary>
    async void ClearIndex(object s, RoutedEventArgs e) => await Run(async _ => { if (MessageBox.Show(this, "¿Borrar el índice y las fechas de análisis? Los archivos originales no se modifican.", "Borrar índice local", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return; vm.AdvancedRows.Clear(); advancedKind = null; vm.AdvancedCount = "Índice borrado"; forgottenGeneration++; vm.ForgottenFiles.Clear(); vm.ForgottenSummary = "Índice borrado. Revisa o analiza carpetas para disponer de candidatos."; await Task.Run(() => { store.ClearIndex(); browser.Reset(); searchSession.Reset(); if (advancedResults != null) advancedResults.Clear(true); else if (persistSettings && File.Exists(Path.Combine(Settings.DataFolder, "advanced.db"))) new AdvancedResultStore(Path.Combine(Settings.DataFolder, "advanced.db")).Clear(true); }); vm.Results.Clear(); vm.BrowserItems.Clear(); vm.BrowserEmpty = true; vm.SearchEmpty = true; vm.Selected = null; RefreshLogs(); await RefreshVolumesAsync(); vm.Status = "Índice borrado."; });
    /// <summary>Confirms and clears only the local operation-history records.</summary>
    async void ClearHistory(object s, RoutedEventArgs e) => await Run(async _ => { if (MessageBox.Show(this, "¿Borrar el historial local de operaciones?", "Historial", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return; await Task.Run(store.ClearHistory); RefreshLogs(); vm.Status = "Historial borrado."; });
    /// <summary>Validates exclusions and retention settings and persists them only in production mode.</summary>
    void SaveConfiguration() { var paths = vm.Exclusions.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Paths.Normalize).ToList(); vm.Settings.Exclusions = paths; vm.Settings.AgeDays = Math.Clamp(vm.Settings.AgeDays, 1, 3650); vm.Settings.RetentionDays = Math.Clamp(vm.Settings.RetentionDays, 1, 365); vm.Settings.ForgottenDays = Math.Clamp(vm.Settings.ForgottenDays, 30, 3650); if (persistSettings) vm.Settings.Save(); }
    /// <summary>Saves validated configuration and applies the history-retention limit.</summary>
    void SaveSettings(object s, RoutedEventArgs e) { try { SaveConfiguration(); store.Prune(vm.Settings.RetentionDays); vm.Status = "Ajustes guardados."; } catch (Exception ex) { vm.Status = ex.Message; } }
    /// <summary>Requests cancellation of the active search while preserving already displayed results.</summary>
    void StopSearch(object s, RoutedEventArgs e)
    {
        if (!vm.CanStopSearch) return;
        vm.SearchStopRequested = true; vm.SearchStateText = "Deteniendo búsqueda · se conservarán los resultados"; vm.SearchEtaText = "Deteniendo…";
        searchOperation?.Cancel(); vm.Status = "Parada solicitada. Se está cerrando la operación de índice en curso; no se borran los resultados.";
    }
    /// <summary>Requests cancellation of the foreground operation or delegates to the active search stop action.</summary>
    void Cancel(object s, RoutedEventArgs e)
    {
        if (!vm.Busy && vm.SearchInProgress) { StopSearch(s, e); return; }
        operation?.Cancel(); vm.Status = "Cancelación solicitada. Se terminará el elemento en curso; lo completado no se revierte.";
    }
    /// <summary>Applies the user's selected light or dark theme resources.</summary>
    void ToggleTheme(object s, RoutedEventArgs e) { ApplyTheme(); }
    /// <summary>Updates the shared palette used by windows, cards, tables, warnings, and notifications.</summary>
    void ApplyTheme() { string[] keys = ["Bg", "Sidebar", "Surface", "Field", "Text", "Muted", "Line", "Accent", "Teal", "Selected", "Hover", "Danger", "DangerTint"]; string[] dark = ["#151B2B", "#121827", "#1C2436", "#242E43", "#F1F3FC", "#A5AFC8", "#35415B", "#B79AF7", "#20D9CB", "#40325F", "#2A354B", "#FF858F", "#30212B"]; string[] light = ["#F3F4FA", "#E9ECF5", "#FFFFFF", "#EEF0F8", "#20263E", "#606B88", "#D3D9E8", "#7651BA", "#00877E", "#E4DAFA", "#E5E9F4", "#B52E4B", "#FFF0F1"]; var colors = vm.Settings.Dark ? dark : light; for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i])); }
}
