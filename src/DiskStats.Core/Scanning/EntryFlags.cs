namespace DiskStats.Core.Scanning;

/// <summary>Eigenschaften eines Eintrags. Bewusst ein Byte breit, weil pro Knoten gespeichert.</summary>
[Flags]
public enum EntryFlags : byte
{
    None = 0,
    Directory = 1,
    /// <summary>Junction, Symlink oder Mount Point. Wird nicht verfolgt.</summary>
    ReparsePoint = 2,
    /// <summary>OneDrive-Platzhalter: belegt aktuell keinen lokalen Platz.</summary>
    CloudPlaceholder = 4,
    /// <summary>Verzeichnis konnte nicht gelesen werden.</summary>
    Unreadable = 8,
    /// <summary>Nach dem Scan entfernt; kein lebender Eintrag mehr.</summary>
    Removed = 16,
}
