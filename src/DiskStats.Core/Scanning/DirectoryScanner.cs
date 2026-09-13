using System.IO.Enumeration;

namespace DiskStats.Core.Scanning;

public readonly record struct PendingDirectory(string Path, int Id);

/// <summary>
/// Vergibt die Verzeichnis-Ids eines Scans. Bewusst eine Instanz je Walk statt eines statischen
/// Zaehlers: Bei zwei gleichzeitigen Scans wuerde ein geteilter Zaehler beide Baeume zerstoeren,
/// weil Ids doppelt vergeben wuerden. Id 0 gehoert immer dem Wurzelknoten.
/// </summary>
public sealed class DirectoryIdAllocator
{
    private int _next;

    public int Next() => Interlocked.Increment(ref _next);
}

public static class DirectoryScanner
{
    // Nicht in FileAttributes enthalten: Windows-Attribute fuer Cloud-Platzhalter.
    private const int FileAttributeRecallOnOpen = 0x00040000;
    private const int FileAttributeRecallOnDataAccess = 0x00400000;

    /// <summary>
    /// Liest genau ein Verzeichnis — ohne Abstieg. Der Abstieg ist Sache des DirectoryWalker,
    /// der die hier gesammelten Unterverzeichnisse parallel abarbeitet.
    /// </summary>
    public static void ScanOne(
        string directory,
        int directoryId,
        IEntrySink sink,
        List<PendingDirectory> subdirectories,
        ScanErrorLog errors,
        DirectoryIdAllocator ids, ScanExclusions? exclusions = null)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,      // Fehler sollen sichtbar werden
            AttributesToSkip = FileAttributes.None,
            ReturnSpecialDirectories = false,
            BufferSize = 65536,              // weniger Syscalls pro Verzeichnis
        };

        try
        {
            using var enumerator = new SinkEnumerator(directory, directoryId, sink, subdirectories, errors, ids, options, exclusions);

            // TransformEntry erledigt die eigentliche Arbeit als Seiteneffekt; Current wird nie gelesen.
            while (enumerator.MoveNext()) { }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Ein einzelner unlesbarer Ordner darf den Scan nicht abbrechen.
            // DirectoryNotFoundException leitet von IOException ab und ist mit abgedeckt.
            errors.Record(directory, ex.HResult);
        }
    }

    private sealed class SinkEnumerator : FileSystemEnumerator<bool>
    {
        private readonly string _directory = null!;
        private readonly int _directoryId;
        private readonly IEntrySink _sink = null!;
        private readonly List<PendingDirectory> _subdirectories = null!;
        private readonly ScanErrorLog? _errors;
        private readonly DirectoryIdAllocator _ids = null!;

        /// <summary>
        /// Fehlercode, den der Basiskonstruktor beim Oeffnen des Verzeichnisses gemeldet hat.
        /// Zu diesem Zeitpunkt existiert noch kein Fehlerprotokoll, deshalb der Umweg.
        /// </summary>
        private int _constructionError;
        private readonly ScanExclusions? _exclusions;

        public SinkEnumerator(
            string directory, int directoryId, IEntrySink sink,
            List<PendingDirectory> subdirectories, ScanErrorLog errors,
            DirectoryIdAllocator ids, EnumerationOptions options, ScanExclusions? exclusions)
            : base(directory, options)
        {
            // Achtung: base(...) laeuft VOR diesen Zuweisungen und ruft dabei bereits
            // ContinueOnError auf, falls das Verzeichnis nicht geoeffnet werden kann.
            _directory = directory;
            _exclusions = exclusions;
            _directoryId = directoryId;
            _sink = sink;
            _subdirectories = subdirectories;
            _errors = errors;
            _ids = ids;

            if (_constructionError != 0)
                errors.Record(directory, _constructionError);
        }

        protected override bool TransformEntry(ref FileSystemEntry entry)
        {
            if (_exclusions?.IsExcluded(entry.ToFullPath()) == true) return false;
            EntryFlags flags = EntryFlags.None;
            int rawAttributes = (int)entry.Attributes;

            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                flags |= EntryFlags.ReparsePoint;

            if ((entry.Attributes & FileAttributes.Offline) != 0
                || (rawAttributes & FileAttributeRecallOnOpen) != 0
                || (rawAttributes & FileAttributeRecallOnDataAccess) != 0)
                flags |= EntryFlags.CloudPlaceholder;

            long mtime = entry.LastWriteTimeUtc.UtcTicks;

            if (entry.IsDirectory)
            {
                flags |= EntryFlags.Directory;
                int ownId = _ids.Next();
                _sink.AddDirectory(_directoryId, ownId, entry.FileName, mtime, flags);

                // Reparse Points werden nicht verfolgt: sonst Doppelzaehlung oder Endlosschleife.
                if ((flags & EntryFlags.ReparsePoint) == 0)
                    _subdirectories.Add(new PendingDirectory(entry.ToFullPath(), ownId));
            }
            else
            {
                // Belegung und Hardlink-Identitaet gibt es nur ueber ein Handle — das kostet je
                // Datei rund ein Drittel der Scanzeit (gemessen: C:\Windows, 437 000 Dateien,
                // 14,9 s mit gegen 10,4 s ohne). Sparen laesst es sich nur dort, wo die Antwort
                // feststeht: Eine leere Datei belegt nichts, und ob sie verlinkt ist, aendert
                // daran nichts.
                FileStorageInfo storage = entry.Length == 0 && (flags & EntryFlags.CloudPlaceholder) == 0
                    && (entry.Attributes & (FileAttributes.Compressed | FileAttributes.SparseFile)) == 0
                    ? FileStorageInfo.Empty
                    : FileStorageInfo.Read(entry.ToFullPath(), entry.Attributes);
                _sink.AddFile(_directoryId, entry.FileName, entry.Length, mtime, flags, storage);
            }

            return true;
        }

        protected override bool ContinueOnError(int error)
        {
            // Wird auch aus dem Basiskonstruktor heraus aufgerufen, wenn das Verzeichnis
            // gar nicht erst geoeffnet werden kann. Dann sind die Felder noch nicht gesetzt.
            if (_errors is null)
                _constructionError = error;
            else
                _errors.Record(_directory, error);

            return true;   // ein unlesbarer Ordner darf den restlichen Scan nicht abbrechen
        }
    }
}
