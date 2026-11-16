using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CleanUpDesk.Core;

namespace CleanUpDesk.App;
public sealed class ReviewWindow : Window
{
    readonly ListBox list;
    readonly ComboBox mode;
    readonly bool requirePermanent;
    public string[] Retained => list.Items.Cast<ReviewedItem>().Select(i => i.Path).ToArray();
    public DeleteMode Mode => requirePermanent || mode.SelectedIndex == 1 ? DeleteMode.Permanent : DeleteMode.Recycle;
    /// <summary>Presents the first-step target list and deletion mode without preselecting a required permanent mode.</summary>
    public ReviewWindow(Window owner, DeleteReview review, bool requirePermanent = false)
    {
        this.requirePermanent = requirePermanent;
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner; Title = "1 de 2 · Revisar selección"; Width = 850; Height = 590; MinWidth = 650; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(24) }; Content = panel;
        var head = new StackPanel(); DockPanel.SetDock(head, Dock.Top); panel.Children.Add(head);
        head.Children.Add(new TextBlock { Text = "Revisa cada destino", FontSize = 25, FontWeight = FontWeights.SemiBold });
        head.Children.Add(new TextBlock { Text = $"{review.Items.Count:N0} elementos principales · {Format.Bytes(review.Items.Sum(i => i.Size))} lógicos estimados.\nLas carpetas incluyen sus descendientes. Puedes retirar elementos de la lista.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 14) });
        mode = new ComboBox { ItemsSource = requirePermanent ? new[] { "Eliminar permanentemente solo si las carpetas siguen vacías" } : new[] { "Enviar a la Papelera (si es compatible)", "Eliminar permanentemente" }, SelectedIndex = requirePermanent ? -1 : review.Mode == DeleteMode.Recycle ? 0 : 1 }; head.Children.Add(mode);
        if (requirePermanent) head.Children.Add(new TextBlock { Text = "Esta selección incluye carpetas vacías. Para garantizar que no se borra contenido nuevo se requiere eliminación permanente sin recursión. Elige ese modo explícitamente o cancela; después se exigirá la frase de confirmación. Puedes revisar archivos y carpetas por separado.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) });
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        footer.Children.Add(new TextBlock { Text = "No se ha eliminado nada. El siguiente paso solicita una confirmación adicional.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 6) });
        var buttons = new WrapPanel(); footer.Children.Add(buttons);
        var cancel = new Button { Content = "Cancelar", IsCancel = true }; buttons.Children.Add(cancel);
        var remove = new Button { Content = "Retirar seleccionados" }; buttons.Children.Add(remove);
        var next = new Button { Content = "Continuar a confirmación" }; buttons.Children.Add(next);
        if (requirePermanent) { next.IsEnabled = false; mode.SelectionChanged += (_, _) => next.IsEnabled = mode.SelectedIndex >= 0; }
        list = new ListBox { SelectionMode = SelectionMode.Extended, DisplayMemberPath = "Path" }; foreach (var i in review.Items) list.Items.Add(i); panel.Children.Add(list);
        remove.Click += (_, _) => { foreach (var i in list.SelectedItems.Cast<object>().ToArray()) list.Items.Remove(i); };
        next.Click += (_, _) => { if (list.Items.Count > 0 && mode.SelectedIndex >= 0) DialogResult = true; };
        Loaded += (_, _) => cancel.Focus();
    }
}
public sealed class FinalWindow : Window
{
    readonly TextBox phrase = new();
    public string Phrase => phrase.Text;
    /// <summary>Presents the independent final confirmation and requires the permanent-deletion phrase when applicable.</summary>
    public FinalWindow(Window owner, string summary, bool permanent)
    {
        SetResourceReference(StyleProperty, typeof(Window));
        Owner = owner; Title = "2 de 2 · Confirmación final"; Width = 610; Height = 390; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(26) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = permanent ? "Eliminación definitiva" : "Enviar a la Papelera", FontSize = 25, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = summary + "\n\nPuedes cancelar entre elementos. Lo ya completado no se revierte al cancelar.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 12) });
        if (permanent) { panel.Children.Add(new TextBlock { Text = "Escribe ELIMINAR DEFINITIVAMENTE:" }); panel.Children.Add(phrase); }
        var row = new WrapPanel(); panel.Children.Add(row); var cancel = new Button { Content = "Cancelar", IsCancel = true }; row.Children.Add(cancel);
        var confirm = new Button { Content = permanent ? "Eliminar definitivamente" : "Confirmar envío a Papelera", IsDefault = false }; row.Children.Add(confirm);
        if (permanent) { confirm.IsEnabled = false; phrase.TextChanged += (_, _) => confirm.IsEnabled = Phrase == "ELIMINAR DEFINITIVAMENTE"; }
        confirm.Click += (_, _) => DialogResult = true; Loaded += (_, _) => cancel.Focus();
    }
}
