using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CleanUpDesk.Core;
using Microsoft.Win32;

namespace CleanUpDesk.App;
public partial class MainWindow
{
    AdvancedResultStore? advancedResults;
    CancellationTokenSource? advancedOperation;
    AdvancedKind? advancedKind;
    int advancedPage;
    AdvancedResultStore AdvancedStore => advancedResults ??= persistSettings ? new AdvancedResultStore(Path.Combine(Settings.DataFolder, "advanced.db")) : throw new InvalidOperationException("Las pruebas necesitan una base avanzada temporal explícita.");
    /// <summary>Starts the selected advanced analysis from its explicit UI action.</summary>
    async void StartAdvanced(object s, RoutedEventArgs e) => await AnalyzeAdvancedAsync();
    /// <summary>Runs a cancellable advanced analysis, restores its bounded results view, and reports real completion or failure.</summary>
    public async Task AnalyzeAdvancedAsync()
    {
        if (closeRequested || resourcesClosed) return;
        if (vm.Busy || vm.SearchInProgress || vm.AdvancedBusy) { vm.AdvancedStatus = "Espera o para la búsqueda actual antes de iniciar otro análisis de disco. Puedes seguir explorando."; return; }
        var scope = AdvancedVolume.SelectedItem is SearchScope selected ? selected.VolumeId : "";
        using var process = TrackProcess();
        var notice = BeginProcess(4);
        duplicateGroups.Groups.Clear();
        using var cancellation = new CancellationTokenSource(); advancedOperation = cancellation;
        vm.AdvancedBusy = true; advancedKind = null; advancedPage = 0; vm.AdvancedRows.Clear(); vm.AdvancedCount = "Analizando…"; vm.AdvancedStatus = "Preparando el análisis del índice guardado…";
        try
        {
            var kind = vm.AdvancedMode switch { 1 => AdvancedKind.Empty, 2 => AdvancedKind.Largest, _ => vm.DuplicateMode switch { 1 => AdvancedKind.DuplicateSize, 2 => AdvancedKind.DuplicateHash, _ => AdvancedKind.DuplicateName } };
            var folder = vm.AdvancedFolder.Trim(); if (folder.Length > 0) folder = Paths.Normalize(folder);
            var filter = new SearchFilter { Folder = folder, Recursive = true, Volumes = scope.Length == 0 ? [] : [scope] };
            if (scope.Length > 0 && folder.Length > 0 && vm.Volumes.FirstOrDefault(v => v.Id == scope) is { } disk && !Paths.Within(folder, disk.Root)) throw new ArgumentException("La carpeta indicada pertenece a otra unidad. Quita el filtro de carpeta o elige su disco.");
            var progress = new Progress<AdvancedProgress>(p =>
            {
                if (cancellation.IsCancellationRequested || !vm.AdvancedBusy || !ReferenceEquals(advancedOperation, cancellation)) return;
                vm.AdvancedStatus = $"{p.Phase} · {p.Processed:N0} candidatos revisados · {p.Hashed:N0} huellas ({p.Cached:N0} reutilizadas) · {Format.Bytes(p.Bytes)} leídos" + (p.TotalBytes.HasValue ? $" de {Format.Bytes(p.TotalBytes.Value)} candidatos estimados" : "") + " · " + p.Path;
            });
            var service = new AdvancedAnalysisService(store, AdvancedStore); var excluded = vm.Settings.Exclusions.Append(Settings.DataFolder).Append(AppContext.BaseDirectory).ToArray();
            Func<Entry, bool>? include = vm.HideWarnings ? e => !riskInspector.Inspect(e, true).BlockDeletion : null;
            var report = await notice.Watch(() => service.AnalyzeAsync(kind, filter, excluded, cancellation.Token, progress, include), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested(); advancedKind = kind;
            await LoadAdvancedPageAsync();
            vm.AdvancedStatus = $"Análisis terminado · {report.Processed:N0} revisados · {report.Skipped:N0} omitidos o alias · {report.Findings:N0} hallazgos" + (kind is AdvancedKind.DuplicateName or AdvancedKind.DuplicateSize or AdvancedKind.DuplicateHash ? $" en {report.Groups:N0} grupos. " : ". ")
                + (kind is AdvancedKind.DuplicateName or AdvancedKind.DuplicateSize ? "Nombre o tamaño iguales NO prueban contenido idéntico; usa SHA256 para verificar. " : "")
                + (kind == AdvancedKind.Largest ? "Top 100 según el tamaño guardado en el índice. " : "")
                + (kind == AdvancedKind.Empty ? "Solo se comprueban candidatos del índice. Actualízalo si has vaciado archivos o carpetas. Se incluyen ocultos; vacío no significa innecesario. " : "")
                + (kind == AdvancedKind.DuplicateHash ? $"{Format.Bytes(report.BytesRead)} leídos · {report.FullReads:N0} lecturas completas · {report.Cached:N0} huellas reutilizadas. Antes de borrar se vuelve a verificar el contenido completo. " : "")
                + "Ámbito: " + (folder.Length > 0 ? folder : "discos elegidos del índice") + ". No se ha eliminado nada.";
            CompleteProcess(4);
            if (report.Findings == 0) vm.AdvancedStatus += " No hay resultados en el alcance consultado. Comprueba disco, carpeta y exclusiones; pulsa Actualizar índices si falta cobertura. " + (vm.HideWarnings ? "El filtro sin advertencias puede descartar todos los candidatos." : "");
            else if (kind == AdvancedKind.Largest && report.Findings < 100) vm.AdvancedStatus += " Se muestran todos los archivos que cumplen el alcance y el filtro; no hay 100 candidatos aptos en este índice.";
        }
        catch (OperationCanceledException) { vm.AdvancedRows.Clear(); vm.AdvancedCount = "Análisis cancelado"; vm.AdvancedStatus = "Análisis cancelado. No se presentan grupos parciales como duplicados verificados."; }
        catch (Exception ex)
        {
            vm.AdvancedRows.Clear(); vm.AdvancedCount = "No se pudo completar";
            vm.AdvancedStatus = "No se pudo completar: " + ex.GetBaseException().Message + LogAdvancedFailure(ex);
        }
        finally { vm.AdvancedBusy = false; advancedOperation = null; }
    }
    /// <summary>Appends a bounded local diagnostic for a production advanced-analysis failure.</summary>
    string LogAdvancedFailure(Exception error)
    {
        if (!persistSettings) return "";
        try
        {
            Directory.CreateDirectory(Settings.DataFolder); var path = Path.Combine(Settings.DataFolder, "advanced-errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_048_576) File.WriteAllText(path, "", System.Text.Encoding.UTF8);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}\n{error}\n\n", System.Text.Encoding.UTF8);
            return " · Detalle en " + path;
        }
        catch { return ""; }
    }
    /// <summary>Requests cancellation without presenting partial groups as verified duplicates.</summary>
    void CancelAdvanced(object s, RoutedEventArgs e) { advancedOperation?.Cancel(); vm.AdvancedStatus = "Cancelación solicitada; se cerrará la lectura en curso."; }
    /// <summary>Loads and risk-checks one bounded findings/group page while preserving relevant visible selections.</summary>
    async Task LoadAdvancedPageAsync()
    {
        var selectedPaths = AdvancedResults.SelectedItems.Cast<AdvancedFinding>().Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = await ReadAdvancedPageAsync();
        var rows = await Task.Run(() =>
        {
            var live = volumeService.Detect();
            return result.Items.Select(row =>
            {
                bool online = live.Any(v => v.Available && v.Id == row.File.VolumeId && Paths.Within(row.Path, v.Root));
                bool exists = online && (File.Exists(row.Path) || Directory.Exists(row.Path));
                return row with { File = row.File with { Risk = riskInspector.Inspect(row.File, online), Availability = !online ? "Unidad desconectada" : exists ? "Disponible al consultar" : "No existe o acceso denegado" } };
            }).ToArray();
        });
        // Detach during replacement to prevent DataGrid from querying CurrentItem while grouping changes.
        AdvancedResults.ItemsSource = null;
        try
        {
            vm.AdvancedRows.Clear(); foreach (var row in rows) vm.AdvancedRows.Add(row);
            ConfigureDuplicateGrouping();
        }
        finally { AdvancedResults.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.AdvancedRows))); }
        foreach (var row in rows.Where(r => selectedPaths.Contains(r.Path) && ExpandedDuplicateRow(r))) AdvancedResults.SelectedItems.Add(row);
        if (!DuplicateResults) vm.AdvancedCount = $"{result.Total:N0} elementos · página {advancedPage + 1} de {advancedPageCount}";
    }
    bool DuplicateResults => advancedKind is AdvancedKind.DuplicateName or AdvancedKind.DuplicateSize or AdvancedKind.DuplicateHash;
    /// <summary>Applies the selected non-destructive retention rule and refreshes the current group summaries.</summary>
    async void ApplyKeepRule(object s, RoutedEventArgs e)
    {
        if (vm.AdvancedBusy || !DuplicateResults) { vm.AdvancedStatus = "Analiza duplicados antes de elegir una regla de conservación."; return; }
        await Run(async _ => { await Task.Run(() => AdvancedStore.ApplyRule((KeepRule)vm.KeepMode)); await LoadAdvancedPageAsync(); vm.AdvancedStatus = "Regla aplicada: se conserva una copia por grupo. No se ha borrado ningún archivo."; }, true);
    }
    /// <summary>Marks the selected expanded-group file as its manually chosen original.</summary>
    async void MarkOriginal(object s, RoutedEventArgs e)
    {
        if (vm.AdvancedBusy || !DuplicateResults || AdvancedResults.SelectedItem is not AdvancedFinding row || !ExpandedDuplicateRow(row)) return;
        await Run(async _ => { await Task.Run(() => AdvancedStore.MarkOriginal(row.Path)); await LoadAdvancedPageAsync(); vm.AdvancedStatus = "Original elegida manualmente para este grupo. Los metadatos no permiten deducirla con certeza."; }, true);
    }
    /// <summary>Loads the previous results/group page when available.</summary>
    async void AdvancedPrevious(object s, RoutedEventArgs e) { if (vm.AdvancedBusy) return; await Run(async _ => { if (advancedPage > 0) { advancedPage--; await LoadAdvancedPageAsync(); } }, true); }
    /// <summary>Loads the next results/group page without launching another disk analysis.</summary>
    async void AdvancedNext(object s, RoutedEventArgs e) { if (vm.AdvancedBusy) return; await Run(async _ => { if (advancedPage + 1L < advancedPageCount) { advancedPage++; await LoadAdvancedPageAsync(); } }, true); }
    /// <summary>Lets the user choose an indexed folder scope without starting analysis automatically.</summary>
    void ChooseAdvancedFolder(object s, RoutedEventArgs e)
    {
        if (vm.AdvancedBusy) return; var picker = new OpenFolderDialog { Title = "Limitar las funciones avanzadas a una carpeta indexada" };
        if (picker.ShowDialog(this) == true) { vm.AdvancedFolder = picker.FolderName; vm.Notify(nameof(vm.AdvancedFolder)); }
    }
    /// <summary>Copies selected visible expanded-group paths, one per line.</summary>
    void CopyAdvancedPaths(object s, RoutedEventArgs e)
    {
        try { var paths = SelectedAdvancedRows().Select(r => r.Path).ToArray(); if (paths.Length > 0) Clipboard.SetText(string.Join(Environment.NewLine, paths)); }
        catch (Exception ex) { vm.Status = ex.Message; }
    }
    /// <summary>Navigates to the containing folder of a selected visible advanced result.</summary>
    async void OpenAdvancedFolder(object s, RoutedEventArgs e)
    {
        if (AdvancedResults.SelectedItem is not AdvancedFinding row || !ExpandedDuplicateRow(row)) return; var path = row.File.IsDirectory ? row.Path : row.File.Parent;
        await Run(async token => { vm.Section = 0; await LoadBrowseAsync(path, token, row.File.VolumeId); }, true);
    }
    /// <summary>Starts the shared safety review only for visible files in expanded groups.</summary>
    async void DeleteAdvancedSelected(object s, RoutedEventArgs e)
    {
        var rows = SelectedAdvancedRows();
        if (rows.Length == 0) { vm.AdvancedStatus = "Expande un grupo y selecciona archivos visibles para revisarlos."; return; }
        await ReviewAdvancedAsync(rows.Select(r => r.Path));
    }
    /// <summary>Queries bounded non-keeper targets and opens the explicit global surplus review.</summary>
    async void DeleteAdvancedSurplus(object s, RoutedEventArgs e)
    {
        if (closeRequested || resourcesClosed || vm.AdvancedBusy || !DuplicateResults) return;
        using var process = TrackProcess();
        await ReviewAdvancedAsync(await Task.Run(() => AdvancedStore.Surplus()));
    }
    /// <summary>Creates an advanced deletion plan and routes it through the shared two-step confirmation workflow.</summary>
    public async Task ReviewAdvancedAsync(IEnumerable<string> paths)
    {
        await Run(async token =>
        {
            if (advancedKind == null) throw new InvalidOperationException("Completa un análisis antes de revisar eliminaciones.");
            var plan = await Task.Run(() => AdvancedStore.Plan(advancedKind.Value, paths), token);
            var service = new DeletionService(store, volumeService, new SafetyPolicy(), new AdvancedDeletionBackend(plan, new WindowsDeleteBackend()));
            vm.AdvancedStatus = "Las copias a conservar se verificarán y mantendrán abiertas durante el borrado. SHA256 vuelve a comprobar el contenido; puede tardar. Nada se elimina sin revisión y confirmación final.";
            var results = await DeleteFlow(plan.Paths, false, token, service, (retained, cancellation) => Task.Run(() => plan.AcquireAsync(cancellation, retained), cancellation), plan.RequiresPermanentFolders);
            // Reflect completed operations even when cancellation occurs between targets.
            if (results != null) await Task.Run(() => AdvancedStore.RemoveCompleted(results.Where(r => r.Status == "Eliminado").Select(r => r.Path), DuplicateResults));
            if (advancedPage > 0 && await Task.Run(() => DuplicateResults ? AdvancedStore.DuplicateGroups(advancedPage).Items.Count == 0 : AdvancedStore.Page(advancedPage).Items.Count == 0)) advancedPage = 0;
            await LoadAdvancedPageAsync();
        });
        vm.AdvancedStatus = vm.Status;
    }
}
