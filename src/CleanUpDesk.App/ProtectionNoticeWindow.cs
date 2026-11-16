using System.Windows;
using System.Windows.Controls;
using CleanUpDesk.Core;

namespace CleanUpDesk.App;
public sealed class ProtectionNoticeWindow : Window
{
    /// <summary>Shows protected paths and copy/close actions without offering an elevation or deletion override.</summary>
    public ProtectionNoticeWindow(Window owner, IReadOnlyList<ProtectedTarget> targets)
    {
        Owner = owner; Title = "Eliminación bloqueada · elementos protegidos"; Width = 740; Height = 490;
        MinWidth = 580; MinHeight = 400; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(StyleProperty, typeof(Window));
        var panel = new DockPanel { Margin = new Thickness(25) }; Content = panel;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        var title = new TextBlock { Text = "⚠  Estos elementos no se eliminarán", FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); header.Children.Add(title);
        header.Children.Add(new TextBlock { Text = "Pueden necesitar permisos adicionales o formar parte de Windows o de una aplicación. Borrarlos puede impedir que el sistema o las aplicaciones funcionen. CleanUpDesk no eleva permisos ni fuerza este borrado.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 13, 0, 12) });
        header.Children.Add(new TextBlock { Text = "CleanUpDesk no se hace responsable de las eliminaciones que realices fuera de la aplicación. Puedes copiar las rutas para revisarlas manualmente. No se recomienda eliminar componentes del sistema.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var copy = new Button { Content = "Copiar rutas" }; buttons.Children.Add(copy);
        var close = new Button { Content = "Cerrar", IsCancel = true }; buttons.Children.Add(close);
        var rows = new ListBox { ItemsSource = targets.Select(t => t.Path + Environment.NewLine + t.Reason).ToArray() }; panel.Children.Add(rows);
        copy.Click += (_, _) => { try { Clipboard.SetText(string.Join(Environment.NewLine, targets.Select(t => t.Path).Distinct(StringComparer.OrdinalIgnoreCase))); copy.Content = "Rutas copiadas"; } catch { copy.Content = "No se pudo copiar"; } };
        close.Click += (_, _) => Close(); Loaded += (_, _) => close.Focus();
    }
}
