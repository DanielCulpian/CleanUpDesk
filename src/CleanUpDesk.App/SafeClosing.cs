using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CleanUpDesk.App;

public sealed class CloseProcessesWindow : Window
{
    /// <summary>Builds the close-with-work warning with Cancel as the focused default action.</summary>
    public CloseProcessesWindow(Window owner)
    {
        Owner = owner; Title = "Hay un proceso en curso"; Width = 470; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Hay procesos en curso", FontSize = 21, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "¿Quieres cerrar CleanUpDesk? Se solicitará la cancelación de todos los procesos y se esperará a que terminen de forma segura antes de salir.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 10) });
        panel.Children.Add(new TextBlock { Text = "Un borrado ya completado no se revierte. Si Windows está realizando una operación que no admite interrupción, se esperará a que termine.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(buttons);
        var cancel = new Button { Content = "Cancelar", IsCancel = true, IsDefault = true };
        var close = new Button { Content = "Cerrar igualmente", Margin = new Thickness(12, 0, 0, 0) };
        buttons.Children.Add(cancel); buttons.Children.Add(close);
        cancel.Click += (_, _) => DialogResult = false; close.Click += (_, _) => DialogResult = true;
        Loaded += (_, _) => cancel.Focus();
    }
}

public partial class MainWindow
{
    readonly HashSet<TaskCompletionSource<bool>> runningProcesses = [];
    readonly Func<bool> confirmCloseWithProcesses;
    bool closeRequested, closeApproved, resourcesClosed;
    /// <summary>Registers a completion lease so closing can wait for every active operation lane.</summary>
    IDisposable TrackProcess()
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); runningProcesses.Add(completed);
        return new ProcessLease(() => { runningProcesses.Remove(completed); completed.TrySetResult(true); });
    }
    /// <summary>Creates an idempotent callback lease used to await tracked work during closing.</summary>
    sealed class ProcessLease(Action finish) : IDisposable
    {
        Action? end = finish;
        /// <summary>Completes the tracked-operation lease exactly once.</summary>
        public void Dispose() => Interlocked.Exchange(ref end, null)?.Invoke();
    }
    /// <summary>Keeps work running when closing is cancelled, or cancels and awaits all tracked work before releasing resources.</summary>
    async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closeRequested && !closeApproved) { e.Cancel = true; return; }
        if (!closeApproved && runningProcesses.Count > 0)
        {
            e.Cancel = true;
            if (!confirmCloseWithProcesses()) return;
            closeRequested = true; IsEnabled = false;
            AudioDiagnostics.Record("window-close-confirmed");
            sound.Dispose(); // Cortar audio antes de esperar a los trabajos de disco/COM.
            vm.Status = "Cerrando: cancelando los procesos de forma segura. Esperando al elemento en curso; lo ya completado se conserva…";
            var pending = runningProcesses.Select(p => p.Task).ToArray();
            lifetime.Cancel(); operation?.Cancel(); searchOperation?.Cancel(); advancedOperation?.Cancel();
            await Task.WhenAll(pending);
            await Dispatcher.Yield(DispatcherPriority.Background);
            closeApproved = true; Close(); return;
        }
        if (resourcesClosed) return;
        resourcesClosed = true; lifetime.Cancel(); searchSession.Dispose(); sound.Dispose();
        if (persistSettings) { try { SaveConfiguration(); } catch { /* Keep the previous configuration. */ } }
    }
}
