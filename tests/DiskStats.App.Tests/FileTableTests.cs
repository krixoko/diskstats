using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DiskStats.App.Controls;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.App.Tests;

public sealed class FileTableTests
{
    private static NodeStore Store(int count = 500)
    {
        var buffer = new NodeBuffer();
        for (int i = 0; i < count; i++)
            buffer.AddFile(0, $"file-{i:D4}.bin", i + 1, DateTime.UtcNow.AddDays(-i).Ticks, EntryFlags.None,
                new FileStorageInfo((i + 1) * 4096, new FileIdentity(1, (ulong)i + 1, 0), 1));
        var store = NodeStoreBuilder.Build(buffer, @"C:\fixture");
        SizeAggregator.Aggregate(store);
        return store;
    }

    /// <summary>
    /// Eine Laufwerkswurzel mit "Unterordner einbeziehen" hat Millionen Treffer. Die Tabelle
    /// zeigt die ersten nach Sortierung und sagt, wie viele es insgesamt sind — statt Millionen
    /// Zeilenobjekte zu bauen und die Oberflaeche sekundenlang anzuhalten.
    /// </summary>
    [Fact]
    public void Tabelle_begrenzt_die_zeilen_und_nennt_die_gesamtzahl()
    {
        NodeStore store = Store(1000);
        FileTable.Page page = FileTable.BuildPage(store, 0, new FileQuery(), false, FileSort.Logical, true,
            limit: 100, TestContext.Current.CancellationToken);

        Assert.Equal(100, page.Rows.Count);
        Assert.Equal(1000, page.Total);
        Assert.Equal(1000, page.Rows[0].Logical);   // die groessten zuerst, nicht irgendwelche
        Assert.Equal(901, page.Rows[^1].Logical);
    }

    [Fact]
    public void Tabelle_sortiert_numerisch_und_filtert_ohne_200_treffer_grenze()
    {
        NodeStore store = Store();
        var rows = FileTable.BuildRows(store, 0, new FileQuery(), false, FileSort.Logical, false, TestContext.Current.CancellationToken);
        Assert.Equal(500, rows.Count);
        Assert.Equal(1, rows[0].Logical);
        Assert.Equal(500, rows[^1].Logical);
        rows = FileTable.BuildRows(store, 0, new FileQuery { MinBytes = 250 }, false, FileSort.Physical, true, TestContext.Current.CancellationToken);
        Assert.Equal(251, rows.Count);
        Assert.Equal(500 * 4096, rows[0].Physical);
        rows = FileTable.BuildRows(store, 0, new FileQuery(), false, FileSort.Modified, true, TestContext.Current.CancellationToken);
        Assert.Equal("file-0000.bin", store.GetName(rows[0].Node));
    }

    [AvaloniaFact]
    public async Task Tabelle_scrollt_bis_zur_letzten_datei_und_virtualisiert_zeilen()
    {
        var table = new FileTable();
        var window = new Window { Width = 1100, Height = 360, Content = table };
        try
        {
            window.Show();
            table.SetStore(Store(), 0);
            await table.Pending;
            Dispatcher.UIThread.RunJobs();
            ListBox rows = table.FindControl<ListBox>("Rows")!;
            Assert.Equal(500, rows.ItemCount);
            rows.ScrollIntoView(rows.Items[499]!);
            rows.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(rows.ContainerFromIndex(499));
            Assert.True(rows.GetVisualDescendants().OfType<ListBoxItem>().Count() < 100);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Veraltete_tabellenarbeit_ueberschreibt_neuen_baum_nicht()
    {
        var table = new FileTable();
        table.SetStore(Store(2000), 0);
        table.SetStore(Store(3), 0);
        await table.Pending;
        Assert.Equal(3, table.FindControl<ListBox>("Rows")!.ItemCount);
        table.SetSort(FileSort.Logical, false);
        await table.Pending;
        Assert.Equal(1, ((FileTableRow)table.FindControl<ListBox>("Rows")!.Items[0]!).Logical);
    }
}
