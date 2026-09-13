using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using DiskStats.App.Localization;

namespace DiskStats.App;

/// <summary>
/// Eine Rueckfrage im Stil der Anwendung.
///
/// Avalonia bringt keinen Meldungsdialog mit, und der des Systems saehe hier fremd aus.
/// Bewusst schlicht: Der Titel sagt, was passiert, der Text nennt Umfang und Folge, und
/// die beiden Knoepfe heissen nach ihrer Wirkung statt "OK" und "Abbrechen".
/// </summary>
public static class Confirm
{
    public static Task<bool> Ask(Window owner, string title, string message, string confirmLabel)
        => Ask(owner, title, message, confirmLabel, null, focusConfirm: false).ContinueWith(t => t.Result.Yes);

    /// <summary>
    /// Wie oben, zusaetzlich mit einem Haken darunter — fuer Fragen, die man nur einmal
    /// gestellt bekommen moechte.
    /// </summary>
    public static async Task<(bool Yes, bool Checked)> Ask(
        Window owner, string title, string message, string confirmLabel,
        string? checkboxLabel, bool focusConfirm, IReadOnlyList<string>? paths = null)
    {
        var heading = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.Bold };
        var body = new TextBlock { Text = message, FontSize = 12.5, TextWrapping = TextWrapping.Wrap };

        var cancel = new Button { Content = Loc.T("Common_Cancel") };
        cancel.Classes.Add("quiet");

        var confirm = new Button { Content = confirmLabel };
        confirm.Classes.Add("primary");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(confirm);

        CheckBox? remember = checkboxLabel is null
            ? null
            : new CheckBox { Content = checkboxLabel, FontSize = 12 };

        var layout = new StackPanel { Margin = new Avalonia.Thickness(22, 20), Spacing = 14 };
        layout.Children.Add(heading);
        layout.Children.Add(body);
        if (paths is not null)
        {
            layout.Children.Add(new ScrollViewer {
                MaxHeight = 280,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new SelectableTextBlock { Text = string.Join(Environment.NewLine, paths), FontSize = 12 }
            });
        }
        if (remember is not null) layout.Children.Add(remember);
        layout.Children.Add(buttons);

        var dialog = new Window
        {
            Title = title,
            Width = paths is null ? 420 : 680,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = layout,
        };

        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);

        // Escape heisst ueberall "nichts tun" — auch hier, egal wo der Fokus gerade liegt.
        cancel.IsCancel = true;
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape) return;
            e.Handled = true;
            dialog.Close(false);
        };

        // Die Vorbelegung liegt normalerweise auf "Abbrechen": Wer nur die Eingabetaste
        // drueckt, soll nichts loeschen. Bei harmlosen Fragen darf der Ja-Knopf vorn stehen.
        dialog.Opened += (_, _) => { if (focusConfirm) confirm.Focus(); else cancel.Focus(); };

        bool yes = await dialog.ShowDialog<bool>(owner);
        return (yes, remember?.IsChecked == true);
    }
}
