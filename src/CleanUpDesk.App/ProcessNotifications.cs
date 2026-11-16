using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using CleanUpDesk.Core;

namespace CleanUpDesk.App;

public static class SectionNotice
{
    public static readonly DependencyProperty PendingProperty = DependencyProperty.RegisterAttached("Pending", typeof(bool), typeof(SectionNotice), new PropertyMetadata(false));
    /// <summary>Reads the attached unread-completion flag for a sidebar section.</summary>
    public static bool GetPending(DependencyObject target) => (bool)target.GetValue(PendingProperty);
    /// <summary>Updates the attached unread-completion flag without starting a process.</summary>
    public static void SetPending(DependencyObject target, bool value) => target.SetValue(PendingProperty, value);
}

// A single stroke travels around the outline; hiding or acknowledging it releases the animation.
public sealed class CompletionHalo : FrameworkElement
{
    public static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(nameof(Phase), typeof(double), typeof(CompletionHalo), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Phase { get => (double)GetValue(PhaseProperty); set => SetValue(PhaseProperty, value); }
    /// <summary>Initializes the non-interactive halo and releases animation clocks when its visual lifetime ends.</summary>
    public CompletionHalo()
    {
        IsHitTestVisible = false;
        IsVisibleChanged += (_, _) => Animate(); Loaded += (_, _) => Animate(); Unloaded += (_, _) => BeginAnimation(PhaseProperty, null);
    }
    /// <summary>Runs the border trace only while visible and when Windows permits client-area animations.</summary>
    void Animate() => BeginAnimation(PhaseProperty, IsVisible && IsLoaded && SystemParameters.ClientAreaAnimation ? new DoubleAnimation(0, 1, TimeSpan.FromSeconds(3)) { RepeatBehavior = RepeatBehavior.Forever } : null);
    /// <summary>Draws the section outline and one moving highlight along its rounded border.</summary>
    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 4 || ActualHeight < 4) return;
        var rect = new Rect(2, 2, ActualWidth - 4, ActualHeight - 4);
        var contour = new RectangleGeometry(rect, 7, 7).GetOutlinedPathGeometry();
        var color = (TryFindResource("Teal") as SolidColorBrush)?.Color ?? Colors.Turquoise;
        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(110, color.R, color.G, color.B)), 1), rect, 7, 7);
        var segment = new StreamGeometry(); using (var c = segment.Open())
        {
            for (int i = 0; i <= 32; i++) { contour.GetPointAtFractionLength((Phase + i * .004) % 1, out var p, out _); if (i == 0) c.BeginFigure(p, false, false); else c.LineTo(p, true, false); }
        }
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)), 6), segment);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(color), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, segment);
    }
}

public partial class MainWindow
{
    /// <summary>Acknowledges old section state and creates one traceable, shutdown-aware completion signal.</summary>
    CompletionSignal BeginProcess(int section)
    {
        AcknowledgeSection(section);
        var id = AudioDiagnostics.NextOperation(); AudioDiagnostics.Record($"operation-start id={id} section={section}");
        return new CompletionSignal(stamp => { completionSound(stamp); AudioDiagnostics.Record($"completion-notify id={id} section={section}"); }, () => !lifetime.IsCancellationRequested && !Volatile.Read(ref closeRequested) && !Volatile.Read(ref resourcesClosed) && vm.Settings.SearchCompletionSound);
    }
    /// <summary>Clears a section's unread completion marker when the user visits it.</summary>
    void AcknowledgeSection(int section)
    {
        var tab = (TabItem)Sections.Items[section]; SectionNotice.SetPending(tab, false); AutomationProperties.SetHelpText(tab, "");
    }
    /// <summary>Marks completion visually only when the originating section is not currently selected.</summary>
    void CompleteProcess(int section)
    {
        if (lifetime.IsCancellationRequested) return;
        if (Sections.SelectedIndex == section) return;
        var tab = (TabItem)Sections.Items[section]; SectionNotice.SetPending(tab, true);
        AutomationProperties.SetHelpText(tab, "Proceso terminado. Abre esta sección para consultar los resultados.");
    }
    /// <summary>Starts the global index-refresh action from the persistent title-bar button.</summary>
    async void RefreshAllIndices(object sender, RoutedEventArgs e) => await RefreshIndicesAsync();
    /// <summary>Rebuilds known available indexed roots while preserving disconnected scopes and enforcing operation guards.</summary>
    public Task RefreshIndicesAsync() => Run(async token =>
    {
        var roots = await Task.Run(() => store.IndexedLocations(), token);
        var saved = await Task.Run(() => store.Volumes(), token);
        var live = await Task.Run(() => volumeService.Detect(), token);
        var ready = new List<string>(); int skipped = 0;
        foreach (var root in roots)
        {
            token.ThrowIfCancellationRequested();
            var previous = saved.Where(v => Paths.Within(root, v.Root)).OrderByDescending(v => v.Root.Length).FirstOrDefault();
            if (previous == null || !live.Any(v => v.Available && v.Id == previous.Id && Paths.Within(root, v.Root))) { skipped++; continue; }
            ready.Add(root);
        }
        if (ready.Count == 0) { vm.Status = "No hay ubicaciones indexadas disponibles. Analiza primero una carpeta o unidad desde Discos. Los índices desconectados se conservan."; return; }
        await ScanRootsCore(ready.ToArray(), token);
        if (!token.IsCancellationRequested) vm.Status += $" · Actualización de índices terminada; {skipped} ubicaciones no disponibles conservadas.";
    });
}
