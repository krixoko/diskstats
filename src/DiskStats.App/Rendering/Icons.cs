namespace DiskStats.App.Rendering;

/// <summary>
/// Die Bildzeichen der Oberflaeche, aus dem Lucide-Satz — demselben, den shadcn/ui verwendet
/// (ISC-Lizenz, lucide.dev). Uebernommen statt nachgezeichnet: Ein selbstgebautes Zeichen
/// trifft die Strichstaerke, die Eckenrundung und das optische Gewicht eines gepflegten
/// Satzes nie ueber zwanzig Symbole hinweg, und genau diese Abweichungen sind es, die eine
/// Oberflaeche selbstgebastelt aussehen lassen.
///
/// Alle Pfade liegen auf einem Feld von 24x24 und sind Konturen, keine Flaechen. Sie muessen
/// deshalb mit Stroke gezeichnet werden — siehe <see cref="IconPresenter"/>.
///
/// Kreise, Rechtecke und Linien der Vorlagen sind in Pfaddaten umgerechnet, weil Avalonia nur
/// die kennt. Beim Zusammenfassen mehrerer Vorlagen-Elemente in einen Pfad wird ein fuehrendes
/// relatives "m" absolut geschrieben: Fuer sich steht es im Ursprung, aneinandergehaengt
/// laege es sonst am Endpunkt des vorigen Teils. Das X war daran zuerst ein Schraegstrich.
/// </summary>
public static class Icons
{
    /// <summary>Kantenlaenge des Felds, auf dem alle Pfade liegen.</summary>
    public const double Grid = 24;

    /// <summary>Strichstaerke auf diesem Feld. Lucide zeichnet durchgaengig mit 2.</summary>
    public const double Stroke = 2;

    public const string ArrowLeft =
        "M 12 19 l -7 -7 l 7 -7 M 19 12 H 5";

    public const string ArrowUp =
        "M 5 12 l 7 -7 l 7 7 M 12 19 V 5";

    public const string CalendarClock =
        "M 16 14 v 2.2 l 1.6 1 M 16 2 v 3 M 21 7.338 V 5 a 2 2 0 0 0 -2 -2 H 5 a 2 2 0 0 0 -2 2 v 14 a 2 2 0 0 0 2 2 h 2.338 M 3 9 h 5.859 M 8 2 v 3 M 10 16 a 6 6 0 1 0 12 0 a 6 6 0 1 0 -12 0";

    public const string ChartBarDecreasing =
        "M 3 3 v 16 a 2 2 0 0 0 2 2 h 16 M 7 11 h 8 M 7 16 h 3 M 7 6 h 12";

    public const string ChartPie =
        "M 21 12 c .552 0 1.005 -.449 .95 -.998 a 10 10 0 0 0 -8.953 -8.951 c -.55 -.055 -.998 .398 -.998 .95 v 8 a 1 1 0 0 0 1 1 z M 21.21 15.89 A 10 10 0 1 1 8 2.83";

    public const string ChartScatter =
        "M 7 7.5 a 0.5 0.5 0 1 0 1 0 a 0.5 0.5 0 1 0 -1 0 M 18 5.5 a 0.5 0.5 0 1 0 1 0 a 0.5 0.5 0 1 0 -1 0 M 11 11.5 a 0.5 0.5 0 1 0 1 0 a 0.5 0.5 0 1 0 -1 0 M 7 16.5 a 0.5 0.5 0 1 0 1 0 a 0.5 0.5 0 1 0 -1 0 M 17 14.5 a 0.5 0.5 0 1 0 1 0 a 0.5 0.5 0 1 0 -1 0 M 3 3 v 16 a 2 2 0 0 0 2 2 h 16";

    public const string ChevronUp =
        "M 18 15 l -6 -6 l -6 6";

    public const string Cloud =
        "M 17.5 19 H 9 a 7 7 0 1 1 6.71 -9 h 1.79 a 4.5 4.5 0 1 1 0 9 Z";

    public const string Cpu =
        "M 12 20 v 2 M 12 2 v 2 M 17 20 v 2 M 17 2 v 2 M 2 12 h 2 M 2 17 h 2 M 2 7 h 2 M 20 12 h 2 M 20 17 h 2 M 20 7 h 2 M 7 20 v 2 M 7 2 v 2 M 6 4 H 18 A 2 2 0 0 1 20 6 V 18 A 2 2 0 0 1 18 20 H 6 A 2 2 0 0 1 4 18 V 6 A 2 2 0 0 1 6 4 Z M 9 8 H 15 A 1 1 0 0 1 16 9 V 15 A 1 1 0 0 1 15 16 H 9 A 1 1 0 0 1 8 15 V 9 A 1 1 0 0 1 9 8 Z";

    public const string Database =
        "M 3 5 a 9 3 0 1 0 18 0 a 9 3 0 1 0 -18 0 M 3 5 V 19 A 9 3 0 0 0 21 19 V 5 M 3 12 A 9 3 0 0 0 21 12";

    public const string Disc3 =
        "M 2 12 a 10 10 0 1 0 20 0 a 10 10 0 1 0 -20 0 M 6 12 c 0 -1.7 .7 -3.2 1.8 -4.2 M 10 12 a 2 2 0 1 0 4 0 a 2 2 0 1 0 -4 0 M 18 12 c 0 1.7 -.7 3.2 -1.8 4.2";

    public const string Flame =
        "M 12 3 q 1 4 4 6.5 t 3 5.5 a 1 1 0 0 1 -14 0 a 5 5 0 0 1 1 -3 a 1 1 0 0 0 5 0 c 0 -2 -1.5 -3 -1.5 -5 q 0 -2 2.5 -4";

    public const string Folder =
        "M 20 20 a 2 2 0 0 0 2 -2 V 8 a 2 2 0 0 0 -2 -2 h -7.9 a 2 2 0 0 1 -1.69 -.9 L 9.6 3.9 A 2 2 0 0 0 7.93 3 H 4 a 2 2 0 0 0 -2 2 v 13 a 2 2 0 0 0 2 2 Z";

    public const string FolderOpen =
        "M 6 14 l 1.5 -2.9 A 2 2 0 0 1 9.24 10 H 20 a 2 2 0 0 1 1.94 2.5 l -1.54 6 a 2 2 0 0 1 -1.95 1.5 H 4 a 2 2 0 0 1 -2 -2 V 5 a 2 2 0 0 1 2 -2 h 3.9 a 2 2 0 0 1 1.69 .9 l .81 1.2 a 2 2 0 0 0 1.67 .9 H 18 a 2 2 0 0 1 2 2 v 2";

    public const string GitFork =
        "M 9 18 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 3 6 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 15 6 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0 M 18 9 v 2 c 0 .6 -.4 1 -1 1 H 7 c -.6 0 -1 -.4 -1 -1 V 9 M 12 12 v 3";

    public const string Gauge =
        "M 12 14 l 4 -4 M 3.34 19 a 10 10 0 1 1 17.32 0 Z";

    public const string HeartPulse =
        "M 19.5 12.572 L 12 20 l -7.5 -7.428 a 5 5 0 1 1 7.5 -6.566 a 5 5 0 1 1 7.5 6.572 M 3.22 12 H 9.5 l .5 -1 l 2 4 l 2 -6 l 1.5 3 h 5.27";

    public const string Grid3x3 =
        "M 5 3 H 19 A 2 2 0 0 1 21 5 V 19 A 2 2 0 0 1 19 21 H 5 A 2 2 0 0 1 3 19 V 5 A 2 2 0 0 1 5 3 Z M 3 9 h 18 M 3 15 h 18 M 9 3 v 18 M 15 3 v 18";

    public const string HardDrive =
        "M 10 16 h .01 M 2.212 11.577 a 2 2 0 0 0 -.212 .896 V 18 a 2 2 0 0 0 2 2 h 16 a 2 2 0 0 0 2 -2 v -5.527 a 2 2 0 0 0 -.212 -.896 L 18.55 5.11 A 2 2 0 0 0 16.76 4 H 7.24 a 2 2 0 0 0 -1.79 1.11 z M 21.946 12.013 H 2.054 M 6 16 h .01";

    public const string LayoutDashboard =
        "M 4 3 H 9 A 1 1 0 0 1 10 4 V 11 A 1 1 0 0 1 9 12 H 4 A 1 1 0 0 1 3 11 V 4 A 1 1 0 0 1 4 3 Z M 15 3 H 20 A 1 1 0 0 1 21 4 V 7 A 1 1 0 0 1 20 8 H 15 A 1 1 0 0 1 14 7 V 4 A 1 1 0 0 1 15 3 Z M 15 12 H 20 A 1 1 0 0 1 21 13 V 20 A 1 1 0 0 1 20 21 H 15 A 1 1 0 0 1 14 20 V 13 A 1 1 0 0 1 15 12 Z M 4 16 H 9 A 1 1 0 0 1 10 17 V 20 A 1 1 0 0 1 9 21 H 4 A 1 1 0 0 1 3 20 V 17 A 1 1 0 0 1 4 16 Z";

    public const string Layers =
        "M 12.83 2.18 a 2 2 0 0 0 -1.66 0 L 2.6 6.08 a 1 1 0 0 0 0 1.83 l 8.58 3.91 a 2 2 0 0 0 1.66 0 l 8.58 -3.9 a 1 1 0 0 0 0 -1.83 z M 2 12 a 1 1 0 0 0 .58 .91 l 8.6 3.91 a 2 2 0 0 0 1.65 0 l 8.58 -3.9 A 1 1 0 0 0 22 12 M 2 17 a 1 1 0 0 0 .58 .91 l 8.6 3.91 a 2 2 0 0 0 1.65 0 l 8.58 -3.9 A 1 1 0 0 0 22 17";

    public const string ListTree =
        "M 8 5 h 13 M 13 12 h 8 M 13 19 h 8 M 3 10 a 2 2 0 0 0 2 2 h 3 M 3 5 v 12 a 2 2 0 0 0 2 2 h 3";

    public const string MemoryStick =
        "M 12 12 v -2 M 12 18 v -2 M 16 12 v -2 M 16 18 v -2 M 2 11 h 1.5 M 20 18 v -2 M 20.5 11 H 22 M 4 18 v -2 M 8 12 v -2 M 8 18 v -2 M 4 6 H 20 A 2 2 0 0 1 22 8 V 14 A 2 2 0 0 1 20 16 H 4 A 2 2 0 0 1 2 14 V 8 A 2 2 0 0 1 4 6 Z";

    public const string Microchip =
        "M 10 12 h 4 M 10 17 h 4 M 10 7 h 4 M 18 12 h 2 M 18 18 h 2 M 18 6 h 2 M 4 12 h 2 M 4 18 h 2 M 4 6 h 2 M 8 2 H 16 A 2 2 0 0 1 18 4 V 20 A 2 2 0 0 1 16 22 H 8 A 2 2 0 0 1 6 20 V 4 A 2 2 0 0 1 8 2 Z";

    public const string Moon =
        "M 20.985 12.486 a 9 9 0 1 1 -9.473 -9.472 c .405 -.022 .617 .46 .402 .803 a 6 6 0 0 0 8.268 8.268 c .344 -.215 .825 -.004 .803 .401";

    public const string Network =
        "M 17 16 H 21 A 1 1 0 0 1 22 17 V 21 A 1 1 0 0 1 21 22 H 17 A 1 1 0 0 1 16 21 V 17 A 1 1 0 0 1 17 16 Z M 3 16 H 7 A 1 1 0 0 1 8 17 V 21 A 1 1 0 0 1 7 22 H 3 A 1 1 0 0 1 2 21 V 17 A 1 1 0 0 1 3 16 Z M 10 2 H 14 A 1 1 0 0 1 15 3 V 7 A 1 1 0 0 1 14 8 H 10 A 1 1 0 0 1 9 7 V 3 A 1 1 0 0 1 10 2 Z M 5 16 v -3 a 1 1 0 0 1 1 -1 h 12 a 1 1 0 0 1 1 1 v 3 M 12 12 V 8";

    public const string Settings =
        "M 9.671 4.136 a 2.34 2.34 0 0 1 4.659 0 a 2.34 2.34 0 0 0 3.319 1.915 a 2.34 2.34 0 0 1 2.33 4.033 a 2.34 2.34 0 0 0 0 3.831 a 2.34 2.34 0 0 1 -2.33 4.033 a 2.34 2.34 0 0 0 -3.319 1.915 a 2.34 2.34 0 0 1 -4.659 0 a 2.34 2.34 0 0 0 -3.32 -1.915 a 2.34 2.34 0 0 1 -2.33 -4.033 a 2.34 2.34 0 0 0 0 -3.831 A 2.34 2.34 0 0 1 6.35 6.051 a 2.34 2.34 0 0 0 3.319 -1.915 M 9 12 a 3 3 0 1 0 6 0 a 3 3 0 1 0 -6 0";

    public const string Sun =
        "M 8 12 a 4 4 0 1 0 8 0 a 4 4 0 1 0 -8 0 M 12 2 v 2 M 12 20 v 2 M 4.93 4.93 l 1.41 1.41 M 17.66 17.66 l 1.41 1.41 M 2 12 h 2 M 20 12 h 2 M 6.34 17.66 l -1.41 1.41 M 19.07 4.93 l -1.41 1.41";

    public const string Usb =
        "M 9 7 a 1 1 0 1 0 2 0 a 1 1 0 1 0 -2 0 M 3 20 a 1 1 0 1 0 2 0 a 1 1 0 1 0 -2 0 M 4.7 19.3 L 19 5 M 21 3 l -3 1 l 2 2 Z M 9.26 7.68 L 5 12 l 2 5 M 10 14 l 5 2 l 3.5 -3.5 M 18 12 l 1 -1 l 1 1 l -1 1 Z";

    public const string Workflow =
        "M 5 3 H 9 A 2 2 0 0 1 11 5 V 9 A 2 2 0 0 1 9 11 H 5 A 2 2 0 0 1 3 9 V 5 A 2 2 0 0 1 5 3 Z M 7 11 v 4 a 2 2 0 0 0 2 2 h 4 M 15 13 H 19 A 2 2 0 0 1 21 15 V 19 A 2 2 0 0 1 19 21 H 15 A 2 2 0 0 1 13 19 V 15 A 2 2 0 0 1 15 13 Z";

    public const string X =
        "M 18 6 L 6 18 M 6 6 l 12 12";

}
