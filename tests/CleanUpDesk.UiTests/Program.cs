using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using CleanUpDesk.App;
using CleanUpDesk.Core;

internal static class Program
{
    /// <summary>Runs the isolated WPF regression suite, captures binding failures, and asserts that no native playback was attempted.</summary>
    [STAThread]
    static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var fixture = Path.Combine(Path.GetTempPath(), "CleanUpDesk-ui-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var app = new App(false) { ShutdownMode = ShutdownMode.OnExplicitShutdown }; MainWindow? window = null;
        try
        {
            app.InitializeComponent();
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            var model = new MainViewModel(new Settings());
            model.Volumes.Add(new("fixture-volume", @"Z:\", "Prueba con ñ y acentos áéíóú", 1000, 250, true));
            model.Volumes.Add(new("offline-volume", @"Y:\", "Desconectada", 2000, 1500, false));
            model.Volumes.Add(new("reader-volume", @"X:\", "", 0, 0, false) { Connection = VolumeConnection.NoMedia });
            model.Results.Add(new(Path.Combine(fixture, "niño_áéíóú.txt"), fixture, "niño_áéíóú.txt", ".TXT", false, 123, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks, 0, "fixture-volume"));
            model.BrowserItems.Add(model.Results[0]);
            model.BrowserItems.Add(new(Path.Combine(fixture, "Carpeta"), fixture, "Carpeta", "", true, 0, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks, 16, "fixture-volume") { FolderLogicalSize = 1024 });
            model.BrowserItems.Add(new(Path.Combine(fixture, "system.fixture"), fixture, "system.fixture", ".FIXTURE", false, 1, 1, 1, (int)FileAttributes.System, "fixture-volume") { Risk = new("Sistema", "Archivo de sistema de prueba; borrado bloqueado.", true) });
            model.SearchEmpty = false; model.BrowserEmpty = false;
            var store = new IndexStore(Path.Combine(fixture, "index.db"));
            Console.WriteLine("Preparando ventana WPF con datos sintéticos…");
            window = new MainWindow(model, store, false, new IndexStore(Path.Combine(fixture, "browser.db")));
            Console.WriteLine("Mostrando ventana WPF…");
            window.Show();
            Check(window.WindowState == WindowState.Maximized, "La aplicación debe abrirse maximizada.");
            var closeButton = Descendants(window).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Cerrar");
            Pump(window); var closePoint = closeButton.TranslatePoint(new Point(0, 0), window);
            Check(closePoint.Y >= 0 && closePoint.Y + closeButton.ActualHeight <= window.ActualHeight, "Los controles superiores deben quedar dentro de la ventana.");
            window.WindowState = WindowState.Normal;
            Console.WriteLine("Comprobando primer renderizado…");
            Pump(window);
            var tabs = (TabControl)window.FindName("Sections");
            for (int theme = 0; theme < 2; theme++)
            {
                if (theme == 1)
                {
                    tabs.SelectedIndex = 5; Pump(window);
                    var dark = Descendants(window).OfType<CheckBox>().Single(c => (string?)c.Content == "Tema oscuro");
                    dark.IsChecked = false;
                    dark.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
                for (int i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i; Pump(window);
                    Check(((SolidColorBrush)window.Background).Color == ((SolidColorBrush)app.Resources["Bg"]).Color, "El fondo de la ventana no coincide con el tema.");
                    Check(window.IsVisible, "La ventana se cerró durante el renderizado.");
                    Check(errors.Messages.Count == 0, string.Join(Environment.NewLine, errors.Messages));
                    Console.WriteLine($"PASS renderizado WPF: {new[] { "Discos", "Buscar archivos", "Limpieza", "Historial", "Avanzadas", "Ajustes" }[i]} (pasada {theme + 1})");
                    if (i == 0)
                    {
                        ((DataGrid)window.FindName("BrowserResults")).SelectedItem = model.BrowserItems[0]; Pump(window);
                        Check(Descendants(window).OfType<TextBlock>().Any(t => t.Inlines.OfType<System.Windows.Documents.Run>().Any(r => r.Text == ".TXT")), "El panel de detalles no se ha generado.");
                        model.Selected = null; Pump(window);
                        var bars = Descendants(window).OfType<ProgressBar>().Where(p => BindingOperations.GetBinding(p, ProgressBar.ValueProperty)?.Path.Path == "UsedPercent").ToArray();
                        Check(bars.Length == 2, "Deben dibujarse las dos unidades sintéticas.");
                        Check(bars.All(bar => BindingOperations.GetBinding(bar, ProgressBar.ValueProperty)!.Mode == BindingMode.OneWay), "UsedPercent debe ser OneWay.");
                        Check(bars[0].Value == 75 && bars[1].Value == 25, "Los porcentajes de las unidades deben ser 75 y 25.");
                        Check(((ListBox)window.FindName("DriveList")).Items.Count == 2, "Un dispositivo sin medio no debe aparecer como disco utilizable.");
                        Check(model.NoMediaSummary.Contains("X:"), "Se debe explicar el dispositivo sin medio disponible.");
                        Check(Descendants(window).OfType<TextBlock>().Any(t => t.Text.Contains("Explora tus discos")), "No se muestra la vista de exploración.");
                        Check(Descendants((DataGrid)window.FindName("BrowserResults")).OfType<TextBlock>().Any(t => t.Text == Format.Bytes(123)), "Debe mostrarse el tamaño de los archivos.");
                        Check(Descendants((DataGrid)window.FindName("BrowserResults")).OfType<TextBlock>().Any(t => t.Text == Format.Bytes(1024)), "Debe mostrarse el tamaño calculado de las carpetas.");
                        model.Busy = true; Pump(window);
                        var host = Descendants((ListBox)window.FindName("DriveList")).OfType<Border>().Single(b => b.Name == "Host");
                        Check(((SolidColorBrush)host.Background).Color == ((SolidColorBrush)app.Resources["Bg"]).Color, "El contenedor de discos no debe cambiar a blanco al deshabilitarse.");
                        model.Busy = false; Pump(window);
                        var riskGrid = (DataGrid)window.FindName("BrowserResults"); var riskItem = model.BrowserItems[2]; riskGrid.ScrollIntoView(riskItem); Pump(window);
                        var riskRow = (DataGridRow)riskGrid.ItemContainerGenerator.ContainerFromItem(riskItem);
                        Check(((SolidColorBrush)riskRow.Background).Color == ((SolidColorBrush)app.Resources["DangerTint"]).Color, "Los elementos protegidos deben tener fondo de aviso en ambos temas.");
                        riskGrid.SelectedItem = riskItem; Pump(window);
                        Check(Descendants(riskRow).OfType<DataGridCell>().Any(c => ((SolidColorBrush)c.Foreground).Color == ((SolidColorBrush)app.Resources["Danger"]).Color), "El aviso debe conservar contraste al seleccionar la fila.");
                        model.Selected = null;
                    }
                    if (i == 1)
                    {
                        model.SearchInProgress = true; model.SearchStateText = "Buscando · resultados provisionales"; Pump(window);
                        Check(Descendants(window).OfType<ProgressBar>().Any(p => p.IsIndeterminate && p.IsVisible), "La búsqueda en curso debe tener un indicador visible.");
                        Check(Descendants(window).OfType<TextBlock>().Any(t => t.Text == model.SearchStateText), "El estado de búsqueda debe estar junto a los resultados.");
                        Check(Descendants(window).OfType<Button>().Any(b => (b.Content as string) == "Parar búsqueda" && b.IsEnabled && b.IsVisible), "Debe existir una parada visible junto a la carga.");
                        Check(Descendants(window).OfType<TextBlock>().Any(t => t.Text == model.SearchEtaText && t.IsVisible), "Debe mostrarse la estimación de tiempo restante.");
                        model.SearchInProgress = false; model.SearchStateText = "Búsqueda terminada"; Pump(window);
                        Check(Descendants(window).OfType<Button>().Any(b => (b.Content as string) == "Buscar en discos"), "La revisión de discos debe tener una acción explícita.");
                        Check(Descendants(window).OfType<Button>().Any(b => (b.Content as string) == "Revisar carpeta…"), "Debe ser posible incluir otra carpeta y sus descendientes.");
                        ((DataGrid)window.FindName("Results")).SelectedItem = model.Results[0]; Pump(window); model.Selected = null; Pump(window);
                        var filters = (Expander)window.FindName("FiltersPanel");
                        Check(!filters.IsExpanded, "Los filtros avanzados deben empezar recogidos.");
                        filters.IsExpanded = true; Pump(window);
                        var combo = Descendants(window).OfType<ComboBox>().First(c => c.Items.OfType<ComboBoxItem>().Any(item => (string?)item.Content == "Todos"));
                        Check(((SolidColorBrush)combo.Foreground).Color == ((SolidColorBrush)app.Resources["Text"]).Color, "Texto de selección sin contraste.");
                        filters.IsExpanded = false; Pump(window);
                        Check(((DataGrid)window.FindName("Results")).ActualHeight >= 250, "Los filtros no deben desplazar la tabla.");
                    }
                }
            }
            tabs.SelectedIndex = 1; Pump(window);
            var grid = (DataGrid)window.FindName("Results");
            Check(((ComboBox)window.FindName("QuickScope")).SelectedItem?.ToString() == "Todos los discos", "El selector no debe mostrar un objeto técnico.");
            window.Width = 1100; window.Height = 760; Pump(window);
            Check(grid.ActualHeight >= 220, "La tabla debe seguir siendo utilizable en la ventana mínima.");
            Check(grid.Columns.Any(c => (string?)c.Header == "Tamaño lógico"), "La ñ o los acentos están dañados.");
            Check(Descendants(window).OfType<TextBlock>().Any(t => t.Text == "niño_áéíóú.txt"), "No se muestra el nombre Unicode del archivo.");
            var final = new FinalWindow(window, "Archivo ficticio niño_áéíóú.txt", true); final.Show(); Pump(final);
            Check(Descendants(final).OfType<Button>().Where(b => (b.Content as string) == "Eliminar definitivamente").All(b => !b.IsEnabled && !b.IsDefault), "La eliminación debe estar deshabilitada sin frase.");
            final.Close();
            model.Section = 0; Pump(window);
            var calculate = Descendants(window).OfType<Button>().Single(b => (b.Content as string) == "Calcular tamaño");
            var pages = Descendants(window).OfType<TextBlock>().Single(t => t.Text == model.BrowserCount);
            Check(calculate.TranslatePoint(new Point(0, 0), window).X - pages.TranslatePoint(new Point(pages.ActualWidth, 0), window).X >= 24, "Calcular tamaño debe tener margen respecto al contador.");
            model.Section = 2; Pump(window);
            Check(Descendants(window).OfType<Button>().Any(b => (b.Content as string) == "Analizar caché") && window.FindName("ForgottenResults") is DataGrid, "Limpieza debe incluir caché y archivos por revisar.");
            WaitFor(window.RefreshForgottenAsync()); Check(model.ForgottenSummary.Contains("Actualizado"), "La revisión de candidatos debe actualizar fecha y alcance.");
            CheckStoppingSearch(app, fixture);
            CheckSearchFolderNavigation(fixture);
            CheckProtectedDeletion(fixture);
            CheckAdvancedFunctions(fixture);
            CheckProcessNotifications(fixture);
            CheckSafeClosing(fixture);
            CheckClosingDeletion(fixture);
            CheckWarningFilterAndLargest(fixture);
            CheckCompletionAudio(fixture);
            CheckDuplicateExpanders(fixture);
            CheckAudioTaskProvenance(fixture);
            CheckDuplicateFullPathLayout(fixture);
            Check(CompletionSound.NativePlaybackAttempts == 0, "Las pruebas aisladas no deben reproducir sonido real en ningún momento.");
            Console.WriteLine("PASS aislamiento de audio: 0 intentos de reproducción nativa en toda la suite WPF.");
            Console.WriteLine("PASS UsedPercent de solo lectura, textos españoles, nombre Unicode y confirmación final sin ejecución destructiva.");
            window.Close(); app.Shutdown(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("FAIL presentación WPF: " + ex); window?.Close(); app.Shutdown(); return 1; }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Path.GetFileName(fixture).StartsWith("CleanUpDesk-ui-tests-", StringComparison.Ordinal) && Paths.Within(fixture, Paths.Normalize(Path.GetTempPath()))) Directory.Delete(fixture, true);
        }
    }
    /// <summary>Fails the current UI regression with the supplied diagnostic when its invariant does not hold.</summary>
    static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    /// <summary>Processes pending Dispatcher/layout work so visual assertions observe the current window state.</summary>
    static void Pump(Window window) { window.UpdateLayout(); }
    /// <summary>Verifies real provisional search results, cancellation, independent browsing, and exactly-once completion callbacks.</summary>
    static void CheckStoppingSearch(App app, string fixture)
    {
        var root = Path.Combine(fixture, "parada"); var slow = Path.Combine(root, "Lenta"); Directory.CreateDirectory(slow);
        var file = Path.Combine(root, "borrrar.txt"); File.WriteAllText(file, "Archivo exclusivo de prueba."); File.WriteAllBytes(Path.Combine(slow, "real.bin"), new byte[1024]);
        var index = new IndexStore(Path.Combine(fixture, "stop-index.db")); var feed = new StopFeed();
        using var session = new SearchIndexSession(index, new SlowTree(slow), (_, _) => feed);
        var model = new MainViewModel(new Settings()) { Name = "borrrar", Folder = root, Section = 1 };
        int sounds = 0;
        var testWindow = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "stop-browser.db")), session, () => sounds++); testWindow.Show(); Pump(testWindow);
        Check(testWindow.Icon != null, "La ventana debe cargar el icono propio.");
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(testWindow.Dispatcher));
        var run = testWindow.SearchCurrentAsync(true); bool pressed = false, reading = false, concurrent = false; Exception? readError = null; var clock = Stopwatch.StartNew(); var frame = new DispatcherFrame();
        var watchdog = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(100) };
        watchdog.Tick += async (_, _) =>
        {
            if (!pressed && !reading && model.Results.Any(e => e.Path == file))
            {
                reading = true;
                try
                {
                    Check(model.SearchInProgress && !model.Busy, "La búsqueda no debe bloquear el carril de exploración.");
                    model.Section = 0; Pump(testWindow); await testWindow.ExploreFolderAsync(root);
                    model.BrowserSelected = model.BrowserItems.Single(e => e.Path == slow); await testWindow.CalculateSelectedFolderAsync();
                    Check(model.SearchInProgress && model.BrowserItems.Single(e => e.Path == slow).FolderLogicalSize == 1024, "Debe poder explorar y calcular tamaño mientras sigue la búsqueda.");
                    concurrent = true; model.Section = 1; Pump(testWindow); pressed = true;
                    Descendants(testWindow).OfType<Button>().Single(b => (b.Content as string) == "Parar búsqueda").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                }
                catch (Exception ex) { readError = ex; frame.Continue = false; }
            }
            if (run.IsCompleted || clock.Elapsed > TimeSpan.FromSeconds(12)) frame.Continue = false;
        };
        watchdog.Start(); Dispatcher.PushFrame(frame); watchdog.Stop();
        if (readError != null) throw readError;
        Check(run.IsCompleted && concurrent, "La búsqueda de prueba no se detuvo a tiempo o no permitió explorar."); run.GetAwaiter().GetResult();
        Check(pressed && model.SearchStateText.Contains("detenida") && !model.Busy && !model.SearchInProgress, $"La parada debe terminar el trabajo y liberar la interfaz. pressed={pressed}; fase={model.SearchStateText}; estado={model.Status}");
        Check(model.Results.Any(e => e.Path == file) && feed.Disposed, "La parada debe conservar la coincidencia y liberar la vigilancia.");
        Check(sounds == 1, "Solo el cálculo de tamaño completado debe sonar; parar la búsqueda no añade un aviso.");
        var beforeFast = index.Search(new() { Name = "dato-" }).Total;
        var next = testWindow.SearchCurrentAsync();
        Check(!model.SearchCoverage.Contains("detenida") && !model.SearchCoverageDetails.Contains("detenida"), "Una búsqueda nueva debe retirar el aviso anterior inmediatamente.");
        WaitFor(next); Check(model.SearchStateText.Contains("terminada") && sounds == 2 && !model.SearchCoverage.Contains("detenida"), "Al completar suena una vez y la cobertura deja de indicar cancelación.");
        Check(index.Search(new() { Name = "dato-" }).Total == beforeFast && model.SearchCoverage.Contains("Índice guardado"), "Buscar rápido no debe reiniciar el recorrido lento ni anunciar cobertura actualizada.");
        model.Settings.SearchCompletionSound = false;
        WaitFor(testWindow.SearchCurrentAsync()); Check(sounds == 2, "El ajuste de silencio debe respetarse.");
        testWindow.Close(); Console.WriteLine("PASS búsqueda WPF real: resultado provisional, Parar búsqueda, resultado conservado y recursos liberados.");
        Console.WriteLine("PASS nueva búsqueda retira cancelación, icono cargado y aviso solo al finalizar con sonido activado.");
        Console.WriteLine("PASS exploración y cálculo real de carpeta mientras continúa una búsqueda lenta.");
    }
    /// <summary>Pumps the Dispatcher while awaiting a bounded test task and propagates its failure.</summary>
    static void WaitFor(Task task)
    {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { if (task.IsCompleted || clock.Elapsed.TotalSeconds > 12) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(task.IsCompleted, "La operación no terminó a tiempo."); task.GetAwaiter().GetResult();
    }
    /// <summary>Verifies folder-result navigation preserves filters, pagination, selection, and existing files.</summary>
    static void CheckSearchFolderNavigation(string fixture)
    {
        var root = Path.Combine(fixture, "result-navigation"); Directory.CreateDirectory(root);
        var volume = new VolumeService().ForPath(root); var index = new IndexStore(Path.Combine(fixture, "navigation-index.db"));
        var entries = new List<Entry>();
        for (int i = 0; i < 221; i++)
        {
            var folder = Path.Combine(root, $"Carpeta {i:D3}"); Directory.CreateDirectory(folder);
            entries.Add(Paths.ReadEntry(folder, volume.Id));
            File.WriteAllText(Path.Combine(folder, "niño_áéíóú.txt"), "Archivo exclusivo de prueba.");
        }
        index.WriteBatch(entries);
        var model = new MainViewModel(new Settings { SearchCompletionSound = false }) { Section = 1, Name = "Carpeta", Folder = root, Kind = 2 };
        model.Volumes.Add(volume);
        var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "navigation-browser.db"))); window.Show(); Pump(window);
        try
        {
            WaitFor(window.SearchCurrentAsync());
            Descendants(window).OfType<Button>().Single(b => (b.Content as string) == "Siguiente →").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => !model.Busy && model.ResultCount.Contains("página 2"));
            var grid = (DataGrid)window.FindName("Results"); var folder = model.Results[0];
            grid.SelectedItem = folder; grid.ScrollIntoView(folder); Pump(window);
            var snapshot = model.Results.ToArray(); var count = model.ResultCount; var phase = model.SearchStateText;
            var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(folder);
            grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent, Source = row });
            PumpUntil(() => !model.Busy && model.Section == 0 && model.BrowserAddress == folder.Path);
            Check(model.BrowserItems.Any(e => e.Name == "niño_áéíóú.txt"), "El doble clic debe abrir el contenido real de la carpeta en Discos.");
            var child = model.BrowserItems.Single();
            WaitFor(window.OpenSearchResultAsync(child)); Check(model.Section == 0 && File.Exists(child.Path), "Abrir un archivo no debe ejecutarlo, eliminarlo ni cambiar de sección.");
            model.Section = 1; Pump(window);
            Check(model.Results.SequenceEqual(snapshot) && model.ResultCount == count && model.SearchStateText == phase, "Volver a Búsqueda debe conservar resultados, página y estado, sin repetir la consulta.");
            Check(model.Name == "Carpeta" && model.Folder == root && model.Kind == 2 && model.ResultSelected == folder && ReferenceEquals(grid.SelectedItem, folder), "La navegación debe conservar filtros y selección de resultados.");
            Console.WriteLine("PASS doble clic real de resultados: abre carpeta en Discos y conserva filtros, página 2, resultados y selección al volver.");
        }
        finally { window.Close(); }
    }
    /// <summary>Pumps UI events until the expected test state appears or the bounded wait fails.</summary>
    static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame(); var clock = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(50) };
        timer.Tick += (_, _) => { if (condition() || clock.Elapsed.TotalSeconds > 12) frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame); timer.Stop(); Check(condition(), "La navegación no terminó a tiempo.");
    }
    /// <summary>Verifies protected-row styling and all UI deletion entry points refuse protected fixture targets.</summary>
    static void CheckProtectedDeletion(string fixture)
    {
        var root = Path.Combine(fixture, "protected-ui"); Directory.CreateDirectory(root); var file = Path.Combine(root, "system.fixture"); File.WriteAllText(file, "fixture"); File.SetAttributes(file, FileAttributes.System);
        var model = new MainViewModel(new Settings()); var notices = new List<ProtectedTarget>();
        var window = new MainWindow(model, new IndexStore(Path.Combine(fixture, "protected-ui.db")), false, new IndexStore(Path.Combine(fixture, "protected-ui-browser.db")), onProtectedDeletion: items => notices.AddRange(items)); window.Show(); Pump(window);
        try
        {
            WaitFor(window.ExploreFolderAsync(root)); var grid = (DataGrid)window.FindName("BrowserResults"); var item = model.BrowserItems.Single();
            Check(item.HasWarning && item.Risk.Label == "Sistema", "La exploración real debe clasificar el atributo de sistema.");
            grid.SelectedItem = item; Pump(window);
            Descendants(window).OfType<Button>().Single(b => (b.Content as string) == "Revisar eliminación…").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => !model.Busy && notices.Count == 1);
            grid.SelectedItem = model.BrowserItems.Single(); Pump(window);
            grid.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(grid)!, Environment.TickCount, Key.Delete) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            PumpUntil(() => !model.Busy && notices.Count == 2);
            WaitFor(window.RequestDeletionAsync([item])); Check(notices.Count == 3 && notices.All(n => n.Path == file) && File.Exists(file), "Botón, Supr y flujo común deben bloquear el mismo destino sin borrar.");
            var notice = new ProtectionNoticeWindow(window, [new(file, "Sistema de prueba")]); notice.Show(); Pump(notice);
            var buttons = Descendants(notice).OfType<Button>().ToArray();
            Check(buttons.Length == 2 && buttons.Any(b => (b.Content as string) == "Copiar rutas") && buttons.Any(b => b.IsCancel && b.IsKeyboardFocused) && buttons.All(b => !b.IsDefault), "El aviso solo debe permitir copiar o cerrar, con foco seguro y sin autorizar el borrado.");
            notice.Close(); Console.WriteLine("PASS protección WPF: colores en ambos temas, botón/Supr/flujo común bloqueados y aviso con copiar rutas sin anulación.");
        }
        finally { window.Close(); File.SetAttributes(file, FileAttributes.Normal); }
    }
    /// <summary>Verifies real advanced findings, retention decisions, empty-folder mode, and unchanged search state.</summary>
    static void CheckAdvancedFunctions(string fixture)
    {
        var root = Path.Combine(fixture, "advanced-ui"); Directory.CreateDirectory(root); var empty = Path.Combine(root, "Empty"); Directory.CreateDirectory(empty);
        foreach (var name in new[] { "a.txt", "b.txt", "c.txt" }) File.WriteAllText(Path.Combine(root, name), "abc");
        File.SetLastWriteTimeUtc(Path.Combine(root, "a.txt"), DateTime.UtcNow.AddDays(-20)); File.SetLastWriteTimeUtc(Path.Combine(root, "b.txt"), DateTime.UtcNow.AddDays(-10)); File.WriteAllBytes(Path.Combine(root, "zero.txt"), []);
        var volume = new VolumeService().ForPath(root); var index = new IndexStore(Path.Combine(fixture, "advanced-ui-index.db")); WaitFor(new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default));
        var cache = new AdvancedResultStore(Path.Combine(fixture, "advanced-ui-results.db")); var model = new MainViewModel(new Settings { SearchCompletionSound = false }) { Section = 4, AdvancedFolder = root, DuplicateMode = 2 };
        model.Volumes.Add(volume); var searchResult = Paths.ReadEntry(Path.Combine(root, "a.txt"), volume.Id); model.Results.Add(searchResult);
        var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "advanced-ui-browser.db")), advancedCache: cache); window.Show(); Pump(window);
        try
        {
            WaitFor(window.AnalyzeAdvancedAsync()); Check(model.AdvancedRows.Count == 3 && model.AdvancedRows.All(r => r.Hash?.Length == 64) && model.AdvancedStatus.Contains("Análisis terminado"), "La vista debe mostrar duplicados SHA256 reales. " + model.AdvancedStatus);
            Descendants(window).OfType<Button>().Single(b => (b.Content as string) == "Aplicar conservación").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => !model.Busy && model.AdvancedRows.Count(r => r.Keep) == 1); Check(model.AdvancedRows.Single(r => r.Keep).Name == "c.txt", "La regla reciente debe conservar la copia modificada más recientemente.");
            Descendants(window).OfType<Button>().Single(b => (b.Content as string) == "Expandir grupos").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(window);
            var grid = (DataGrid)window.FindName("AdvancedResults"); grid.SelectedItem = model.AdvancedRows.Single(r => r.Name == "a.txt"); Pump(window);
            Descendants(window).OfType<Button>().Single(b => (b.Content as string) == "Esta es la original").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => !model.Busy && model.AdvancedRows.Any(r => r.Name == "a.txt" && r.Keep && r.Original));
            SaveAdvancedPreview(window, "advanced-dark.png");
            model.Section = 5; Pump(window); var theme = Descendants(window).OfType<CheckBox>().Single(c => (string?)c.Content == "Tema oscuro"); theme.IsChecked = false; theme.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); model.Section = 4; Pump(window);
            SaveAdvancedPreview(window, "advanced-light.png");
            model.AdvancedMode = 1; WaitFor(window.AnalyzeAdvancedAsync()); Check(model.AdvancedRows.Count == 2 && model.AdvancedRows.Any(r => r.IsDirectory && r.Path == empty), "La vista debe detectar archivo de 0 bytes y carpeta vacía.");
            var service = new DeletionService(index, new VolumeService(), new SafetyPolicy(), new WindowsDeleteBackend()); DeleteReview? review = null;
            WaitFor(Task.Run(async () => review = await service.PrepareAsync([empty], DeleteMode.Recycle)));
            var dialog = new ReviewWindow(window, review!, true); dialog.Show(); Pump(dialog);
            var next = Descendants(dialog).OfType<Button>().Single(b => (b.Content as string) == "Continuar a confirmación"); Check(!next.IsEnabled && Descendants(dialog).OfType<ComboBox>().Single().SelectedIndex == -1, "El modo permanente de carpetas vacías debe elegirse explícitamente.");
            Descendants(dialog).OfType<ComboBox>().Single().SelectedIndex = 0; Pump(dialog); Check(next.IsEnabled && dialog.Mode == DeleteMode.Permanent, "La elección explícita permite pasar a la confirmación final."); dialog.Close();
            model.AdvancedMode = 2; WaitFor(window.AnalyzeAdvancedAsync()); Check(model.AdvancedRows.Count == 4 && model.AdvancedStatus.Contains("Top 100") && model.AdvancedRows.All(r => !r.IsDirectory), "Top 100 debe mostrar archivos del índice.");
            Check(model.Results.Single() == searchResult && File.Exists(searchResult.Path) && Directory.Exists(empty), "Analizar y aplicar reglas no deben borrar ni sustituir los resultados de Búsqueda.");
            Console.WriteLine("PASS WPF avanzado: SHA256 real, conservar reciente/original manual, vacíos, Top 100 y modo permanente sin selección automática.");
        }
        finally { window.Close(); }
    }
    /// <summary>Verifies pending-section markers, silent mode, completion callbacks, and fixture-only global index updates.</summary>
    static void CheckProcessNotifications(string fixture)
    {
        var root = Path.Combine(fixture, "notifications"); Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "one.txt"), "abc"); File.WriteAllText(Path.Combine(root, "two.txt"), "abc");
        var volume = new VolumeService().ForPath(root); var index = new IndexStore(Path.Combine(fixture, "notifications.db")); index.RememberVolumes([volume]);
        WaitFor(new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default));
        var offline = new Volume("not-the-real-volume", @"Z:\", "Fixture desconectada", 0, 0, false); index.RememberVolumes([offline]); index.ScanFinished(new(0, 0, false, @"Z:\OfflineFixture", TimeSpan.Zero));
        var model = new MainViewModel(new Settings()) { Section = 1, Folder = root, AdvancedFolder = root, DuplicateMode = 2 }; model.Volumes.Add(volume);
        int sounds = 0;
        var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "notifications-browser.db")), onSearchCompleted: () => sounds++, advancedCache: new AdvancedResultStore(Path.Combine(fixture, "notifications-advanced.db")));
        window.Show(); Pump(window); var tabs = (TabControl)window.FindName("Sections");
        // Summary: Reads a sidebar section notification flag for the current UI assertion.
        bool Pending(int i) => SectionNotice.GetPending((TabItem)tabs.Items[i]);
        try
        {
            WaitFor(window.SearchCurrentAsync()); Check(sounds == 1 && !Pending(1), "Una búsqueda visible suena una vez sin marcar la sección.");
            var searching = window.SearchCurrentAsync(); model.Section = 0; Pump(window); WaitFor(searching);
            Check(sounds == 2 && Pending(1) && !Pending(0), "Una búsqueda que termina en otra pestaña debe marcar solo su sección.");
            var halo = Descendants((TabItem)tabs.Items[1]).OfType<CompletionHalo>().Single(); Check(halo.IsVisible, "El borde de aviso debe ser visible.");
            if (SystemParameters.ClientAreaAnimation) { var phase = halo.Phase; PumpUntil(() => halo.Phase != phase); }
            SaveAdvancedPreview(window, "notifications-dark.png");
            model.Section = 1; Pump(window); Check(!Pending(1) && !halo.IsVisible, "Volver por teclado o ratón retira el aviso y su animación.");
            model.Section = 4; Pump(window); var advanced = window.AnalyzeAdvancedAsync(); model.Section = 0; Pump(window);
            Check(!((Button)window.FindName("RefreshIndicesButton")).IsEnabled, "Actualizar índices se bloquea durante otro análisis.");
            WaitFor(advanced); Check(sounds == 3 && Pending(4), "Duplicados terminados fuera de Avanzadas deben sonar y marcarse.");
            model.Section = 5; Pump(window); var theme = Descendants(window).OfType<CheckBox>().Single(c => (string?)c.Content == "Tema oscuro"); theme.IsChecked = false; theme.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); SaveAdvancedPreview(window, "notifications-light.png"); Check(Pending(4), "Cambiar a otra sección conserva el aviso pendiente.");
            model.Section = 4; Pump(window); model.Settings.SearchCompletionSound = false; model.AdvancedMode = 1;
            var empty = window.AnalyzeAdvancedAsync(); model.Section = 0; Pump(window); WaitFor(empty); Check(sounds == 3 && Pending(4), "Silenciar el sonido no debe desactivar la señal visual de vacíos.");
            model.Section = 4; Pump(window); model.AdvancedFolder = "ruta no absoluta"; WaitFor(window.AnalyzeAdvancedAsync()); Check(sounds == 3 && !Pending(4) && model.AdvancedCount == "No se pudo completar", "Un error no genera aviso de éxito.");
            model.AdvancedFolder = root; model.Settings.SearchCompletionSound = true;
            File.WriteAllText(Path.Combine(root, "new.txt"), "nuevo");
            var refresh = (Button)window.FindName("RefreshIndicesButton"); Check(refresh.IsVisible && refresh.IsEnabled, "La actualización global debe estar disponible desde Avanzadas.");
            WaitFor(window.RefreshIndicesAsync());
            Check(index.Search(new() { Folder = root }).Total == 3 && index.IndexedLocations().Contains(@"Z:\OfflineFixture"), "Actualizar incorpora archivos nuevos solo en alcances conocidos y conserva el índice desconectado.");
            Check(sounds == 4 && Pending(0) && model.Section == 4, "Actualizar desde otra pestaña avisa en Discos sin cambiar de sección.");
            model.Section = 2; Pump(window); Check(refresh.IsVisible && Pending(0), "El botón global y el aviso siguen accesibles desde Limpieza.");
            model.Section = 0; Pump(window); Check(!Pending(0), "Consultar Discos reconoce la actualización terminada.");
            Console.WriteLine("PASS avisos WPF: sonido único, fuera/dentro de sección, halo animado, lectura, silencio, error y ambos temas; actualización global real limitada a fixtures y conservación offline.");
        }
        finally { window.Close(); }
    }
    /// <summary>Verifies cancellation of a close request preserves work and confirmed closing awaits all active fixture lanes.</summary>
    static void CheckSafeClosing(string fixture)
    {
        var root = Path.Combine(fixture, "safe-closing"); Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "fixture.txt"), "fixture");
        var index = new IndexStore(Path.Combine(fixture, "safe-closing.db")); using var tree = new ClosingTree(root); var feed = new StopFeed();
        using var session = new SearchIndexSession(index, tree, (_, _) => feed);
        var model = new MainViewModel(new Settings()) { Section = 1, Folder = root }; bool accept = false; int questions = 0;
        var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "safe-closing-browser.db")), session, onCloseWithProcesses: () => { questions++; return accept; });
        window.Show(); Pump(window);
        // Exercise the actual dialog without triggering deletion or exiting the test.
        var notice = new CloseProcessesWindow(window); notice.Show(); Pump(notice);
        var cancelButton = Descendants(notice).OfType<Button>().Single(b => (b.Content as string) == "Cancelar");
        var closeButton = Descendants(notice).OfType<Button>().Single(b => (b.Content as string) == "Cerrar igualmente");
        Check(cancelButton.IsDefault && cancelButton.IsCancel && cancelButton.IsKeyboardFocused && !closeButton.IsDefault, "Cancelar debe ser la opción segura por defecto del aviso de cierre."); notice.Close();
        var search = window.SearchCurrentAsync(true); PumpUntil(() => tree.Entered.IsSet);
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); bool safeFinished = false, tokenCancelled = false, closed = false;
        Func<CancellationToken, Task> diskWork = async token =>
        {
            try { await Task.Run(() => { started.Set(); if (!release.Wait(TimeSpan.FromSeconds(12))) throw new IOException("La prueba no liberó su operación."); tokenCancelled = token.IsCancellationRequested; token.ThrowIfCancellationRequested(); }); }
            finally { safeFinished = true; }
        };
        var runMethod = typeof(MainWindow).GetMethod("Run", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var work = (Task)runMethod.Invoke(window, [diskWork, true])!; PumpUntil(() => started.IsSet);
        window.Closed += (_, _) => { Check(safeFinished && work.IsCompleted && search.IsCompleted && feed.Disposed, "No se deben liberar recursos ni cerrar antes de terminar ambos carriles."); closed = true; };
        try
        {
            window.Close(); Pump(window); Check(questions == 1 && !closed && window.IsEnabled && !work.IsCompleted && !search.IsCompleted, "Cancelar el cierre conserva la ventana y todos los procesos activos.");
            accept = true; window.Close(); Pump(window);
            Check(questions == 2 && !closed && !window.IsEnabled, "Cerrar igualmente debe esperar al trabajo bloqueado sin forzar la salida.");
            bool newWork = false; WaitFor((Task)runMethod.Invoke(window, [new Func<CancellationToken, Task>(_ => { newWork = true; return Task.CompletedTask; }), true])!); Check(!newWork, "No se admiten operaciones nuevas al cerrar.");
            window.Close(); Pump(window); Check(questions == 2 && !closed, "Un segundo cierre durante la espera no repite el aviso ni fuerza la salida.");
            release.Set(); WaitFor(work); Check(tokenCancelled && safeFinished && !closed, "Se cancela el carril de operaciones pero se sigue esperando a la búsqueda.");
            tree.Release.Set(); WaitFor(search); PumpUntil(() => closed);
            Check(!model.Busy && !model.SearchInProgress, "El cierre automático espera a los estados finales.");
        }
        finally { release.Set(); tree.Release.Set(); if (!closed) { accept = true; window.Close(); } }
        Console.WriteLine("PASS cierre WPF: Cancelar por defecto, conservar procesos, cancelar todos los carriles, esperar operaciones no interrumpibles, impedir nuevos trabajos y cerrar automáticamente una sola vez.");
    }
    /// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
    sealed class ClosingTree(string root) : ITreeReader, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        /// <summary>Enumerates owned fixture children while applying this test reader's controlled delay, interruption, or cancellation behavior.</summary>
        public IEnumerable<string> Children(string path) { if (path == root) { Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(12))) throw new IOException("La prueba no liberó el recorrido."); } return Directory.EnumerateFileSystemEntries(path); }
        /// <summary>Returns fixture metadata or injects the isolated read failure required by this test reader.</summary>
        public Entry Read(string path, string volume) => Paths.ReadEntry(path, volume);
        /// <summary>Releases fixture synchronization or fake feed/audio resources without touching user data.</summary>
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
    /// <summary>Verifies closing finishes one already-started fixture deletion, skips the next, and commits results before exit.</summary>
    static void CheckClosingDeletion(string fixture)
    {
        var root = Path.Combine(fixture, "closing-delete"); Directory.CreateDirectory(root); var first = Path.Combine(root, "a.txt"); var second = Path.Combine(root, "b.txt"); File.WriteAllText(first, "fixture"); File.WriteAllText(second, "fixture");
        var index = new IndexStore(Path.Combine(fixture, "closing-delete.db")); var volume = new VolumeService().ForPath(root); WaitFor(new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default));
        using var backend = new ClosingFixtureBackend(root); var service = new DeletionService(index, new VolumeService(), new SafetyPolicy(), backend);
        DeleteReview? review = null; WaitFor(Task.Run(async () => review = await service.PrepareAsync([first, second], DeleteMode.Permanent)));
        service.CompleteReview(review!, [first, second]); var permit = service.Confirm(review!, true, "ELIMINAR DEFINITIVAMENTE");
        var model = new MainViewModel(new Settings()); var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "closing-delete-browser.db")), onCloseWithProcesses: () => true);
        window.Show(); Pump(window); bool closed = false; window.Closed += (_, _) => closed = true;
        IReadOnlyList<OperationResult>? results = null;
        Func<CancellationToken, Task> delete = async token => results = await service.ExecuteAsync(permit, false, token);
        var runMethod = typeof(MainWindow).GetMethod("Run", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var work = (Task)runMethod.Invoke(window, [delete, false])!;
        try
        {
            PumpUntil(() => backend.Entered.IsSet); window.Close(); Pump(window);
            Check(!closed && !window.IsEnabled && File.Exists(first), "Cerrar durante el borrado espera a la operación de Windows ya iniciada.");
            backend.Release.Set(); WaitFor(work); PumpUntil(() => closed);
            Check(backend.Calls == 1 && !File.Exists(first) && File.Exists(second), "La cancelación termina el elemento iniciado y no borra el siguiente.");
            Check(results?.Count(r => r.Status == "Eliminado") == 1 && results.Count(r => r.Status == "Omitido") == 1 && index.Search(new() { Folder = root }).Total == 1 && index.History().Count == 2, "Antes de cerrar se actualizan índice e historial de eliminados y omitidos.");
        }
        finally { backend.Release.Set(); if (!closed) window.Close(); }
        Console.WriteLine("PASS cierre durante borrado real de fixture: espera al elemento iniciado, conserva el siguiente, registra resultados y actualiza el índice antes de salir.");
    }
    /// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
    sealed class ClosingFixtureBackend(string root) : IDeleteBackend, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new(); public ManualResetEventSlim Release { get; } = new(); public int Calls;
        /// <summary>Deletes only an explicitly owned fixture file after validating the test backend boundary and mode.</summary>
        public void Delete(string path, bool directory, DeleteMode mode)
        {
            if (!Paths.Within(path, root) || directory || mode != DeleteMode.Permanent) throw new IOException("Fuera de la fixture de borrado.");
            Entered.Set(); if (!Release.Wait(TimeSpan.FromSeconds(12))) throw new IOException("La prueba no liberó su borrado."); File.Delete(path); Calls++;
        }
        /// <summary>Releases fixture synchronization or fake feed/audio resources without touching user data.</summary>
        public void Dispose() { Entered.Dispose(); Release.Dispose(); }
    }
    /// <summary>Verifies the shared warning filter and Top 100 replacement behavior across all file-list views.</summary>
    static void CheckWarningFilterAndLargest(string fixture)
    {
        var root = Path.Combine(fixture, "warning-top"); Directory.CreateDirectory(root); var paths = new List<string>();
        for (int i = 0; i < 140; i++) { var p = Path.Combine(root, $"{i:D3}.bin"); File.WriteAllBytes(p, new byte[1000 - i]); if (i < 40) File.SetAttributes(p, FileAttributes.ReadOnly); paths.Add(p); }
        var volume = new VolumeService().ForPath(root); var index = new IndexStore(Path.Combine(fixture, "warning-top.db")); WaitFor(new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default));
        var model = new MainViewModel(new Settings { SearchCompletionSound = false }) { Section = 4, AdvancedMode = 2, AdvancedFolder = root };
        model.Volumes.Add(volume); model.Volumes.Add(new("fake-other", @"Z:\", "Otra unidad de fixture", 0, 0, false));
        var good = Paths.ReadEntry(paths[100], volume.Id); var warning = Paths.ReadEntry(paths[0], volume.Id) with { Risk = new("Solo lectura", "Fixture", true) };
        model.Results.Add(good); model.Results.Add(warning); model.BrowserItems.Add(good); model.BrowserItems.Add(warning);
        model.CleanupPaths.Add(good); model.CleanupPaths.Add(warning); model.BrowserCachePaths.Add(good); model.BrowserCachePaths.Add(warning);
        model.ForgottenFiles.Add(new(good, null)); model.ForgottenFiles.Add(new(warning, null));
        var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "warning-browser.db")), advancedCache: new AdvancedResultStore(Path.Combine(fixture, "warning-advanced.db"))); window.Show(); Pump(window);
        try
        {
            var tabs = (TabControl)window.FindName("Sections"); Check(System.Windows.Automation.AutomationProperties.GetName((TabItem)tabs.Items[^1]) == "Ajustes" && System.Windows.Automation.AutomationProperties.GetName((TabItem)tabs.Items[4]) == "Funciones avanzadas", "Ajustes debe ser siempre la última sección.");
            WaitFor(window.AnalyzeAdvancedAsync()); Check(model.AdvancedRows.Count == 100 && model.AdvancedRows.Count(r => r.HasWarning) == 40, "Top sin filtro incluye los 40 avisos y 60 aptos.");
            WaitFor(window.SetWarningFilterAsync(true)); Pump(window);
            Check(model.AdvancedRows.Count == 100 && model.AdvancedRows.All(r => !r.HasWarning) && model.AdvancedRows[0].Name == "040.bin" && model.AdvancedRows[^1].Name == "139.bin", "Ocultar avisos debe recomponer los 100 mayores aptos en orden.");
            foreach (var list in new System.Collections.IEnumerable[] { model.Results, model.BrowserItems, model.CleanupPaths, model.BrowserCachePaths, model.ForgottenFiles }) Check(System.Windows.Data.CollectionViewSource.GetDefaultView(list).Cast<object>().Count() == 1, "El filtro debe alcanzar todas las listas de archivos.");
            Check(((DataGrid)window.FindName("AdvancedResults")).Items.Count == 100 && ((CheckBox)window.FindName("HideWarningsToggle")).IsVisible, "El Top y el filtro global deben estar conectados a controles reales.");
            SaveAdvancedPreview(window, "largest-filter-dark.png");
            model.Section = 5; Pump(window); var theme = Descendants(window).OfType<CheckBox>().Single(c => (string?)c.Content == "Tema oscuro"); theme.IsChecked = false; theme.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); model.Section = 4; Pump(window); SaveAdvancedPreview(window, "largest-filter-light.png");
            // Regression: retaining a folder from another volume used to produce no results.
            var scope = (ComboBox)window.FindName("AdvancedVolume"); model.AdvancedFolder = @"Z:\CarpetaAnterior"; scope.SelectedIndex = 1; Pump(window);
            Check(model.AdvancedFolder == "", "Elegir otro disco debe retirar una carpeta incompatible.");
            model.AdvancedFolder = root; WaitFor(window.AnalyzeAdvancedAsync()); Check(model.AdvancedRows.Count == 100, "Top del disco del sistema funciona con su carpeta indexada y filtro de aptos.");
            model.AdvancedFolder = @"Z:\OtraCarpeta"; WaitFor(window.AnalyzeAdvancedAsync()); Check(model.AdvancedCount == "No se pudo completar" && model.AdvancedStatus.Contains("otra unidad"), "Un cruce de disco/carpeta introducido después debe explicar la causa en vez de anunciar cero archivos.");
            model.AdvancedFolder = root; WaitFor(window.SetWarningFilterAsync(false));
            Check(System.Windows.Data.CollectionViewSource.GetDefaultView(model.Results).Cast<object>().Count() == 2 && File.Exists(paths[0]), "Desactivar el filtro recupera avisos sin borrar archivos.");
            Console.WriteLine("PASS WPF filtro global en seis listas, Top 100 repuesto 40+60→100 aptos, temas, Ajustes al final y cruce de carpeta/disco explicado.");
        }
        finally { window.Close(); foreach (var p in paths) File.SetAttributes(p, FileAttributes.Normal); }
    }
    /// <summary>Verifies expiry, stop/disposal guards, and worker-side completion while the Dispatcher is deliberately blocked.</summary>
    static void CheckCompletionAudio(string fixture)
    {
        long now = Stopwatch.GetTimestamp(); var old = now; var backend = new TestAudio(); var player = new CompletionSound(backend, () => now);
        player.Play(now); Check(backend.Plays == 1 && backend.Active, "Un aviso vigente debe reproducirse inmediatamente.");
        now += Stopwatch.Frequency * 180; player.Play(old); Check(backend.Plays == 1, "Un aviso retrasado tres minutos debe descartarse, no reproducirse después.");
        player.Dispose(); player.Play(now); player.Dispose(); Check(!backend.Active && backend.Stops == 1 && backend.Disposals == 1 && backend.Plays == 1, "Cerrar corta el audio una vez y prohíbe posteriores reproducciones.");

        var counts = new TestAudio(); using var audio = new CompletionSound(counts);
        var signal = new CompletionSignal(audio.Play, () => true);
        signal.Watch(Task.FromResult(1), default).GetAwaiter().GetResult(); signal.Watch(Task.FromResult(2), default).GetAwaiter().GetResult();
        Check(counts.Plays == 1, "Un proceso solo puede intentar emitir un aviso, sin duplicarlo al volver a la interfaz.");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        new CompletionSignal(audio.Play, () => true).Watch(Task.FromResult(1), cancellation.Token).GetAwaiter().GetResult();
        new CompletionSignal(audio.Play, () => false).Watch(Task.FromResult(1), default).GetAwaiter().GetResult();
        bool failed = false; try { new CompletionSignal(audio.Play, () => true).Watch(Task.FromException<int>(new IOException("fixture")), default).GetAwaiter().GetResult(); } catch (IOException) { failed = true; }
        Check(failed && counts.Plays == 1, "Cancelación, silencio y fallo no generan un aviso pendiente para más tarde.");
        var gate = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously); var late = new CompletionSignal(audio.Play, () => true).Watch(gate.Task, default);
        audio.Dispose(); gate.SetResult(1); late.GetAwaiter().GetResult(); Check(counts.Plays == 1 && !counts.Active, "Un trabajador que termina después de liberar el audio no puede reactivarlo.");

        // Deliberately block the Dispatcher: a real scan must signal completion from the worker.
        var root = Path.Combine(fixture, "audio-completion"); Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "a.txt"), "abc"); File.WriteAllText(Path.Combine(root, "b.txt"), "abc");
        var volume = new VolumeService().ForPath(root); var index = new IndexStore(Path.Combine(fixture, "audio-completion.db")); WaitFor(new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default));
        var model = new MainViewModel(new Settings()) { Section = 4, AdvancedFolder = root, DuplicateMode = 2 }; model.Volumes.Add(volume);
        using var played = new ManualResetEventSlim(); int callbacks = 0, callbackThread = 0; int uiThread = Environment.CurrentManagedThreadId;
        var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "audio-browser.db")), onSearchCompleted: () => { Interlocked.Increment(ref callbacks); callbackThread = Environment.CurrentManagedThreadId; played.Set(); }, advancedCache: new AdvancedResultStore(Path.Combine(fixture, "audio-advanced.db")));
        window.Show(); Pump(window);
        try
        {
            var analysis = window.AnalyzeAdvancedAsync();
            Check(played.Wait(TimeSpan.FromSeconds(3)) && callbackThread != uiThread, "El sonido de un análisis terminado no debe esperar al Dispatcher bloqueado.");
            WaitFor(analysis); Check(callbacks == 1 && model.AdvancedRows.Count == 2, "Al desbloquear la interfaz no debe repetirse el sonido ya enviado.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS audio: caducidad de 3 minutos, parada y liberación idempotentes, sin avisos tras cerrar, aviso único y finalización real con Dispatcher bloqueado.");
    }
    sealed class TestAudio : ICompletionAudioBackend
    {
        public int Plays, Stops, Disposals; public bool Active;
        /// <summary>Updates the fake audio state and counters without playing anything on a physical device.</summary>
        public void Play() { Interlocked.Increment(ref Plays); Active = true; }
        /// <summary>Updates the fake audio state and counters without playing anything on a physical device.</summary>
        public void Stop() { Stops++; Active = false; }
        /// <summary>Releases fixture synchronization or fake feed/audio resources without touching user data.</summary>
        public void Dispose() => Disposals++;
    }
    /// <summary>Verifies duplicate group paging, expansion, keeper decisions, selection safety, themes, and ungrouped Top results.</summary>
    static void CheckDuplicateExpanders(string fixture)
    {
        var root = Path.Combine(fixture, "duplicate-expanders"); Directory.CreateDirectory(root);
        for (int i = 0; i < 54; i++) { var dir = Path.Combine(root, $"{i:D3}"); Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir, "dup.txt"), "same"); }
        for (int i = 0; i < 2; i++) File.WriteAllText(Path.Combine(root, $"{i:D3}", "other.txt"), "different");
        var volume = new VolumeService().ForPath(root); var index = new IndexStore(Path.Combine(fixture, "expanders.db")); WaitFor(new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default));
        var model = new MainViewModel(new Settings { SearchCompletionSound = false }) { Section = 4, AdvancedFolder = root };
        model.Volumes.Add(volume); var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "expanders-browser.db")), advancedCache: new AdvancedResultStore(Path.Combine(fixture, "expanders-results.db"))); window.Show(); Pump(window);
        var grid = (DataGrid)window.FindName("AdvancedResults");
        // Summary: Finds currently realized duplicate-group expanders in the test grid.
        IEnumerable<Expander> Groups() => Descendants(grid).OfType<Expander>().Where(e => e.DataContext is CollectionViewGroup { Name: DuplicateGroupState });
        try
        {
            WaitFor(window.AnalyzeAdvancedAsync()); Pump(window);
            Check(Groups().Count() == 2 && Groups().All(e => !e.IsExpanded) && model.AdvancedRows.Count == 52 && grid.Columns[0].Visibility == Visibility.Collapsed, "Los duplicados deben aparecer en dos bloques plegados, con un máximo de 50 filas por grupo.");
            var big = Groups().Single(e => ((DuplicateGroupState)((CollectionViewGroup)e.DataContext).Name).Info.Count == 54);
            var state = (DuplicateGroupState)((CollectionViewGroup)big.DataContext).Name;
            Check(state.Label.Contains("Mismo nombre") && state.Hint.Contains("no demuestra") && state.PageText.Contains("de 54"), "La cabecera debe mostrar criterio, alcance completo y limitación de metadatos.");
            big.IsExpanded = true; Pump(window);
            Check(Descendants(big).OfType<DataGridRow>().Any(r => r.IsVisible), "Expandir debe mostrar filas reales seleccionables.");
            Check(((SolidColorBrush)Descendants(big).OfType<TextBlock>().First(t => t.Text == "dup.txt").Foreground).Color == ((SolidColorBrush)Application.Current.Resources["Text"]).Color, "El texto de los archivos agrupados debe conservar contraste en oscuro.");
            var next = Descendants(big).OfType<Button>().Single(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Siguientes archivos de este grupo"); next.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => !model.Busy && state.Page == 1 && model.AdvancedRows.Count(r => r.GroupKey == state.Key) == 4); Pump(window);
            var row = model.AdvancedRows.First(r => r.GroupKey == state.Key); grid.SelectedItem = row; Pump(window);
            Descendants(window).OfType<Button>().Single(b => (b.Content as string) == "Esta es la original").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            PumpUntil(() => !model.Busy && model.AdvancedRows.Any(r => r.Path == row.Path && r.Keep && r.Original)); Pump(window);
            Check(state.Info.Kept == 1 && state.Page == 1 && state.IsExpanded, "Conservar original debe actualizar la cabecera sin perder la expansión ni la página del grupo.");
            Groups().Single(e => ((DuplicateGroupState)((CollectionViewGroup)e.DataContext).Name).Key == state.Key).IsExpanded = false; Pump(window);
            Check(!grid.SelectedItems.Cast<AdvancedFinding>().Any(r => r.GroupKey == state.Key), "Plegar debe retirar selecciones ocultas del grupo sin borrar archivos.");
            Descendants(window).OfType<Button>().Single(b => (b.Content as string) == "Contraer grupos").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(window); grid.SelectAll();
            var selected = (AdvancedFinding[])typeof(MainWindow).GetMethod("SelectedAdvancedRows", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(window, null)!;
            Check(selected.Length == 0, "Ctrl+A no puede introducir filas de grupos plegados en acciones sobre la selección."); grid.UnselectAll();
            Descendants(window).OfType<Button>().Single(b => (b.Content as string) == "Expandir grupos").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); Pump(window); Check(Groups().All(e => e.IsExpanded), "Expandir grupos debe actuar sobre todos los bloques visibles.");
            SaveAdvancedPreview(window, "duplicate-groups-dark.png");
            model.Section = 5; Pump(window); var theme = Descendants(window).OfType<CheckBox>().Single(c => (string?)c.Content == "Tema oscuro"); theme.IsChecked = false; theme.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); model.Section = 4; Pump(window); SaveAdvancedPreview(window, "duplicate-groups-light.png");
            model.DuplicateMode = 1; WaitFor(window.AnalyzeAdvancedAsync()); Pump(window); Check(Groups().Count() == 2 && Groups().All(e => ((DuplicateGroupState)((CollectionViewGroup)e.DataContext).Name).Label.Contains("Mismo tamaño")), "Tamaño también debe usar grupos desplegables.");
            model.DuplicateMode = 2; WaitFor(window.AnalyzeAdvancedAsync()); Pump(window); Check(Groups().Count() == 2 && Groups().All(e => ((DuplicateGroupState)((CollectionViewGroup)e.DataContext).Name).Label.Contains("SHA256")), "SHA256 debe agrupar por contenido completo.");
            model.AdvancedMode = 2; WaitFor(window.AnalyzeAdvancedAsync()); Pump(window); Check(CollectionViewSource.GetDefaultView(model.AdvancedRows).GroupDescriptions.Count == 0 && model.AdvancedRows.Count == 56, "Top 100 conserva su tabla sin agrupación de duplicados.");
            Check(File.Exists(row.Path), "Plegar, paginar y conservar nunca deben eliminar archivos.");
            Console.WriteLine("PASS duplicados desplegables: nombre/tamaño/SHA256, 54 copias paginadas dentro del grupo, cabeceras, selección, original, expansión conservada, temas y Top sin agrupar.");
        }
        finally { window.Close(); }
    }
    /// <summary>Verifies selector rebuilds do not start searches, isolated windows are silent, and bounded audio tracing identifies lifecycle events.</summary>
    static void CheckAudioTaskProvenance(string fixture)
    {
        var root = Path.Combine(fixture, "audio-provenance"); Directory.CreateDirectory(root); File.WriteAllText(Path.Combine(root, "fixture.txt"), "fixture");
        var index = new IndexStore(Path.Combine(fixture, "audio-provenance.db")); var volume = new VolumeService().ForPath(root); WaitFor(new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default));
        var model = new MainViewModel(new Settings()) { Section = 1, Folder = root }; model.Volumes.Add(volume); int sounds = 0;
        var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "audio-provenance-browser.db")), onSearchCompleted: () => Interlocked.Increment(ref sounds)); window.Show(); Pump(window);
        try
        {
            Check(window.Title == "CleanUpDesk" && Descendants(window).OfType<TextBlock>().Any(t => t.Text == AppBuild.Title), "La cabecera no debe anunciar una release todavía.");
            var scope = (ComboBox)window.FindName("QuickScope");
            for (int i = 0; i < 4; i++) { scope.SelectedIndex = i % 2 == 0 ? 2 : 0; model.Notify(nameof(MainViewModel.Scopes)); Pump(window); }
            Check(sounds == 0 && !model.SearchInProgress && model.Results.Count == 0, "Reconstruir selectores o cambiar de disco no debe crear tareas automáticas ni avisos.");
            WaitFor(window.SearchCurrentAsync()); Check(sounds == 1, "Una búsqueda explícita debe producir exactamente un aviso.");
            for (int i = 0; i < 4; i++) { model.Volumes.Add(new("fake-" + i, $"{(char)('V' + i)}:\\", "Fixture", 0, 0, false)); model.Notify(""); Pump(window); }
            Check(sounds == 1 && !model.SearchInProgress, "Cuatro actualizaciones del listado no deben convertirse en cuatro búsquedas adicionales.");
        }
        finally { window.Close(); }
        var isolatedModel = new MainViewModel(new Settings()) { Section = 1, Folder = root };
        var isolated = new MainWindow(isolatedModel, index, false, new IndexStore(Path.Combine(fixture, "audio-silent-browser.db")));
        try
        {
            var output = (CompletionSound)typeof(MainWindow).GetField("sound", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(isolated)!;
            Check(output.IsSilent, "Una ventana aislada sin callback de prueba debe usar un backend silencioso antes de ejecutar tareas.");
            isolated.Show(); Pump(isolated); WaitFor(isolated.SearchCurrentAsync());
            Check(CompletionSound.NativePlaybackAttempts == 0, "Incluso con el ajuste de sonido activado una ventana aislada nunca debe tocar el dispositivo real.");
        }
        finally { isolated.Close(); }
        var logFolder = Path.Combine(fixture, "audio-log"); AudioDiagnostics.Start(logFolder);
        var a = new TestAudio(); var b = new TestAudio(); using var first = new CompletionSound(a); using var second = new CompletionSound(b);
        first.Play(Stopwatch.GetTimestamp()); second.Play(Stopwatch.GetTimestamp()); CompletionSound.StopAll();
        first.Play(Stopwatch.GetTimestamp()); second.Play(Stopwatch.GetTimestamp());
        Check(a.Plays == 1 && b.Plays == 1 && a.Stops == 1 && b.Stops == 1 && !a.Active && !b.Active, "El apagado global debe parar todos los reproductores y prohibir reactivarlos.");
        AudioDiagnostics.Record("test-operation id=1"); AudioDiagnostics.Stop(); Check(AudioDiagnostics.Flush(TimeSpan.FromSeconds(3)), "El registro de prueba debe terminar de escribirse.");
        var log = File.ReadAllText(Path.Combine(logFolder, "audio-diagnostics.log"));
        Check(log.Contains("module=") && log.Contains("audio-disabled") && log.Contains("application-exit") && new FileInfo(Path.Combine(logFolder, "audio-diagnostics.log")).Length <= 65536, "El diagnóstico local debe distinguir compilación, solicitudes, parada y salida, sin crecer indefinidamente.");
        Console.WriteLine("PASS procedencia de audio: cabecera sin release, cambios/reconstrucciones de selector sin búsquedas fantasma, una tarea/un aviso, apagado global y diagnóstico local acotado.");
    }
    /// <summary>Verifies complete long Unicode paths, responsive row widths, metadata, and table-mode restoration at multiple window sizes.</summary>
    static void CheckDuplicateFullPathLayout(string fixture)
    {
        var root = Path.Combine(fixture, "duplicate-layout"); Directory.CreateDirectory(root); var paths = new List<string>();
        foreach (var branch in new[] { "uno", "dos" })
        {
            var deep = Path.Combine(root, branch); for (int i = 0; i < 8; i++) deep = Path.Combine(deep, "carpeta-ñ-" + new string('a', 35));
            Directory.CreateDirectory(deep); var path = Path.Combine(deep, "información completa.txt"); File.WriteAllText(path, "fixture"); paths.Add(path);
        }
        var volume = new VolumeService().ForPath(root); var index = new IndexStore(Path.Combine(fixture, "duplicate-layout.db")); WaitFor(new Scanner(index).ScanAsync(root, volume.Id, true, [], null, default));
        var model = new MainViewModel(new Settings { SearchCompletionSound = false }) { Section = 4, AdvancedFolder = root, DuplicateMode = 2 }; model.Volumes.Add(volume);
        var window = new MainWindow(model, index, false, new IndexStore(Path.Combine(fixture, "layout-browser.db")), advancedCache: new AdvancedResultStore(Path.Combine(fixture, "layout-results.db"))); window.Show(); Pump(window);
        var grid = (DataGrid)window.FindName("AdvancedResults");
        try
        {
            WaitFor(window.AnalyzeAdvancedAsync()); Pump(window);
            var group = Descendants(grid).OfType<Expander>().Single(e => e.DataContext is CollectionViewGroup { Name: DuplicateGroupState }); group.IsExpanded = true;
            window.WindowState = WindowState.Normal; window.Height = 900;
            foreach (int width in new[] { 960, 1280, 1600 })
            {
                window.Width = width; Pump(window); grid.ScrollIntoView(model.AdvancedRows[0]); Pump(window);
                var row = (DataGridRow)grid.ItemContainerGenerator.ContainerFromItem(model.AdvancedRows[0]);
                if (row == null) row = Descendants(grid).OfType<DataGridRow>().First(r => ReferenceEquals(r.Item, model.AdvancedRows[0]));
                var path = Descendants(row).OfType<TextBlock>().Single(t => t.Name == "FullDuplicatePath");
                var x = path.TranslatePoint(new Point(0, 0), grid).X;
                Check(path.Text == model.AdvancedRows[0].Path && paths.Contains(path.Text) && path.Text.Length > 450 && path.TextWrapping == TextWrapping.Wrap && path.TextTrimming == TextTrimming.None, "Debe mostrarse toda la ruta Unicode larga, sin elipsis ni depender del tooltip.");
                Check(path.ActualHeight > path.FontSize * 2 && path.ActualWidth > grid.ActualWidth * .7 && x >= 0 && x + path.ActualWidth <= grid.ActualWidth + 2 && double.IsNaN(grid.RowHeight), "La ruta debe aprovechar el ancho disponible y aumentar la altura real de la fila sin comprimirse.");
                Check(grid.Columns.Count(c => c.Visibility == Visibility.Visible) == 1 && ScrollViewer.GetHorizontalScrollBarVisibility(grid) == ScrollBarVisibility.Disabled, "Las columnas comprimidas deben sustituirse por una fila completa, sin desplazamiento horizontal ocultando datos.");
                Check(VirtualizingPanel.GetScrollUnit(grid) == ScrollUnit.Pixel, "El desplazamiento debe permitir recorrer una fila muy larga sin saltarse partes de la ruta.");
                var texts = Descendants(row).OfType<TextBlock>().Select(t => t.Text).ToArray();
                Check(texts.Any(t => t.Contains("Tamaño:")) && texts.Any(t => t.Contains("Modificado:")) && texts.Contains("Por revisar") && texts.Contains("Disponible al consultar"), "Tamaño, fecha, decisión y disponibilidad deben estar en la misma fila legible.");
                if (width == 960) SaveAdvancedPreview(window, "duplicate-full-path-960-dark.png");
            }
            model.Section = 5; Pump(window); var theme = Descendants(window).OfType<CheckBox>().Single(c => (c.Content as string) == "Tema oscuro"); theme.IsChecked = false; theme.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent)); model.Section = 4; window.Width = 960; Pump(window); SaveAdvancedPreview(window, "duplicate-full-path-960-light.png");
            model.AdvancedMode = 2; WaitFor(window.AnalyzeAdvancedAsync()); Pump(window);
            Check(grid.Columns.Count(c => c.Visibility == Visibility.Visible) == grid.Columns.Count && grid.RowHeight == 42 && ScrollViewer.GetHorizontalScrollBarVisibility(grid) == ScrollBarVisibility.Auto, "Top 100 debe recuperar su tabla normal al cambiar de función.");
            Console.WriteLine("PASS diseño de duplicados: rutas Unicode de más de 450 caracteres completas, ajuste real a 960/1280/1600, metadatos visibles y ambos temas; tabla normal restaurada en Top.");
        }
        finally { window.Close(); }
    }
    /// <summary>Writes a fixture-only screenshot artifact when the regression runs from the project root.</summary>
    static void SaveAdvancedPreview(Window window, string name)
    {
        if (!File.Exists(Path.Combine(Environment.CurrentDirectory, "CleanUpDesk.slnx"))) return;
        Pump(window); var folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "screenshots"); Directory.CreateDirectory(folder);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(window);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(folder, name)); encoder.Save(stream);
    }
    /// <summary>Enumerates the visual subtree used by controlled UI assertions.</summary>
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var c = VisualTreeHelper.GetChild(root, i); yield return c; foreach (var child in Descendants(c)) yield return child; }
    }
    sealed class BindingErrors : TraceListener
    {
        public List<string> Messages { get; } = [];
        /// <summary>Collects a data-binding diagnostic emitted during the isolated render tests.</summary>
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
        /// <summary>Collects a complete data-binding diagnostic line for the final regression check.</summary>
        public override void WriteLine(string? message) => Write(message);
    }
    /// <summary>Initializes the controlled fixture dependency used to reproduce this regression without operating on user files.</summary>
    sealed class SlowTree(string slow) : ITreeReader
    {
        /// <summary>Enumerates owned fixture children while applying this test reader's controlled delay, interruption, or cancellation behavior.</summary>
        public IEnumerable<string> Children(string p)
        {
            if (!p.Equals(slow, StringComparison.OrdinalIgnoreCase)) return Directory.EnumerateFileSystemEntries(p);
            return Slow();
            // Summary: Delays fixture enumeration to expose cancellation and independent-lane behavior reproducibly.
            IEnumerable<string> Slow() { for (int i = 0; i < 2000; i++) { Thread.Sleep(2); yield return Path.Combine(p, $"dato-{i}.txt"); } }
        }
        /// <summary>Returns fixture metadata or injects the isolated read failure required by this test reader.</summary>
        public Entry Read(string p, string v) => p.StartsWith(slow + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? new(p, slow, Path.GetFileName(p), ".TXT", false, 1, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks, 0, v) : Paths.ReadEntry(p, v);
    }
    sealed class StopFeed : ISearchChangeFeed
    {
        public event Action<SearchChange>? Changed { add { } remove { } }
        public event Action? Interrupted { add { } remove { } }
        public bool Disposed;
        /// <summary>Releases fixture synchronization or fake feed/audio resources without touching user data.</summary>
        public void Dispose() => Disposed = true;
    }
}
