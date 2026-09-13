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

/// <summary>Scan starten, begleiten und abschliessen; erhoehte Rechte.</summary>
public partial class MainWindow
{
    // ---------- Scan ----------

    /// <summary>Oeffnet die Auswahl im Stil der Anwendung.</summary>
    private void OnBrowseClicked(object? sender, RoutedEventArgs e) => Guard(BrowseAsync(), "Ordnerauswahl");

    private async Task BrowseAsync()
    {
        string? chosen = await new FolderPickerWindow().ShowDialog<string?>(this);
        if (chosen is { Length: > 0 }) StartScan(chosen);
    }

    private void ScanFromCommandLine()
    {
        string[] args = _args;

        // Optional: --view <name> waehlt die Startansicht, --dark das dunkle Thema.
        int viewArg = Array.FindIndex(args, a => a.Equals("--view", StringComparison.OrdinalIgnoreCase));
        if (viewArg >= 0 && viewArg + 1 < args.Length)
        {
            string name = args[viewArg + 1];
            // "tree" war der alte Name der Dateitabelle, bevor die tote Layout-Engine wegfiel.
            if (name.Equals("tree", StringComparison.OrdinalIgnoreCase)) SelectView(ViewKind.Files);
            else if (Enum.TryParse(name, ignoreCase: true, out ViewKind kind)) SelectView(kind);
        }

        if (Array.Exists(args, a => a.Equals("--dark", StringComparison.OrdinalIgnoreCase)))
            Avalonia.Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;

        string path = args.Length > 1 ? args[1] : string.Empty;

        Loaded += (_, _) => Guard(StartUp(path), "Start");
    }

    private async Task StartUp(string path)
    {
        // Vor dem ersten Scan fragen: Wer zustimmt, startet neu, und ein Scan, der dabei
        // verloren geht, waere umsonst gelaufen.
        if (await OfferElevation()) return;

        if (path.Length > 0 && Directory.Exists(path)) await RunScanAsync(path);
        else RestoreLastScan();
    }

    /// <summary>
    /// Fuer alles, was aus einem Ereignis heraus laeuft und niemandem etwas zurueckgeben kann.
    /// Fehler landen im Protokoll und in der Statuszeile statt den Prozess zu beenden.
    /// </summary>
    private void Guard(Task work, string context)
        => _ = TaskGuard.Fire(work, context, message =>
        {
            if (!_closed) StatusText.Text = Loc.T("Stat_Failed", message);
        });

    /// <summary>
    /// Holt den zuletzt betrachteten Scan aus der Ablage, statt ihn neu zu durchlaufen.
    /// Genau dafuer gibt es das Snapshot-Format: kalt kostet ein grosses Laufwerk Minuten,
    /// aus der Ablage unter einer Sekunde. Was dort steht, ist ein Stand — kein Live-Bild;
    /// deshalb sagt die Statusleiste, von wann er ist.
    /// </summary>
    private void RestoreLastScan()
    {
        string last = AppSettings.Current.LastPath;
        if (last.Length == 0) return;

        NodeStore? store = SnapshotStore.TryLoad(last, out DateTime written);
        if (store is null) return;

        _store = store;
        _scanRoot = last;
        _currentRoot = 0;
        _history.Clear();
        _selected = -1;
        UpButton.IsVisible = false;
        EmptyHint.IsVisible = false;

        Badge.Kind = VolumeProbe.KindOf(last);
        ToolTip.SetTip(Badge, Badge.Description);
        AutomationProperties.SetName(Badge, Loc.T("A11y_Drive", Badge.Description));
        (_driveTotal, _driveLabel) = DriveOf(last);

        SetViewStore(_store, 0);
        UpdateHeader();
        ShowFileTypes();
        ShowQuickWins();
        OfferDuplicates();
        ShowNode(0);

        StatusText.Text = Loc.T("Stat_Cached",
            written.ToString(Loc.Current == Language.German ? "dd.MM.yyyy HH:mm" : "yyyy-MM-dd HH:mm"));
    }

    private void StartScan(string path) => Guard(RunScanAsync(path), $"Scan von {path}");

    internal async Task RunScanAsync(string path,
        Func<string, int, Action<ScanSession.Update>, CancellationToken, Task>? run = null)
    {
        if (_cleanupRunning || _benchmarkOpen) return;
        if (!Directory.Exists(path))
        {
            StatusText.Text = Loc.T("Stat_NotFound", path);
            return;
        }

        // Ein laufender Refresh wird abgebrochen und zu Ende gewartet: Sein finally setzt
        // Knoepfe und Badge zurueck — taete man das hier von aussen, bliebe sein spaeteres
        // Aufraeumen wirkungslos und die Oberflaeche in halbem Zustand.
        _refresh?.Cancel();
        await _refreshTask;
        _scan?.Cancel();
        using var session = new CancellationTokenSource();
        _scan = session;

        _scanRoot = path;
        StartWatching();
        Badge.Kind = VolumeProbe.KindOf(path);
        _workers = WorkersFor(Badge.Kind);
        ToolTip.SetTip(Badge, Badge.Description);
        AutomationProperties.SetName(Badge, Loc.T("A11y_Drive", Badge.Description));
        _expectedBytes = ExpectedBytesFor(path);
        (_driveTotal, _driveLabel) = DriveOf(path);
        AppSettings.Apply(x => x.LastPath = path);
        Badge.Progress = null;
        Badge.Busy = true;

        _scanRunning = true;
        FilesTable.IsEnabled = false;
        CancelButton.IsVisible = true;
        EmptyHint.IsVisible = false;
        ResetNavigation();
        _store = null;
        SetViewStore(null, 0);
        FileTypesBlock.IsVisible = false;
        CleanupRun.IsEnabled = false;
        DuplicatesButton.IsEnabled = false;

        // Die Zeilen der Quick Wins zeigen auf Knoten des vorigen Baums. Waehrend der Scan
        // laeuft, wird der ersetzt — ein Klick darauf traefe etwas anderes oder nichts.
        QuickWinsBlock.IsVisible = false;
        _wins = [];

        ChangesBlock.IsVisible = false;
        ChangesList.ItemsSource = null;

        _dupSearch?.Cancel();
        DuplicatesList.ItemsSource = null;
        DuplicatesTotal.Text = string.Empty;
        _duplicates = [];
        _cleanup.Clear();
        ShowCleanup();

        try
        {
            await (run ?? ((p, w, update, token) => ScanSession.RunAsync(p,
                new WalkOptions { WorkerCount = w, ExcludedPaths = AppSettings.Current.ExcludedPaths, Method = AppSettings.Current.ScanMethod }, update, token)))
                (path, _workers, update =>
            {
                if (!ReferenceEquals(_scan, session) || session.IsCancellationRequested) return;
                OnScanUpdate(update);
            }, session.Token);
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_scan, session)) StatusText.Text = Loc.T("Stat_Cancelled");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write($"Scan von {path} fehlgeschlagen", ex);
            if (ReferenceEquals(_scan, session)) StatusText.Text = Loc.T("Stat_ScanFailed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_scan, session))
            {
                _scan = null;
                _scanRunning = false;
                FilesTable.IsEnabled = true;
                Badge.Busy = false;
                CancelButton.IsVisible = false;
                CleanupRun.IsEnabled = true;
                DuplicatesButton.IsEnabled = true;
                if (IsLive(_inspected)) ShowNode(_inspected);
                RefreshFolderButton.IsEnabled = true;
                DrainChanges();
            }
        }
    }

    /// <summary>
    /// Vergleicht den Fund mit dem, was Windows als belegt meldet — aber nur beim Scan einer
    /// ganzen Laufwerkswurzel, wo beide Zahlen dasselbe meinen.
    ///
    /// Ein oft geaeusserter Wunsch an WinDirStat: Wenn ein Werkzeug 698 GB findet und Windows
    /// 702 GB meldet, will man wissen, dass es diese Luecke gibt, statt sich zu fragen, wem
    /// man glauben soll. Die Ursachen sind harmlos und benennbar — nicht lesbare Ordner,
    /// Schattenkopien, belegte Cluster.
    /// </summary>
    private string Discrepancy(long found)
    {
        if (_expectedBytes <= 0 || found <= 0) return string.Empty;

        long gap = _expectedBytes - found;

        // Unter einem Prozent ist der Unterschied Rundung und Clustergroesse, kein Hinweis.
        if (Math.Abs(gap) < _expectedBytes / 100) return string.Empty;

        string direction = gap > 0 ? Loc.T("Stat_Less") : Loc.T("Stat_More");
        return Loc.T("Stat_Gap", Sizes.Format(Math.Abs(gap)), direction);
    }

    /// <summary>Gesamtgroesse und Name des Laufwerks, auf dem ein Pfad liegt.</summary>
    private static (long Total, string Label) DriveOf(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(path));
            if (root is null) return (0, string.Empty);

            var drive = new DriveInfo(root);
            return drive.IsReady
                ? (drive.TotalSize, root.TrimEnd(Path.DirectorySeparatorChar))
                : (0, string.Empty);
        }
        catch (IOException) { return (0, string.Empty); }
        catch (ArgumentException) { return (0, string.Empty); }
    }

    /// <summary>
    /// Parallele Zugriffe. Auf einer drehenden Platte kostet hohe Parallelitaet durch
    /// Kopfbewegungen mehr, als sie einbringt — dort wird gedrosselt.
    /// </summary>
    private static int WorkersFor(StorageKind kind)
    {
        int chosen = AppSettings.Current.WorkerCount;
        if (chosen > 0) return chosen;

        return kind == StorageKind.Hdd ? 2 : Environment.ProcessorCount;
    }

    /// <summary>
    /// Belegte Bytes des Laufwerks, wenn dessen Wurzel gescannt wird. Nur dann ist der Umfang
    /// vorher bekannt; bei einem Unterordner bleibt der Ring bewusst unbestimmt.
    /// </summary>
    private static long ExpectedBytesFor(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string? root = Path.GetPathRoot(full);
            if (root is null || !string.Equals(full.TrimEnd(Path.DirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
                return 0;

            var drive = new DriveInfo(root);
            return drive.IsReady ? drive.TotalSize - drive.AvailableFreeSpace : 0;
        }
        catch (IOException) { return 0; }
        catch (ArgumentException) { return 0; }
    }

    private void OnScanUpdate(ScanSession.Update update)
    {
        ResetNavigation();
        _store = update.Store;

        Badge.Progress = _expectedBytes > 0
            ? Math.Clamp(update.TotalBytes / (double)_expectedBytes, 0, 1)
            : null;

        SetViewStore(_store, _currentRoot);
        UpdateHeader();

        string unreadable = update.UnreadableFolders > 0
            ? Loc.T("Stat_Unreadable", update.UnreadableFolders)
            : string.Empty;

        StatusText.Text = update.IsFinal
            ? Loc.T("Stat_Done", update.Elapsed.TotalSeconds.ToString("F1"), unreadable,
                Discrepancy(update.TotalBytes)) + Admin()
            : Loc.T("Stat_Scanning", update.FileCount.ToString("N0"));

        if (!update.IsFinal) return;
        StatusText.Text += " " + Loc.T(update.Method == ScanMethod.Ntfs ? "Scan_NtfsUsed" : "Scan_DirectoryUsed");
        if (update.Fallback is not null) StatusText.Text += " " + Loc.T("Scan_Fallback", update.Fallback);

        ShowFileTypes();
        ShowQuickWins();
        OfferDuplicates();
        ShowNode(_currentRoot);

        // Im Hintergrund ablegen: dreissig Megabyte zu schreiben darf die Oberflaeche
        // nicht anhalten, und ob es klappt, aendert am gezeigten Ergebnis nichts.
        NodeStore finished = update.Store;
        _snapshotWork = _snapshots.Enqueue(() => SaveAndCompare(finished));
    }

    // ---------- Erhoehte Rechte ----------

    /// <summary>
    /// Haengt an die Statuszeile, dass erhoeht gelaufen wird — sonst sieht man dem Ergebnis
    /// nicht an, unter welchen Rechten es entstanden ist, und zwei Scans desselben Ordners
    /// widersprechen sich scheinbar grundlos.
    /// </summary>
    private static string Admin()
        => Elevation.IsActive ? "  ·  " + Loc.T("Elev_Running") : string.Empty;

    /// <summary>
    /// Bietet an, sich mit Administratorrechten neu zu starten. Gibt zurueck, ob das
    /// geschieht — dann hat dieser Prozess nichts mehr zu tun.
    ///
    /// Erhoehte Rechte erlauben auch direkte NTFS-Scans, sofern das Laufwerk sie unterstuetzt.
    /// </summary>
    private async Task<bool> OfferElevation()
    {
        if (!Elevation.CanOffer) return false;

        AppSettings settings = AppSettings.Current;

        if (!settings.ElevationPrompt)
        {
            // Die Antwort von damals gilt weiter.
            return settings.AutoElevate && Restart();
        }

        (bool yes, bool remember) = await Confirm.Ask(
            this, Loc.T("Elev_Title"), Loc.T("Elev_Question"), Loc.T("Elev_Yes"),
            Loc.T("Elev_Remember"), focusConfirm: true);

        if (remember)
            AppSettings.Apply(s => { s.ElevationPrompt = false; s.AutoElevate = yes; });

        return yes && Restart();
    }

    private bool Restart()
    {
        if (!Elevation.Relaunch(_args)) return false;

        // Erst schliessen, wenn der neue Prozess wirklich laeuft: Sonst stuende der Benutzer
        // ohne Fenster da, weil er die Rueckfrage von Windows abgebrochen hat.
        Close();
        return true;
    }
}
