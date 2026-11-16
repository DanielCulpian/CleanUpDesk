using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CleanUpDesk.Core;

namespace CleanUpDesk.App;
/// <summary>Creates bounded group presentation state from complete persisted summary statistics.</summary>
public sealed class DuplicateGroupState(DuplicateGroupInfo info) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public DuplicateGroupInfo Info { get; private set; } = info;
    public string Key => Info.Key;
    public string Label => Key.StartsWith("H:") ? "Mismo contenido · SHA256 " + Key[2..14] : Key.StartsWith("N:") ? "Mismo nombre · " + Info.Name : "Mismo tamaño · " + Format.Bytes(Info.Size / Math.Max(1, Info.Count));
    public string Summary => $"{Info.Count:N0} archivos · {Format.Bytes(Info.Size)} lógicos · {Info.Kept:N0} para conservar";
    public string Hint => Key.StartsWith("H:") ? "Contenido comparado mediante SHA256. No significa que sobre ninguna copia." : "Coincidencia de metadatos; no demuestra contenido idéntico.";
    public int Page { get; set; }
    public int Visible { get; set; }
    public string PageText => $"{Visible} en esta página · {Page * 50L + 1:N0}–{Math.Min(Info.Count, (Page + 1L) * 50):N0} de {Info.Count:N0}";
    public bool HasPrevious => Page > 0;
    public bool HasNext => (Page + 1L) * 50 < Info.Count;
    bool expanded;
    public bool IsExpanded { get => expanded; set { expanded = value; Refresh(); } }
    public string Arrow => IsExpanded ? "⌄" : "›";
    /// <summary>Refreshes group totals while keeping a still-valid internal page and expansion state.</summary>
    public void Update(DuplicateGroupInfo info) { Info = info; if (Page * 50L >= info.Count) Page = 0; Refresh(); }
    /// <summary>Notifies bindings that the group's summary, paging, or expansion state changed.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}
public sealed class DuplicateGroupConverter : IValueConverter
{
    public Dictionary<string, DuplicateGroupState> Groups { get; } = new();
    /// <summary>Resolves a findings group key to its current bounded presentation state.</summary>
    public object Convert(object value, Type type, object parameter, CultureInfo culture) => value is string key && Groups.TryGetValue(key, out var group) ? group : value;
    /// <summary>Rejects reverse conversion because grouping only maps stored keys to display state.</summary>
    public object ConvertBack(object value, Type type, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
public partial class MainWindow
{
    /// <summary>Checks whether a result belongs to an expanded group before selection-based actions may use it.</summary>
    bool ExpandedDuplicateRow(AdvancedFinding row) => !DuplicateResults || duplicateGroups.Groups.TryGetValue(row.GroupKey, out var state) && state.IsExpanded;
    /// <summary>Returns only selected results that are visible in expanded groups or an ungrouped view.</summary>
    AdvancedFinding[] SelectedAdvancedRows() => AdvancedResults.SelectedItems.Cast<AdvancedFinding>().Where(ExpandedDuplicateRow).ToArray();
    readonly DuplicateGroupConverter duplicateGroups = new();
    long advancedPageCount = 1;
    /// <summary>Reads ten group summaries and at most fifty files per group, or a normal bounded findings page.</summary>
    async Task<AdvancedPage> ReadAdvancedPageAsync()
    {
        if (!DuplicateResults)
        {
            duplicateGroups.Groups.Clear();
            var result = await Task.Run(() => AdvancedStore.Page(advancedPage, advancedKind == AdvancedKind.Largest));
            advancedPageCount = Math.Max(1, (result.Total + 199) / 200); return result;
        }
        var groups = await Task.Run(() => AdvancedStore.DuplicateGroups(advancedPage));
        var active = groups.Items.Select(g => g.Key).ToHashSet(); foreach (var key in duplicateGroups.Groups.Keys.Where(k => !active.Contains(k)).ToArray()) duplicateGroups.Groups.Remove(key);
        foreach (var info in groups.Items)
        {
            if (duplicateGroups.Groups.TryGetValue(info.Key, out var state)) state.Update(info); else duplicateGroups.Groups.Add(info.Key, new(info));
        }
        var pages = groups.Items.Select(info => (info.Key, duplicateGroups.Groups[info.Key].Page)).ToArray();
        var rows = await Task.Run(() => pages.SelectMany(g => AdvancedStore.DuplicateGroupItems(g.Key, g.Page)).ToArray());
        advancedPageCount = Math.Max(1, (groups.Groups + 9) / 10);
        vm.AdvancedCount = $"{groups.Files:N0} archivos en {groups.Groups:N0} grupos · página de grupos {advancedPage + 1} de {advancedPageCount}";
        return new(rows, groups.Files);
    }
    /// <summary>Applies duplicate-only collection grouping and refreshes visible counts without changing stored decisions.</summary>
    void ConfigureDuplicateGrouping()
    {
        var view = CollectionViewSource.GetDefaultView(vm.AdvancedRows);
        using (view.DeferRefresh())
        {
            view.GroupDescriptions.Clear();
            if (DuplicateResults) view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(AdvancedFinding.GroupKey), duplicateGroups));
        }
        ConfigureDuplicateColumns();
        foreach (var state in duplicateGroups.Groups.Values) { state.Visible = vm.AdvancedRows.Count(r => r.GroupKey == state.Key && (!vm.HideWarnings || !r.HasWarning)); state.Refresh(); }
    }
    /// <summary>Uses full-width wrapped file details for duplicates and restores normal columns for other analyses.</summary>
    void ConfigureDuplicateColumns()
    {
        bool grouped = DuplicateResults;
        for (int i = 0; i < AdvancedResults.Columns.Count; i++) AdvancedResults.Columns[i].Visibility = grouped && i != 1 ? Visibility.Collapsed : Visibility.Visible;
        var file = (DataGridTemplateColumn)AdvancedResults.Columns[1];
        file.CellTemplate = (DataTemplate)FindResource(grouped ? "DuplicateFileCard" : "FileName");
        file.Header = grouped ? "Archivo · ruta completa y detalles" : "Nombre";
        file.MinWidth = grouped ? 120 : 170;
        file.CanUserResize = !grouped;
        file.Width = grouped ? new DataGridLength(Math.Max(120, AdvancedResults.ActualWidth - SystemParameters.VerticalScrollBarWidth - 24)) : new DataGridLength(210);
        AdvancedResults.RowHeight = grouped ? double.NaN : 42;
        AdvancedResults.MinRowHeight = grouped ? 96 : 42;
        ScrollViewer.SetHorizontalScrollBarVisibility(AdvancedResults, grouped ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
    }
    /// <summary>Keeps the grouped file-details column fitted to the actual viewport instead of a compressed star width.</summary>
    void AdvancedResultsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!DuplicateResults) return;
        var width = Math.Max(120, e.NewSize.Width - SystemParameters.VerticalScrollBarWidth - 24);
        if (Math.Abs(AdvancedResults.Columns[1].Width.Value - width) > .5) AdvancedResults.Columns[1].Width = new DataGridLength(width);
    }
    /// <summary>Requests the previous internal file page for the clicked duplicate group.</summary>
    async void DuplicateGroupPrevious(object sender, RoutedEventArgs e) => await ChangeDuplicatePage(sender, -1);
    /// <summary>Requests the next internal file page for the clicked duplicate group.</summary>
    async void DuplicateGroupNext(object sender, RoutedEventArgs e) => await ChangeDuplicatePage(sender, 1);
    /// <summary>Changes one group's bounded page while preserving its expanded state.</summary>
    async Task ChangeDuplicatePage(object sender, int delta)
    {
        if (sender is not Button { Tag: DuplicateGroupState state } || (delta < 0 ? !state.HasPrevious : !state.HasNext)) return;
        await Run(async _ => { state.Page += delta; state.IsExpanded = true; await LoadAdvancedPageAsync(); }, true);
    }
    /// <summary>Removes selections hidden by collapsing a group without modifying any files.</summary>
    void DuplicateGroupCollapsed(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: CollectionViewGroup { Name: DuplicateGroupState state } }) return;
        foreach (var row in AdvancedResults.SelectedItems.Cast<AdvancedFinding>().Where(r => r.GroupKey == state.Key).ToArray()) AdvancedResults.SelectedItems.Remove(row);
    }
    /// <summary>Expands only the duplicate groups loaded on the current group page.</summary>
    void ExpandDuplicateGroups(object sender, RoutedEventArgs e) { foreach (var state in duplicateGroups.Groups.Values) state.IsExpanded = true; }
    /// <summary>Collapses the current group page and lets collapse handling clear hidden selections.</summary>
    void CollapseDuplicateGroups(object sender, RoutedEventArgs e) { foreach (var state in duplicateGroups.Groups.Values) state.IsExpanded = false; }
}
