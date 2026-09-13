using Avalonia.Controls;
using DiskStats.Core.Scanning;
using Avalonia.Controls.Primitives;

using DiskStats.App.Localization;

namespace DiskStats.App;

/// <summary>
/// Die Einstellungen. Jede Aenderung wirkt sofort und wird sofort gespeichert — ein
/// "Uebernehmen" zu verlangen waere eine Huerde ohne Gegenwert, weil sich hier nichts
/// gegenseitig widerspricht.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        // Die Namen der Sprachen stehen in ihrer eigenen Sprache: Wer die Oberflaeche
        // nicht lesen kann, findet "Deutsch" trotzdem.
        BuildSegments(LanguageGroup,
            [(Language.English, "English"), (Language.German, "Deutsch")],
            AppSettings.Current.Language,
            value => AppSettings.Apply(s => s.Language = (Language)value));

        BuildSegments(ThemeGroup,
            [(ThemeChoice.System, Loc.T("Set_ThemeSystem")),
             (ThemeChoice.Light, Loc.T("Set_ThemeLight")),
             (ThemeChoice.Dark, Loc.T("Set_ThemeDark"))],
            AppSettings.Current.Theme,
            value => AppSettings.Apply(s => s.Theme = (ThemeChoice)value));

        BuildSegments(DetailGroup,
            [(DetailLevel.Coarse, Loc.T("Set_DetailCoarse")),
             (DetailLevel.Normal, Loc.T("Set_DetailNormal")),
             (DetailLevel.Fine, Loc.T("Set_DetailFine"))],
            AppSettings.Current.Detail,
            value => AppSettings.Apply(s => s.Detail = (DetailLevel)value));

        BuildSegments(SizeGroup,
            [(SizeBase.Binary, Loc.T("Set_SizesBinary")),
             (SizeBase.Decimal, Loc.T("Set_SizesDecimal"))],
            AppSettings.Current.Sizes,
            value => { AppSettings.Apply(s => s.Sizes = (SizeBase)value); UpdateSizeHint(); });

        BuildSegments(ScanMethodGroup,
            [(ScanMethod.Auto, Loc.T("Scan_Auto")), (ScanMethod.Directory, Loc.T("Scan_Directory")), (ScanMethod.Ntfs, Loc.T("Scan_Ntfs"))],
            AppSettings.Current.ScanMethod, value => AppSettings.Apply(s => s.ScanMethod = (ScanMethod)value));
        UpdateCacheHint();
        CacheClear.Click += (_, _) =>
        {
            long freed = AppData.ClearCache();
            CacheHint.Text = Loc.T("Cache_Cleared", Sizes.Format(freed));
            CacheClear.IsEnabled = false;
        };

        // Nur zeigen, wo die Frage ueberhaupt gestellt werden kann — ein Schalter fuer etwas,
        // das im eigenen Konto nie vorkommt, ist eine Einstellung ohne Wirkung.
        ElevationCard.IsVisible = Elevation.CanOffer;
        ElevationSwitch.IsChecked = AppSettings.Current.ElevationPrompt;
        ElevationSwitch.IsCheckedChanged += (_, _) => AppSettings.Apply(s =>
        {
            s.ElevationPrompt = ElevationSwitch.IsChecked == true;
            if (s.ElevationPrompt) s.AutoElevate = false;
        });

        AnimationSwitch.IsChecked = AppSettings.Current.Animations;
        AnimationSwitch.IsCheckedChanged += (_, _) =>
            AppSettings.Apply(s => s.Animations = AnimationSwitch.IsChecked == true);

        int workers = AppSettings.Current.WorkerCount;
        AutoWorkers.IsChecked = workers <= 0;
        WorkerSlider.Maximum = Math.Max(8, Environment.ProcessorCount * 2);
        WorkerSlider.Value = workers > 0 ? workers : Environment.ProcessorCount;
        WorkerRow.IsVisible = workers > 0;
        UpdateWorkerValue();

        AutoWorkers.IsCheckedChanged += (_, _) =>
        {
            bool automatic = AutoWorkers.IsChecked == true;
            WorkerRow.IsVisible = !automatic;
            AppSettings.Apply(s => s.WorkerCount = automatic ? 0 : (int)WorkerSlider.Value);
        };

        // Der Regler feuert bei jeder Stufe; gespeichert wird erst, wenn er kurz ruht — sonst
        // schreibt ein Zug ueber zehn Stufen zehnmal die Einstellungsdatei.
        var saveWorkers = new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        void FlushWorkers()
        {
            if (!saveWorkers.IsEnabled) return;
            saveWorkers.Stop();
            if (AutoWorkers.IsChecked != true)
                AppSettings.Apply(s => s.WorkerCount = (int)WorkerSlider.Value);
        }
        saveWorkers.Tick += (_, _) => FlushWorkers();
        WorkerSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property != RangeBase.ValueProperty) return;
            UpdateWorkerValue();
            saveWorkers.Stop();
            saveWorkers.Start();
        };
        Closed += (_, _) => FlushWorkers();

        CloseButton.Click += (_, _) => Close();
        UpdateSizeHint();
    }

    private void UpdateWorkerValue() => WorkerValue.Text = ((int)WorkerSlider.Value).ToString();

    /// <summary>
    /// Nennt, was die Anwendung selbst belegt.
    ///
    /// Ein Werkzeug, das Platzfresser anprangert, muss ueber den eigenen Verbrauch Auskunft
    /// geben — und ihn auf Wunsch hergeben.
    /// </summary>
    private void UpdateCacheHint()
    {
        long bytes = AppData.CachedBytes();

        CacheHint.Text = bytes > 0
            ? Loc.T("Cache_Hint", Sizes.Format(bytes))
            : Loc.T("Cache_Empty");

        CacheClear.IsEnabled = bytes > 0;
    }

    private void UpdateSizeHint() => SizeHint.Text = AppSettings.Current.Sizes == SizeBase.Binary
        ? Loc.T("Set_SizesBinaryHint")
        : Loc.T("Set_SizesDecimalHint");

    /// <summary>
    /// Baut eine Reihe von Segmentknoepfen aus Wert und Beschriftung. Die aktive Wahl traegt
    /// eine Flaeche — dieselbe Sprache wie der Ansichts-Umschalter im Hauptfenster.
    /// </summary>
    private static void BuildSegments<T>(
        StackPanel group, (T Value, string Label)[] options, T active, Action<object> onPick)
        where T : notnull
    {
        foreach ((T value, string label) in options)
        {
            var button = new Button { Content = label, Tag = value };
            button.Classes.Add("segment");

            button.Click += (_, _) =>
            {
                foreach (Control child in group.Children)
                    if (child is Button other) other.Classes.Remove("active");

                button.Classes.Add("active");
                onPick(value);
            };

            if (value.Equals(active)) button.Classes.Add("active");
            group.Children.Add(button);
        }
    }
}
