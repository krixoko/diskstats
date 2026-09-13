using Avalonia;
using Avalonia.Headless.XUnit;
using DiskStats.App.Rendering;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.App.Tests;

/// <summary>
/// Die Layout-Module sind reine Funktionen: Baum und Flaeche hinein, Formen heraus. Damit
/// lassen sie sich ohne Fenster pruefen — und das lohnt, weil sie den groessten Teil des
/// Zeichencodes ausmachen und ein Fehler darin nur als merkwuerdiges Bild auffaellt.
/// </summary>
public class EngineTests
{
    /// <summary>
    /// Ein kuenstlicher Baum ohne Dateisystem: eine Wurzel, zwoelf Ordner absteigender
    /// Groesse, in jedem eine Datei. Zwoelf, damit die Mind Map ihre Grenze von zehn
    /// ueberschreitet und den Rest ausweisen muss.
    /// </summary>
    private static NodeStore Build(int folders = 12)
    {
        var buffer = new NodeBuffer();

        // Ein echter Zeitstempel, weil die Altersansicht Dateien ohne einen ("mtime <= 0")
        // absichtlich uebergeht — mit Nullen faende sie nichts und der Test pruefte nichts.
        long alt = DateTime.UtcNow.AddYears(-3).Ticks;

        for (int i = 0; i < folders; i++)
        {
            int id = i + 1;
            buffer.AddDirectory(0, id, $"ordner{i:00}", alt, EntryFlags.Directory);
            buffer.AddFile(id, $"datei{i:00}.bin", (folders - i) * 1000L, alt, EntryFlags.None);
        }

        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\test");
        SizeAggregator.Aggregate(store);
        return store;
    }

    /// <summary>Eine Wurzel mit Dateien direkt darunter, ohne Ordner — fuer Flaechenrechnungen.</summary>
    private static NodeStore BuildFlat(params long[] sizes)
    {
        var buffer = new NodeBuffer();
        for (int i = 0; i < sizes.Length; i++)
            buffer.AddFile(0, $"datei{i:00}.bin", sizes[i], 0, EntryFlags.None);

        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\flach");
        SizeAggregator.Aggregate(store);
        return store;
    }

    /// <summary>
    /// Ein grosser Baum, wie ihn ein echter Scan liefert: Ordner mit Unterordnern, darin
    /// tausende Dateien verschiedener Groesse. Ueber 50 000 Knoten, damit jede Ansicht an
    /// ihr Budget stoesst statt einfach alles zu zeichnen.
    /// </summary>
    private static NodeStore BuildLarge(int folders = 50, int subfolders = 10, int files = 100)
    {
        var buffer = new NodeBuffer();
        var random = new Random(7);
        long stamp = DateTime.UtcNow.AddYears(-2).Ticks;
        int nextId = 1;

        for (int i = 0; i < folders; i++)
        {
            int folder = nextId++;
            buffer.AddDirectory(0, folder, $"ordner{i:000}", stamp, EntryFlags.Directory);

            for (int j = 0; j < subfolders; j++)
            {
                int sub = nextId++;
                buffer.AddDirectory(folder, sub, $"unter{j:00}", stamp, EntryFlags.Directory);

                for (int k = 0; k < files; k++)
                    buffer.AddFile(sub, $"datei{k:000}.bin", random.NextInt64(1, 1L << 24), stamp, EntryFlags.None);
            }
        }

        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\gross");
        SizeAggregator.Aggregate(store);
        return store;
    }

    public static TheoryData<ViewKind> AllKinds()
    {
        var data = new TheoryData<ViewKind>();
        foreach (ILayoutEngine engine in ChartView.AllEngines) data.Add(engine.Kind);
        return data;
    }

    private static ILayoutEngine EngineFor(ViewKind kind)
        => ChartView.AllEngines.First(e => e.Kind == kind);

    private static readonly Rect Canvas = new(0, 0, 900, 600);

    [AvaloniaTheory]
    [MemberData(nameof(AllKinds))]
    public void Jede_ansicht_liefert_formen(ViewKind kind)
    {
        NodeStore store = Build();

        IReadOnlyList<VisualItem> items = EngineFor(kind).Layout(store, 0, Canvas);

        Assert.NotEmpty(items);
    }

    /// <summary>
    /// Jede Form muss auf einen Knoten zeigen, den es gibt. Eine Nummer daneben faellt beim
    /// Zeichnen nicht auf, beim Anklicken dann umso mehr.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(AllKinds))]
    public void Jede_form_zeigt_auf_einen_vorhandenen_knoten(ViewKind kind)
    {
        NodeStore store = Build();

        foreach (VisualItem item in EngineFor(kind).Layout(store, 0, Canvas))
            Assert.InRange(item.NodeIndex, 0, store.Count - 1);
    }

    /// <summary>
    /// Ein leerer Ordner ist ein gewoehnlicher Fall, kein Sonderfall. Wieviel eine Ansicht
    /// dabei zeigt, darf sie selbst entscheiden — die Sunburst zeichnet ihren Wurzelring, die
    /// Mind Map gar nichts. Geprueft wird nur, dass nichts wirft und nichts ins Leere zeigt.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(AllKinds))]
    public void Ein_leerer_baum_wirft_nicht(ViewKind kind)
    {
        var buffer = new NodeBuffer();
        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\leer");
        SizeAggregator.Aggregate(store);

        foreach (VisualItem item in EngineFor(kind).Layout(store, 0, Canvas))
            Assert.InRange(item.NodeIndex, 0, store.Count - 1);
    }

    /// <summary>Beim Verkleinern des Fensters wird die Flaeche kurzzeitig winzig.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(AllKinds))]
    public void Eine_winzige_flaeche_wirft_nicht(ViewKind kind)
    {
        NodeStore store = Build();

        EngineFor(kind).Layout(store, 0, new Rect(0, 0, 4, 3));
        EngineFor(kind).Layout(store, 0, new Rect(0, 0, 0, 0));
    }

    [AvaloniaTheory]
    [MemberData(nameof(AllKinds))]
    public void Jede_ansicht_hat_titel_und_hinweis(ViewKind kind)
    {
        ILayoutEngine engine = EngineFor(kind);

        Assert.False(string.IsNullOrWhiteSpace(engine.Title));
        Assert.False(string.IsNullOrWhiteSpace(engine.Hint));
    }

    // ---------- Detailbudget ----------

    /// <summary>
    /// Die Design-Spec verlangt: Es werden nie alle Knoten gezeichnet. Jede Ansicht bricht ab,
    /// sobald eine Form unter die Mindestflaeche faellt oder das Elementbudget erschoepft ist.
    /// Bis hierher galt das nur fuer die Treemap — die uebrigen hatten feste eigene Grenzen.
    /// </summary>
    [AvaloniaTheory]
    [MemberData(nameof(AllKinds))]
    public void Jede_ansicht_haelt_das_detailbudget_ein(ViewKind kind)
    {
        NodeStore store = BuildLarge();
        Assert.True(store.Count >= 50_000, $"Der Testbaum hat nur {store.Count} Knoten.");

        var budget = new LayoutBudget(MinArea: 30, MaxItems: 700);

        IReadOnlyList<VisualItem> items = EngineFor(kind).Layout(store, 0, Canvas, budget);

        Assert.True(items.Count <= budget.MaxItems,
            $"{kind} liefert {items.Count} Formen, erlaubt sind {budget.MaxItems}.");

        // Die Wurzel darf beliebig klein sein — sie ist der Bezugspunkt, nicht ein Element.
        foreach (VisualItem item in items.Where(i => i.NodeIndex != 0))
            Assert.True(item.Area >= budget.MinArea - 1e-6,
                $"{kind}: Knoten {item.NodeIndex} hat nur {item.Area:F1} px².");
    }

    /// <summary>Das Budget greift wirklich: Die Treemap haette Platz fuer weit mehr Kacheln.</summary>
    [AvaloniaFact]
    public void Die_treemap_hoert_beim_elementbudget_auf()
    {
        NodeStore store = BuildLarge();

        IReadOnlyList<VisualItem> items = EngineFor(ViewKind.Treemap)
            .Layout(store, 0, Canvas, new LayoutBudget(MinArea: 1, MaxItems: 300));

        Assert.Equal(300, items.Count);
    }

    /// <summary>Grob zeichnet weniger als fein — sonst waere der Regler wirkungslos.</summary>
    [Fact]
    public void Das_budget_folgt_der_detaileinstellung()
    {
        LayoutBudget coarse = LayoutBudget.FromSettings(new AppSettings { Detail = DetailLevel.Coarse });
        LayoutBudget normal = LayoutBudget.FromSettings(new AppSettings { Detail = DetailLevel.Normal });
        LayoutBudget fine = LayoutBudget.FromSettings(new AppSettings { Detail = DetailLevel.Fine });

        Assert.True(coarse.MinArea > normal.MinArea && normal.MinArea > fine.MinArea);
        Assert.True(coarse.MaxItems < normal.MaxItems && normal.MaxItems < fine.MaxItems);
    }

    /// <summary>
    /// Die Dateitabelle ist eine Ansicht, keine Zeichenflaeche. Eine Layout-Engine dafuer
    /// waere toter Code: der Umschalter blendet die Tabelle ein und die Karte aus.
    /// </summary>
    [Fact]
    public void Die_dateitabelle_ist_keine_layout_engine()
    {
        Assert.DoesNotContain(ChartView.AllEngines, e => e.Kind == ViewKind.Files);
        Assert.Equal(Enum.GetValues<ViewKind>().Length - 1, ChartView.AllEngines.Count);
    }

    // ---------- Treemap ----------

    private static IReadOnlyList<VisualItem> Treemap(NodeStore store, Rect? canvas = null)
        => EngineFor(ViewKind.Treemap).Layout(store, 0, canvas ?? Canvas);

    /// <summary>
    /// Zwei Blaetter duerfen sich nicht ueberdecken — sonst waere eines nicht anklickbar und die
    /// Flaeche loege. Eine halbe Bildpunktbreite Toleranz fuer Rundung.
    /// </summary>
    [AvaloniaFact]
    public void Die_treemap_ueberlappt_keine_blaetter()
    {
        NodeStore store = Build(folders: 12);
        VisualItem[] leaves = [.. Treemap(store).Where(i => !i.IsDirectory)];

        Assert.NotEmpty(leaves);
        for (int a = 0; a < leaves.Length; a++)
            for (int b = a + 1; b < leaves.Length; b++)
            {
                Rect overlap = leaves[a].Bounds.Intersect(leaves[b].Bounds);
                Assert.False(overlap.Width > 0.5 && overlap.Height > 0.5,
                    $"Knoten {leaves[a].NodeIndex} und {leaves[b].NodeIndex} ueberlappen um {overlap.Width:F1} x {overlap.Height:F1}.");
            }
    }

    /// <summary>Was die Kacheln zusammen einnehmen, ist der Rahmen — nicht mehr, nicht weniger.</summary>
    [AvaloniaFact]
    public void Die_treemap_fuellt_den_rahmen_mit_ihren_blaettern()
    {
        NodeStore store = BuildFlat(10_000, 9_000, 8_000, 7_000, 6_000, 5_000, 4_000, 3_000, 2_000, 1_000);

        double sum = Treemap(store).Where(i => !i.IsDirectory).Sum(i => i.Area);
        double frame = Canvas.Width * Canvas.Height;

        Assert.InRange(sum, frame * 0.99, frame * 1.01);
    }

    /// <summary>Dreimal so gross heisst dreimal so viel Flaeche.</summary>
    [AvaloniaFact]
    public void Die_treemap_gibt_geschwistern_flaeche_im_verhaeltnis_ihrer_groesse()
    {
        NodeStore store = BuildFlat(3_000, 1_000, 500, 250);
        IReadOnlyList<VisualItem> items = Treemap(store);

        VisualItem big = items.Single(i => store.Size[i.NodeIndex] == 3_000);
        VisualItem small = items.Single(i => store.Size[i.NodeIndex] == 1_000);

        Assert.InRange(big.Area / small.Area, 3 * 0.95, 3 * 1.05);
    }

    // ---------- Mind Map ----------

    [AvaloniaFact]
    public void Die_mindmap_zeigt_die_mitte_und_hoechstens_zehn_aeste()
    {
        NodeStore store = Build(folders: 12);

        IReadOnlyList<VisualItem> items = EngineFor(ViewKind.MindMap).Layout(store, 0, Canvas);

        Assert.Equal(11, items.Count);
        Assert.Equal(0, items[0].NodeIndex);
    }

    [AvaloniaFact]
    public void Die_mindmap_nimmt_die_groessten_zuerst()
    {
        NodeStore store = Build(folders: 12);

        long[] sizes =
        [
            .. EngineFor(ViewKind.MindMap).Layout(store, 0, Canvas)
                .Skip(1).Select(i => store.Size[i.NodeIndex]),
        ];

        Assert.Equal(sizes.OrderByDescending(s => s), sizes);
    }

    [AvaloniaFact]
    public void Die_mindmap_kommt_auch_mit_wenigen_ordnern_aus()
    {
        NodeStore store = Build(folders: 3);

        Assert.Equal(4, EngineFor(ViewKind.MindMap).Layout(store, 0, Canvas).Count);
    }

    /// <summary>Der Knoten in der Mitte ist immer der groesste — sein Radius sagt nichts anderes.</summary>
    [AvaloniaFact]
    public void Die_mindmap_zeichnet_groessere_ordner_groesser()
    {
        NodeStore store = Build(folders: 12);

        IReadOnlyList<VisualItem> items = EngineFor(ViewKind.MindMap).Layout(store, 0, Canvas);

        Assert.True(items[1].OuterRadius > items[^1].OuterRadius,
            "Der groesste Ast ist nicht groesser gezeichnet als der kleinste.");
    }

    /// <summary>Dateien gehoeren nicht in die Uebersicht der Ordner.</summary>
    [AvaloniaFact]
    public void Die_mindmap_zeigt_nur_ordner()
    {
        var buffer = new NodeBuffer();
        buffer.AddDirectory(0, 1, "ordner", 0, EntryFlags.Directory);
        buffer.AddFile(1, "klein.bin", 10, 0, EntryFlags.None);
        buffer.AddFile(0, "riesig.bin", 999_999, 0, EntryFlags.None);

        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\test");
        SizeAggregator.Aggregate(store);

        IReadOnlyList<VisualItem> items = EngineFor(ViewKind.MindMap).Layout(store, 0, Canvas);

        // Trotz ihrer Groesse taucht die Datei nicht auf: Mitte plus ein Ordner.
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(store.IsDirectory(i.NodeIndex)));
    }
}
