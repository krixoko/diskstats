using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using DiskStats.App.Controls;
using DiskStats.App.Rendering;
using DiskStats.App.Rendering.Engines;
using DiskStats.App.Localization;
using DiskStats.Core.Analysis;
using DiskStats.Core.Cleanup;
using DiskStats.Core.Diagnostics;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.App;

/// <summary>Ansichten, Farbmodi, Dateitypen, Legende und Filter.</summary>
public partial class MainWindow
{
    // ---------- Umschalter ----------

    /// <summary>
    /// Baut den Umschalter aus allen Ansichten, inklusive der Dateitabelle. Die liegt nicht
    /// in den Layout-Engines: sie blendet die Zeichenflaeche aus statt sie zu fuellen.
    /// </summary>
    private void BuildViewSwitcher()
    {
        foreach (ViewKind kind in Enum.GetValues<ViewKind>())
        {
            var button = new Button { Tag = kind, Content = BuildViewButtonContent(kind) };

            button.Classes.Add("segment");

            // Der Hinweis haengt am Knopf, nicht in einer Zeile daneben: Dort erklaerte er nur
            // die gerade aktive Ansicht, und der Umschalter laesst ihm ohnehin keinen Platz.
            ToolTip.SetTip(button, $"{TitleOf(kind)} — {HintOf(kind)}");

            // Das Schildchen liest ein Vorlesewerkzeug nicht als Namen, sondern hoechstens als
            // Zusatz. Ohne den Namen hiessen alle neun Knoepfe gleich: "Schaltflaeche".
            AutomationProperties.SetName(button, TitleOf(kind));
            button.Click += (_, _) => SelectView((ViewKind)button.Tag!);
            ViewSwitcher.Children.Add(button);
        }

        SelectView(ViewKind.Treemap);
    }

    /// <summary>
    /// Symbol plus Name, wobei der Name nur bei der aktiven Ansicht sichtbar ist. Acht
    /// beschriftete Knoepfe passen nicht in eine Zeile, acht Symbole schon — und welche
    /// Ansicht gerade laeuft, muss man trotzdem lesen koennen.
    /// </summary>
    private static Control BuildViewButtonContent(ViewKind kind)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };

        row.Children.Add(new IconPresenter
        {
            Data = ViewIcons.For(kind),
            IconSize = 16,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        row.Children.Add(new TextBlock
        {
            Text = TitleOf(kind),
            FontSize = 11.5,
            IsVisible = false,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        });

        return row;
    }

    private static string TitleOf(ViewKind kind)
        => kind == ViewKind.Files
            ? Loc.T("View_Files")
            : ChartView.AllEngines.First(e => e.Kind == kind).Title;

    private static string HintOf(ViewKind kind)
        => kind == ViewKind.Files
            ? Loc.T("View_FilesHint")
            : ChartView.AllEngines.First(e => e.Kind == kind).Hint;

    private void SelectView(ViewKind kind)
    {
        Chart.SetView(kind);
        FilesTable.IsVisible = kind == ViewKind.Files;
        Chart.IsVisible = kind != ViewKind.Files;
        FilesTable.SetStore(kind == ViewKind.Files ? _store : null, _currentRoot, _fileQuery);
        MarkActive(ViewSwitcher, kind);
        UpdateViewButtons();
    }

    private void BuildColorSwitcher()
    {
        (ColorMode Mode, string Title)[] modes =
        [
            (ColorMode.ByFolder, Loc.T("Color_ByFolder")),
            (ColorMode.ByType, Loc.T("Color_ByType")),
            (ColorMode.ByAge, Loc.T("Color_ByAge")),
        ];

        foreach ((ColorMode mode, string title) in modes)
        {
            var button = new Button { Content = title, Tag = mode };
            button.Classes.Add("segment");
            button.Click += (_, _) => SelectColorMode((ColorMode)button.Tag!);
            ColorSwitcher.Children.Add(button);
        }

        SelectColorMode(ColorMode.ByFolder);
    }

    private void SelectColorMode(ColorMode mode)
    {
        Chart.SetColorMode(mode);
        MarkActive(ColorSwitcher, mode);
        BuildLegend();
    }

    /// <summary>Faerbt die Symbole nach und blendet den Namen der aktiven Ansicht ein.</summary>
    private void UpdateViewButtons()
    {
        // Direkt aus den Themenwerten statt ueber FindResource: Beim Aufbau des Fensters sind
        // die Themenressourcen noch nicht aufgeloest und lieferten UnsetValue.
        IBrush active = new SolidColorBrush(Dark
            ? Color.FromRgb(0x1A, 0x18, 0x15) : Color.FromRgb(0xFB, 0xF9, 0xF3));
        IBrush idle = new SolidColorBrush(Dark
            ? Color.FromRgb(0x9B, 0x93, 0x87) : Color.FromRgb(0x7A, 0x73, 0x6A));

        foreach (Control child in ViewSwitcher.Children)
        {
            if (child is not Button { Content: StackPanel row } button) continue;
            bool isActive = button.Classes.Contains("active");

            foreach (Control element in row.Children)
            {
                switch (element)
                {
                    case IconPresenter icon:
                        icon.Brush = isActive ? active : idle;
                        break;
                    case TextBlock label:
                        label.IsVisible = isActive;
                        // Muss ausdruecklich gesetzt werden: der globale TextBlock-Stil
                        // gewinnt sonst gegen die Vordergrundfarbe des Knopfes.
                        label.Foreground = active;
                        break;
                }
            }
        }
    }

    private static void MarkActive(StackPanel group, object value)
    {
        foreach (Control child in group.Children)
        {
            if (child is not Button button) continue;
            if (Equals(button.Tag, value)) button.Classes.Add("active");
            else button.Classes.Remove("active");
        }
    }

    private void OnThemeClicked(object? sender, RoutedEventArgs e)
        => AppSettings.Apply(s => s.Theme = Dark ? ThemeChoice.Light : ThemeChoice.Dark);

    /// <summary>Der Knopf zeigt, wohin er fuehrt, nicht wo man gerade ist.</summary>
    private void UpdateThemeGlyph()
    {
        ThemeGlyph.Data = Dark ? Icons.Sun : Icons.Moon;
        ToolTip.SetTip(ThemeButton, Dark ? Loc.T("Side_ToLight") : Loc.T("Side_ToDark"));
    }

    // ---------- Dateitypen ----------

    /// <summary>Kategorien oder Endungen — beides beantwortet eine andere Frage.</summary>
    private void BuildTypeModeSwitch()
    {
        (bool ByExtension, string Label)[] modes =
            [(true, Loc.T("Types_ByExtension")), (false, Loc.T("Types_ByKind"))];

        foreach ((bool byExtension, string label) in modes)
        {
            var button = new Button { Content = label, Tag = byExtension, FontSize = 10.5 };
            button.Classes.Add("segment");
            button.Padding = new Avalonia.Thickness(7, 2);

            button.Click += (_, _) =>
            {
                _byExtension = (bool)button.Tag!;
                ClearHighlight();
                MarkActive(TypeModeSwitch, _byExtension);
                ShowFileTypes();
            };

            TypeModeSwitch.Children.Add(button);
        }

        MarkActive(TypeModeSwitch, _byExtension);
    }

    private int _fileTypesGeneration;

    private void ShowFileTypes() => Guard(ShowFileTypesAsync(), "Dateitypen");

    /// <summary>Was die Dateityp-Leiste zeigt: je Zeile Schluessel, Beschriftung, Bytes, Farbe.</summary>
    private readonly record struct TypeTotal(string Key, string Label, long Bytes, Color Colour);

    private async Task ShowFileTypesAsync()
    {
        if (_store is null) { FileTypesBlock.IsVisible = false; return; }
        NodeStore store = _store;
        bool byExtension = _byExtension;
        bool dark = Dark;
        int generation = ++_fileTypesGeneration;

        // Der Baumlauf laeuft im Hintergrund; nur das Bauen der Steuerelemente bleibt hier.
        (IReadOnlyList<TypeTotal> totals, long grand) = await Task.Run(() => FileTypeTotals(store, byExtension, dark));

        // Waehrend des Zaehlens kann ein neuer Baum, ein Themenwechsel oder ein Modusklick
        // gekommen sein — dann gehoert dieses Ergebnis in den Papierkorb.
        if (generation != _fileTypesGeneration || !ReferenceEquals(store, _store)) return;

        if (grand <= 0) { FileTypesBlock.IsVisible = false; return; }

        var rows = new List<TypeEntry>(totals.Count);
        var bands = new List<(long Bytes, Color Colour)>(totals.Count);
        foreach (TypeTotal total in totals)
        {
            bands.Add((total.Bytes, total.Colour));
            rows.Add(new TypeEntry(total.Key, total.Label, Sizes.Format(total.Bytes),
                new SolidColorBrush(total.Colour), Math.Round(total.Bytes / (double)grand * TypeBarWidth)));
        }

        FileTypeSegments.Children.Clear();
        foreach ((long bytes, Color colour) in bands)
        {
            FileTypeSegments.Children.Add(new Border
            {
                Width = Math.Max(1, Math.Round(bytes / (double)grand * TypeBarWidth)),
                Background = new SolidColorBrush(colour),
            });
        }

        FileTypeList.ItemsSource = rows;
        FileTypesBlock.IsVisible = true;
    }

    private static (IReadOnlyList<TypeTotal> Rows, long Grand) FileTypeTotals(NodeStore store, bool byExtension, bool dark)
    {
        var result = new List<TypeTotal>();

        if (byExtension)
        {
            // Die Endung ist die Ebene, auf der man entscheidet: Acht Kategorien sagen
            // "Video frisst 60 GB", aber nicht, ob es .mkv oder .iso ist.
            IReadOnlyList<ExtensionStats.Entry> stats = ExtensionStats.Of(store, 0);

            // Innerhalb derselben Kategorie abgestuft, damit .exe und .dll unterscheidbar bleiben.
            var seen = new Dictionary<FileCategory, int>();
            foreach (ExtensionStats.Entry entry in stats.Take(12))
            {
                FileCategory category = FileCategories.Of(
                    entry.Extension == ExtensionStats.NoExtension ? "x" : "x" + entry.Extension);
                seen.TryGetValue(category, out int variant);
                seen[category] = variant + 1;
                result.Add(new TypeTotal(entry.Extension, entry.Extension, entry.Bytes,
                    Palette.ForCategory(category, variant, dark)));
            }
            // Anteile beziehen sich auf alles, nicht nur auf die zwoelf gelisteten Endungen.
            return (result, stats.Sum(e => e.Bytes));
        }

        var totals = new Dictionary<FileCategory, long>();
        LayoutHelp.WalkFiles(store, 0, node =>
        {
            FileCategory category = FileCategories.Of(store.GetName(node));
            totals.TryGetValue(category, out long sum);
            totals[category] = sum + store.Size[node];
        });

        foreach ((FileCategory category, long bytes) in totals.Where(e => e.Value > 0).OrderByDescending(e => e.Value))
            result.Add(new TypeTotal(string.Empty, FileCategories.DisplayName(category), bytes,
                Palette.ForCategory(category, dark)));
        return (result, totals.Values.Sum());
    }

    /// <summary>
    /// Ein Klick auf eine Endung hebt alle zugehoerigen Dateien in der Karte hervor. Genau
    /// diese Verbindung zwischen Liste und Karte macht WinDirStats drei Bereiche aus — eine
    /// Liste ohne Bezug zur Karte waere nur eine weitere Tabelle.
    /// </summary>
    private void OnTypeClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key } || key.Length == 0) return;

        if (_highlight == key) { ClearHighlight(); return; }

        _highlight = key;
        Chart.SetHighlight(key);
        StatusText.Text = Loc.T("Types_Highlighted", key);
    }

    private void ClearHighlight()
    {
        if (_highlight is null) return;

        _highlight = null;
        Chart.SetHighlight(null);
    }

    private void BuildLegend()
    {
        // Jede Faerbung braucht ihre eigene Erklaerung. Bei "Alter" bedeuten die Farben etwas
        // voellig anderes als bei "Typ" — eine Typlegende waere dort schlicht falsch.
        AgeLegend.IsVisible = Chart.ColorMode == ColorMode.ByAge;
        if (AgeLegend.IsVisible) BuildAgeRamp();

        if (Chart.ColorMode != ColorMode.ByType)
        {
            Legend.ItemsSource = null;
            return;
        }

        Legend.ItemsSource = FileCategories.All
            .Select(c => new LegendEntry(
                FileCategories.DisplayName(c),
                new SolidColorBrush(Palette.ForCategory(c, Dark))))
            .ToList();
    }

    private void SetViewStore(NodeStore? store, int root)
    {
        Chart.SetStore(store, root);
        FilesTable.SetStore(Chart.CurrentView == ViewKind.Files ? store : null, root, _fileQuery);
    }

    private static string StorageText(NodeStore store, int node)
    {
        if (store.PhysicalSize.Length != store.Count) return Loc.T("Storage_Unknown");
        long bytes = store.IsDirectory(node) ? store.PhysicalSize[node] : store.AllocationOf(node);
        if (bytes < 0) return Loc.T("Storage_Unknown");
        string text = Loc.T("Storage_Allocated", Sizes.Format(bytes));
        if (store.UnknownAllocation[node] > 0) text += " " + Loc.T("Storage_Missing", store.UnknownAllocation[node]);
        return text;
    }

    private async Task EditFilters()
    {
        if (_scanRunning || _cleanupRunning) return;
        FilterSelection? selected = await new FilterWindow(_fileQuery, AppSettings.Current.ExcludedPaths)
            .ShowDialog<FilterSelection?>(this);
        if (selected is null) return;
        bool exclusionsChanged = !AppSettings.Current.ExcludedPaths.SequenceEqual(selected.ExcludedPaths);
        _fileQuery = selected.Query;
        AppSettings.Apply(s => s.ExcludedPaths = selected.ExcludedPaths);
        CloseSearch();
        SelectView(ViewKind.Files);
        FilesTable.SetRecursive(true);
        FilesTable.SetStore(_store, _currentRoot, _fileQuery);
        if (exclusionsChanged && _scanRoot.Length > 0) await RunScanAsync(_scanRoot);
    }
}
