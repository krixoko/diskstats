using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using DiskStats.App.Rendering;

using DiskStats.App.Controls;
using DiskStats.App.Localization;
using DiskStats.Core.Scanning;

namespace DiskStats.App;

/// <summary>
/// Auswahl eines Ordners oder Laufwerks im Stil der Anwendung.
///
/// Bewusst nicht der Systemdialog: Der zeigt Dateien, Ansichtsoptionen und ein Namensfeld —
/// alles bedeutungslos, wenn man ein Ziel zum Scannen sucht. Hier stehen stattdessen die
/// Laufwerke mit ihrem Fuellstand vorn, denn genau danach waehlt man aus.
/// </summary>
public partial class FolderPickerWindow : Window
{
    private const double DriveBarWidth = 186;

    private string _current = string.Empty;
    private string _choice = string.Empty;

    public FolderPickerWindow()
    {
        InitializeComponent();

        UpButton.Click += OnUpClicked;
        CancelButton.Click += (_, _) => Close(null);
        ChooseButton.Click += (_, _) => Close(_choice.Length > 0 ? _choice : null);

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close(null);
            else if (e.Key == Key.Enter && _choice.Length > 0) Close(_choice);
        };

        LoadDrives();
        LoadPlaces();
        Navigate(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    private bool Dark => ActualThemeVariant == ThemeVariant.Dark;

    private void LoadDrives()
    {
        var drives = new List<DriveEntry>();
        int index = 0;

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;

            try
            {
                long total = drive.TotalSize;
                double used = total > 0 ? (total - drive.AvailableFreeSpace) / (double)total : 0;
                string name = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.Name.TrimEnd(Path.DirectorySeparatorChar)
                    : $"{drive.Name.TrimEnd(Path.DirectorySeparatorChar)}  {drive.VolumeLabel}";

                StorageKind kind = VolumeProbe.KindOf(drive.RootDirectory.FullName);

                drives.Add(new DriveEntry(
                    name, drive.RootDirectory.FullName,
                    Loc.T("Side_FreeOf", Sizes.Format(drive.AvailableFreeSpace)),
                    Math.Round(used * DriveBarWidth),
                    new SolidColorBrush(Palette.ForFolderBranch(index++, 0, Dark)),
                    DriveBadge.IconFor(kind), DriveBadge.NameFor(kind)));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        DriveList.ItemsSource = drives;
    }

    /// <summary>Die Orte, an denen erfahrungsgemaess der Platz liegt.</summary>
    private void LoadPlaces()
    {
        (Environment.SpecialFolder Folder, string Label)[] wanted =
        [
            (Environment.SpecialFolder.UserProfile, Loc.T("Pick_Profile")),
            (Environment.SpecialFolder.MyDocuments, Loc.T("Pick_Documents")),
            (Environment.SpecialFolder.MyPictures, Loc.T("Pick_Pictures")),
            (Environment.SpecialFolder.MyVideos, Loc.T("Pick_Videos")),
            (Environment.SpecialFolder.LocalApplicationData, Loc.T("Pick_AppData")),
            (Environment.SpecialFolder.ProgramFiles, Loc.T("Pick_Programs")),
        ];

        var places = new List<FolderEntry>();

        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        if (Directory.Exists(downloads)) places.Add(new FolderEntry(Loc.T("Pick_Downloads"), downloads));

        foreach ((Environment.SpecialFolder folder, string label) in wanted)
        {
            string path = Environment.GetFolderPath(folder);
            if (path.Length > 0 && Directory.Exists(path)) places.Add(new FolderEntry(label, path));
        }

        PlaceList.ItemsSource = places;
    }

    private void Navigate(string path)
    {
        if (!Directory.Exists(path)) return;

        _current = path;
        _choice = path;
        PathText.Text = path;
        ChoiceText.Text = path;
        UpButton.IsEnabled = Path.GetDirectoryName(path) is { Length: > 0 };

        var folders = new List<FolderEntry>();
        string? problem = null;

        try
        {
            foreach (string child in Directory.EnumerateDirectories(path))
            {
                try
                {
                    // Versteckte und Systemordner ausblenden: Sie sind selten das Ziel und
                    // wuerden die Liste mit Dingen wie "System Volume Information" fluten.
                    var info = new DirectoryInfo(child);
                    if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    folders.Add(new FolderEntry(info.Name, child));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (UnauthorizedAccessException) { problem = Loc.T("Pick_Denied"); }
        catch (IOException ex) { problem = ex.Message; }

        folders.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        FolderList.ItemsSource = folders;

        EmptyNote.Text = problem ?? Loc.T("Pick_NoSubfolders");
        EmptyNote.IsVisible = folders.Count == 0;
    }

    private void OnUpClicked(object? sender, RoutedEventArgs e)
    {
        string? parent = Path.GetDirectoryName(_current);
        if (parent is { Length: > 0 }) Navigate(parent);
    }

    private void OnPlaceClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path }) Navigate(path);
    }

    /// <summary>Ein Klick waehlt aus, ein Doppelklick geht hinein — wie im Explorer.</summary>
    private void OnFolderClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path }) return;

        _choice = path;
        ChoiceText.Text = path;
    }

    private void OnFolderOpened(object? sender, TappedEventArgs e)
    {
        if (sender is Button { Tag: string path }) Navigate(path);
    }
}
