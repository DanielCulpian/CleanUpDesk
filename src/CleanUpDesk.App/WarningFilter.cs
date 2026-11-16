using System.Collections;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CleanUpDesk.Core;

namespace CleanUpDesk.App;
public partial class MainWindow
{
    /// <summary>Identifies risk-marked entries across the supported file-list presentation types.</summary>
    static bool Warns(object item) => item switch { Entry e => e.HasWarning, AdvancedFinding a => a.HasWarning, ForgottenFile f => f.HasWarning, _ => false };
    IEnumerable<IList> FileLists => new IList[] { vm.Results, vm.BrowserItems, vm.AdvancedRows, vm.ForgottenFiles, vm.CleanupPaths, vm.BrowserCachePaths };
    /// <summary>Installs a shared warning filter on each file list without changing audit history.</summary>
    void ConfigureWarningViews()
    {
        foreach (var list in FileLists)
        {
            CollectionViewSource.GetDefaultView(list).Filter = item => !vm.HideWarnings || !Warns(item);
            ((INotifyCollectionChanged)list).CollectionChanged += (_, _) => UpdateWarningSummary();
        }
        UpdateWarningSummary();
    }
    /// <summary>Refreshes empty states and explains that ordinary page counts retain their pre-filter totals.</summary>
    void UpdateWarningSummary()
    {
        vm.SearchEmpty = !vm.Results.Any(e => !vm.HideWarnings || !e.HasWarning);
        vm.BrowserEmpty = !vm.BrowserItems.Any(e => !vm.HideWarnings || !e.HasWarning);
        vm.WarningSummary = vm.HideWarnings ? "Advertencias ocultas · los contadores y estimaciones originales se conservan; el filtro se aplica a cada página de búsqueda/exploración. Top 100 se rellena con los siguientes archivos aptos. Sin advertencias no significa prescindible." : "Oculta elementos protegidos, en uso o sin verificar en todas las listas de archivos. En Top 100 se buscan los siguientes candidatos hasta completar 100 si hay suficientes. Sin advertencias no significa prescindible.";
        vm.Notify(nameof(vm.WarningSummary));
    }
    /// <summary>Applies the explicitly clicked global warning-visibility option.</summary>
    async void WarningFilterChanged(object sender, RoutedEventArgs e) => await SetWarningFilterAsync(vm.HideWarnings);
    /// <summary>Refreshes file-list views and recomputes advanced groups/Top 100 so hidden risks do not leave missing slots or keepers.</summary>
    public async Task SetWarningFilterAsync(bool hide)
    {
        if (closeRequested || resourcesClosed) return;
        vm.HideWarnings = hide;
        foreach (var list in FileLists) CollectionViewSource.GetDefaultView(list).Refresh();
        if (vm.Selected?.HasWarning == true && hide) vm.Selected = null;
        UpdateWarningSummary();
        // Rebuild the selection before LIMIT 100 instead of hiding rows from an already limited result.
        // Duplicate groups and decisions are rebuilt too, without retaining hidden copies.
        if (advancedKind != null && !vm.Busy && !vm.SearchInProgress && !vm.AdvancedBusy) await AnalyzeAdvancedAsync();
    }
    /// <summary>Classifies bounded cleanup candidates using their explicitly authorized temporary/cache policy.</summary>
    async Task<Entry[]> InspectPreviewAsync(IEnumerable<string> paths, IEnumerable<string> roots, CancellationToken token)
    {
        var inspector = new FileRiskInspector(new SafetyPolicy(tempRoots: roots));
        return await Task.Run(() => paths.Select(path =>
        {
            token.ThrowIfCancellationRequested();
            try { var entry = Paths.ReadEntry(path, ""); return entry with { Risk = inspector.Inspect(entry, true, temporary: true) }; }
            catch (Exception ex) when (Scanner.IsReadError(ex)) { return new Entry(path, Path.GetDirectoryName(path)!, Path.GetFileName(path), "", false, 0, 0, 0, 0, "") { Risk = new("No disponible", "No se pudieron consultar sus metadatos.", true) }; }
        }).ToArray(), token);
    }
    /// <summary>Removes a stale folder restriction when the user selects a different drive.</summary>
    void AdvancedScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (vm == null || AdvancedVolume?.SelectedItem is not SearchScope scope || scope.VolumeId.Length == 0 || vm.AdvancedFolder.Length == 0) return;
        var volume = vm.Volumes.FirstOrDefault(v => v.Id == scope.VolumeId);
        if (volume != null && Path.IsPathFullyQualified(vm.AdvancedFolder) && !Paths.Within(Paths.Normalize(vm.AdvancedFolder), volume.Root))
        {
            vm.AdvancedFolder = ""; vm.Notify(nameof(vm.AdvancedFolder));
            vm.AdvancedStatus = "Se ha quitado el filtro de carpeta porque pertenecía a otra unidad. El análisis usará el disco elegido.";
        }
    }
    /// <summary>Removes only the advanced folder restriction, leaving files and the index untouched.</summary>
    void ClearAdvancedFolder(object sender, RoutedEventArgs e) { vm.AdvancedFolder = ""; vm.Notify(nameof(vm.AdvancedFolder)); }
}
