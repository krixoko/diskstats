using DiskStats.Core.Storage;

namespace DiskStats.App;

public sealed record SavedFilter(string Name, FileQuery Query)
{
    public override string ToString() => Name;

    public static void Save(string name, FileQuery query)
    {
        name = name.Trim();
        if (name.Length is 0 or > 60) throw new ArgumentException(Localization.Loc.T("Filter_PresetNameError"));
        query.Validate();
        string key = name;
        var filters = AppSettings.Current.SavedFilters.Where(p => !p.Name.Equals(key, StringComparison.OrdinalIgnoreCase)).ToList();
        if (filters.Count >= 30) throw new ArgumentException(Localization.Loc.T("Filter_PresetLimit"));
        filters.Add(new SavedFilter(name, query));
        AppSettings.Apply(s => s.SavedFilters = filters.ToArray());
    }

    public static void Remove(string name) => AppSettings.Apply(s => s.SavedFilters = s.SavedFilters
        .Where(p => !p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray());
}
