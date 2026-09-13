using System.Globalization;
using Avalonia.Controls;
using Avalonia.Layout;
using DiskStats.App.Localization;
using DiskStats.Core.Storage;

namespace DiskStats.App;

public sealed record FilterSelection(FileQuery Query, string[] ExcludedPaths);

public sealed class FilterWindow : Window
{
    public FilterWindow(FileQuery query, string[] exclusions)
    {
        Title = Loc.T("Filter_Title");
        Width = 560;
        Height = 660;
        MinHeight = 380;
        MinWidth = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this.Bind(BackgroundProperty, this.GetResourceObservable("Page"));
        var name = new TextBox { Text = query.Name };
        var path = new TextBox { Text = query.PathPattern };
        var min = new TextBox { Text = query.MinBytes?.ToString(CultureInfo.InvariantCulture) };
        var max = new TextBox { Text = query.MaxBytes?.ToString(CultureInfo.InvariantCulture) };
        var age = new TextBox { Text = query.OlderThanDays?.ToString(CultureInfo.InvariantCulture) };
        var regex = new CheckBox { Content = Loc.T("Filter_Regex"), IsChecked = query.UseRegex };
        var exclude = new TextBox { Text = string.Join(Environment.NewLine, exclusions), AcceptsReturn = true, Height = 76 };
        var error = new TextBlock { TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        var layout = new StackPanel { Margin = new Avalonia.Thickness(22), Spacing = 8 };
        layout.Children.Add(new TextBlock { Text = Title, FontSize = 19, FontWeight = Avalonia.Media.FontWeight.Bold });
        void Field(string label, Control control)
        {
            string text = Loc.T(label);
            Avalonia.Automation.AutomationProperties.SetName(control, text);
            layout.Children.Add(new TextBlock { Text = text });
            layout.Children.Add(control);
        }
        Field("Filter_Name", name);
        Field("Filter_Path", path);
        layout.Children.Add(regex);
        Field("Filter_Min", min);
        Field("Filter_Max", max);
        Field("Filter_Age", age);
        Field("Filter_Exclude", exclude);
        layout.Children.Add(new TextBlock { Text = Loc.T("Filter_Hint"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        layout.Children.Add(error);
        FileQuery ReadQuery()
        {
            var selected = new FileQuery { Name = name.Text ?? "", PathPattern = path.Text ?? "",
                UseRegex = regex.IsChecked == true, MinBytes = Number(min.Text), MaxBytes = Number(max.Text),
                OlderThanDays = string.IsNullOrWhiteSpace(age.Text) ? null : checked((int)Number(age.Text)!.Value) };
            selected.Validate();
            return selected;
        }
        var presets = new ComboBox { ItemsSource = AppSettings.Current.SavedFilters, PlaceholderText = Loc.T("Filter_Saved"),
            HorizontalAlignment = HorizontalAlignment.Stretch };
        var presetName = new TextBox { PlaceholderText = Loc.T("Filter_SaveAs"), MaxLength = 60 };
        Avalonia.Automation.AutomationProperties.SetName(presets, Loc.T("Filter_Saved"));
        Avalonia.Automation.AutomationProperties.SetName(presetName, Loc.T("Filter_SaveAs"));
        presets.SelectionChanged += (_, _) => {
            if (presets.SelectedItem is not SavedFilter preset) return;
            FileQuery q = preset.Query;
            name.Text = q.Name; path.Text = q.PathPattern; regex.IsChecked = q.UseRegex;
            min.Text = q.MinBytes?.ToString(CultureInfo.InvariantCulture);
            max.Text = q.MaxBytes?.ToString(CultureInfo.InvariantCulture);
            age.Text = q.OlderThanDays?.ToString(CultureInfo.InvariantCulture);
            presetName.Text = preset.Name;
        };
        var presetButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var save = new Button { Content = Loc.T("Filter_Save") };
        var remove = new Button { Content = Loc.T("Filter_Remove") };
        save.Click += async (_, _) => {
            try {
                FileQuery q = ReadQuery();
                string title = (presetName.Text ?? "").Trim();
                if (AppSettings.Current.SavedFilters.Any(p => p.Name.Equals(title, StringComparison.OrdinalIgnoreCase))
                    && !await Confirm.Ask(this, Loc.T("Filter_Save"), Loc.T("Filter_Replace", title), Loc.T("Filter_Save"))) return;
                SavedFilter.Save(title, q);
                presets.ItemsSource = AppSettings.Current.SavedFilters;
                presets.SelectedItem = AppSettings.Current.SavedFilters.First(p => p.Name.Equals(title, StringComparison.OrdinalIgnoreCase));
                error.Text = string.Empty;
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            { error.Text = Loc.T("Filter_Invalid", ex.Message); }
        };
        remove.Click += (_, _) => {
            if (presets.SelectedItem is not SavedFilter preset) return;
            SavedFilter.Remove(preset.Name); presets.ItemsSource = AppSettings.Current.SavedFilters;
            presetName.Text = string.Empty;
        };
        presetButtons.Children.Add(save); presetButtons.Children.Add(remove);
        layout.Children.Add(presets); layout.Children.Add(presetName); layout.Children.Add(presetButtons);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var reset = new Button { Content = Loc.T("Filter_Reset") };
        reset.Click += (_, _) =>
        {
            name.Text = path.Text = min.Text = max.Text = age.Text = exclude.Text = string.Empty;
            regex.IsChecked = false;
            error.Text = string.Empty;
        };
        var cancel = new Button { Content = Loc.T("Common_Cancel"), IsCancel = true };
        cancel.Classes.Add("quiet");
        cancel.Click += (_, _) => Close(null);
        var apply = new Button { Content = Loc.T("Filter_Apply") };
        apply.Classes.Add("primary");
        reset.Classes.Add("quiet");
        apply.Click += (_, _) =>
        {
            try
            {
                FileQuery selected = ReadQuery();
                Close(new FilterSelection(selected, (exclude.Text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
            { error.Text = Loc.T("Filter_Invalid", ex.Message); }
        };
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        buttons.Children.Add(apply);
        buttons.Margin = new Avalonia.Thickness(22, 8, 22, 16);
        var frame = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Bottom);
        frame.Children.Add(buttons);
        frame.Children.Add(new ScrollViewer { Content = layout });
        Content = frame;
    }

    private static long? Number(string? text) => string.IsNullOrWhiteSpace(text) ? null
        : long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
