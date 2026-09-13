using Avalonia.Media;

namespace DiskStats.App;

/// <summary>Ein Laufwerk in der Seitenleiste.</summary>
public sealed record DriveEntry(
    string Label, string Path, string FreeText, double UsedBarWidth, IBrush Fill,
    string Icon, string Kind);

/// <summary>Eine Veraenderung seit dem letzten Stand.</summary>
public sealed record ChangeRow(
    string Path, string Name, string Detail, string DeltaText, IBrush Fill);

/// <summary>Eine Gruppe inhaltsgleicher Dateien in der Seitenleiste.</summary>
public sealed record DuplicateRow(
    int Index, string Title, string Detail, string SizeText, IBrush Fill);

/// <summary>Eine Zeile in der Liste der groessten Inhalte im Inspector.</summary>
public sealed record ChildEntry(string Name, string SizeText, double BarWidth, IBrush Fill);

/// <summary>Ein Eintrag der Farblegende in der Statusleiste.</summary>
public sealed record LegendEntry(string Name, IBrush Fill);

/// <summary>Ein Dateityp mit seinem Anteil am Scan.</summary>
public sealed record TypeEntry(string Key, string Name, string SizeText, IBrush Fill, double BarWidth);

/// <summary>Ein Ordner in der Auswahl — Schnellzugriff oder Unterordner.</summary>
public sealed record FolderEntry(string Name, string Path);

/// <summary>Ein Suchtreffer mit seinem Fundort.</summary>
public sealed record SearchRow(int Node, string Name, string Location, string SizeText, IBrush Fill);

/// <summary>Eine Zeile der Aufraeumliste.</summary>
public sealed record CleanupRow(string Path, string Name, string SizeText);

/// <summary>Eine Gruppe der Quick Wins in der Seitenleiste.</summary>
public sealed record QuickWinRow(
    int Index, string Title, string Detail, string Hint, string SizeText, IBrush Fill);

/// <summary>Eine Beschriftungs-Wert-Zeile in der Detailkarte.</summary>
public sealed record DetailRow(string Label, string Value);
