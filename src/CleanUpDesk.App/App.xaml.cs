using System.Windows;
namespace CleanUpDesk.App;
public partial class App : Application
{
    readonly bool startProduction;
    /// <summary>Creates the application in production or isolated-test startup mode.</summary>
    public App() : this(true) { }
    /// <summary>Creates the application in production or isolated-test startup mode.</summary>
    public App(bool startProduction) => this.startProduction = startProduction;
    Mutex? instance;
    /// <summary>Initializes production error handling, diagnostics, the single-instance guard, and the main window.</summary>
    protected override void OnStartup(StartupEventArgs e)
    {
        if (!startProduction) return;
        base.OnStartup(e);
        AudioDiagnostics.Start(Core.Settings.DataFolder);
        DispatcherUnhandledException += (_, args) =>
        {
            CompletionSound.StopAll();
            ReportFailure(args.Exception); args.Handled = true; Shutdown(1);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception error) WriteFailure(error);
        };
        instance = new Mutex(true, "Local\\CleanUpDesk-Desktop", out bool first);
        if (!first) { MessageBox.Show("CleanUpDesk ya está abierta en esta sesión de Windows.", "CleanUpDesk"); Shutdown(); return; }
        try { MainWindow = new MainWindow(); MainWindow.Show(); }
        catch (Exception ex) { ReportFailure(ex); Shutdown(1); }
    }
    /// <summary>Writes a bounded local startup/error diagnostic without recording file contents.</summary>
    static string? WriteFailure(Exception error)
    {
        try
        {
            var folder = Core.Settings.DataFolder; Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "startup-errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_048_576) File.WriteAllText(path, "", System.Text.Encoding.UTF8);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}\n{error}\n\n", System.Text.Encoding.UTF8);
            return path;
        }
        catch { return null; }
    }
    /// <summary>Shows a user-facing fatal error and the diagnostic location when one was written.</summary>
    static void ReportFailure(Exception error)
    {
        var log = WriteFailure(error);
        MessageBox.Show("CleanUpDesk no puede continuar y se cerrará.\n\n" + error.GetBaseException().Message +
            (log == null ? "" : "\n\nDetalle guardado en:\n" + log), "Error de CleanUpDesk", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    /// <summary>Stops every audio player, closes diagnostics, and releases the per-session application guard.</summary>
    protected override void OnExit(ExitEventArgs e) { CompletionSound.StopAll(); AudioDiagnostics.Stop(); instance?.Dispose(); base.OnExit(e); }
}
