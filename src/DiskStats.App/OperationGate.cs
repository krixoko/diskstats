namespace DiskStats.App;

/// <summary>
/// Was das Hauptfenster gerade tut — und was deshalb nicht gleichzeitig geht.
///
/// Vorher standen vier Bool-Felder verstreut im Fenster, und jede Aktion pruefte ihre eigene
/// Auswahl davon: "Aufraeumen" sechs, "Verschieben" fuenf, "Navigieren" zwei. Wer eine fuenfte
/// Operation hinzufuegte, musste alle Stellen finden. Hier steht die Verträglichkeit an einem
/// Ort, mit Namen statt Kombinationen — und als Tabelle testbar.
///
/// Aufraeumen und Verschieben teilen sich <see cref="ChangingFiles"/>: Beide aendern Dateien und
/// verlangen denselben Ausschluss. Das Verschieben hat dafuer frueher das Feld des Aufraeumens
/// mitbenutzt, ohne dass der Name es verriet.
///
/// Nur auf dem UI-Faden anfassen; die Zustaende sind absichtlich nicht synchronisiert.
/// </summary>
internal sealed class OperationGate
{
    /// <summary>Ein Scan laeuft (auch der abschliessende Aufbau).</summary>
    public bool Scanning { get; set; }

    /// <summary>Ein Ordner wird neu eingelesen.</summary>
    public bool Refreshing { get; set; }

    /// <summary>Dateien werden gerade geloescht oder verschoben.</summary>
    public bool ChangingFiles { get; set; }

    /// <summary>Das Benchmark-Fenster ist offen.</summary>
    public bool BenchmarkOpen { get; set; }

    /// <summary>Irgendetwas ausser dem Benchmark liest oder aendert den Baum.</summary>
    public bool AnyRunning => Scanning || Refreshing || ChangingFiles;

    /// <summary>
    /// Ein neuer Scan darf starten. Ein laufender Refresh stoert nicht — er wird vom Scan
    /// selbst abgebrochen —, ein laufender Scan ebenso wenig: er wird ersetzt.
    /// </summary>
    public bool CanStartScan => !ChangingFiles && !BenchmarkOpen;

    /// <summary>Den Baum ansehen, durch ihn navigieren, in ihm suchen. Waehrend eines Scans ist er im Umbau.</summary>
    public bool CanBrowse => !Scanning && !ChangingFiles;

    /// <summary>Etwas auf die Aufraeumliste setzen. Nur der Scan stoert; die Liste selbst aendert keine Dateien.</summary>
    public bool CanStage => !Scanning;

    /// <summary>Analysen starten (Duplikate, Vergleiche): wie Navigieren, aber nicht neben dem Benchmark.</summary>
    public bool CanAnalyze => CanBrowse && !BenchmarkOpen;

    /// <summary>
    /// Dateien loeschen oder verschieben, einen Ordner neu lesen, den Benchmark oeffnen: Alles, was
    /// den Baum aendert oder die Platte belastet, braucht Ruhe von den anderen.
    /// </summary>
    public bool CanChangeFiles => !AnyRunning && !BenchmarkOpen;
}
