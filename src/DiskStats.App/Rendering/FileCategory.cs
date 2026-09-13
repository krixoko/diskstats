using Avalonia.Media;

using DiskStats.App.Localization;

namespace DiskStats.App.Rendering;

public enum FileCategory
{
    Other = 0,
    Video,
    Image,
    Audio,
    Document,
    Code,
    Archive,
    Program,
}

/// <summary>
/// Ordnet Dateien einer Kategorie zu und gibt ihr eine Farbe.
///
/// Die Farbe ist das einzige gesaettigte Element der Oberflaeche — alles andere ist neutral
/// gehalten, damit die Karte nicht mit ihrer eigenen Umgebung konkurriert. Die acht Farbtoene
/// sind ueber den Farbkreis verteilt und in Helligkeit angeglichen, sodass keine Kategorie
/// allein durch Leuchtkraft wichtiger wirkt als eine andere.
/// </summary>
public static class FileCategories
{
    public static FileCategory Of(ReadOnlySpan<char> name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || dot == name.Length - 1) return FileCategory.Other;

        Span<char> ext = stackalloc char[12];
        ReadOnlySpan<char> raw = name[(dot + 1)..];
        if (raw.Length > ext.Length) return FileCategory.Other;
        for (int i = 0; i < raw.Length; i++) ext[i] = char.ToLowerInvariant(raw[i]);

        return ext[..raw.Length] switch
        {
            "mp4" or "mkv" or "avi" or "mov" or "wmv" or "webm" or "m4v" or "mpg" or "mpeg" or "flv"
                => FileCategory.Video,
            "jpg" or "jpeg" or "png" or "gif" or "bmp" or "webp" or "tiff" or "tif" or "heic"
                or "raw" or "cr2" or "nef" or "svg" or "ico" or "psd"
                => FileCategory.Image,
            "mp3" or "flac" or "wav" or "aac" or "ogg" or "m4a" or "wma" or "opus" or "aiff"
                => FileCategory.Audio,
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "txt" or "md"
                or "rtf" or "odt" or "ods" or "csv" or "epub"
                => FileCategory.Document,
            "cs" or "js" or "ts" or "tsx" or "jsx" or "py" or "java" or "cpp" or "c" or "h" or "hpp"
                or "rs" or "go" or "rb" or "php" or "html" or "css" or "json" or "xml" or "yml"
                or "yaml" or "sql" or "sh" or "ps1" or "toml"
                => FileCategory.Code,
            "zip" or "rar" or "7z" or "tar" or "gz" or "bz2" or "xz" or "iso" or "cab" or "zst"
                => FileCategory.Archive,
            "exe" or "dll" or "msi" or "sys" or "so" or "dylib" or "appx" or "msix" or "pdb"
                or "lib" or "bin" or "dat" or "vhdx" or "vhd" or "wim"
                => FileCategory.Program,
            _ => FileCategory.Other,
        };
    }

    public static string DisplayName(FileCategory category) => category switch
    {
        FileCategory.Video => Loc.T("Cat_Video"),
        FileCategory.Image => Loc.T("Cat_Images"),
        FileCategory.Audio => Loc.T("Cat_Audio"),
        FileCategory.Document => Loc.T("Cat_Documents"),
        FileCategory.Code => Loc.T("Cat_Code"),
        FileCategory.Archive => Loc.T("Cat_Archives"),
        FileCategory.Program => Loc.T("Cat_Programs"),
        _ => Loc.T("Cat_Other"),
    };

    public static Color ColorOf(FileCategory category, bool darkTheme)
        => Palette.ForCategory(category, darkTheme);

    public static IEnumerable<FileCategory> All =>
    [
        FileCategory.Video, FileCategory.Image, FileCategory.Audio, FileCategory.Document,
        FileCategory.Code, FileCategory.Archive, FileCategory.Program, FileCategory.Other,
    ];
}
