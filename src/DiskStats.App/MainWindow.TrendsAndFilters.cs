using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DiskStats.App.Localization;
using DiskStats.App.Rendering;
using DiskStats.Core.Storage;

namespace DiskStats.App;

public partial class MainWindow
{
    private void BuildSavedFilters()
    {
        SavedFiltersList.Children.Clear();
        SavedFiltersBlock.IsVisible = AppSettings.Current.SavedFilters.Length > 0;
        foreach (SavedFilter preset in AppSettings.Current.SavedFilters)
        {
            var button = new Button { Content = preset.Name, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(8, 5) };
            button.Classes.Add("quiet");
            button.Click += (_, _) => ApplySavedFilter(preset);
            SavedFiltersList.Children.Add(button);
        }
    }

    internal void ApplySavedFilter(SavedFilter preset)
    {
        if (_scanRunning || _cleanupRunning || _store is null) return;
        try
        {
            preset.Query.Validate();
            _fileQuery = preset.Query;
            CloseSearch();
            SelectView(ViewKind.Files);
            FilesTable.SetRecursive(true);
            FilesTable.SetStore(_store, _currentRoot, _fileQuery);
            StatusText.Text = preset.Name;
        }
        catch (ArgumentException ex) { StatusText.Text = Loc.T("Filter_Invalid", ex.Message); }
    }

    private async Task ShowTrendsAsync()
    {
        if (_scanRunning || _cleanupRunning || _store is null) return;
        int node = IsLive(_inspected) ? _inspected : _currentRoot;
        if (!_store.IsDirectory(node)) node = _store.ParentIndex[node];
        string root = _store.RootPath, folder = _store.PathOf(Math.Max(0, node));
        await _snapshotWork;
        string? path = await new TrendsWindow(root, folder).ShowDialog<string?>(this);
        if (path is null || _store is null) return;
        int found = SubtreeRefresh.Find(_store, path);
        if (found < 0) return;
        if (_store.IsDirectory(found)) OnNodeOpened(found);
        else OnNodeSelected(found);
    }
}
