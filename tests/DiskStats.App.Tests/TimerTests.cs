using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DiskStats.App.Controls;
using DiskStats.App.Rendering;
using DiskStats.Core.Scanning;
using DiskStats.Core.Storage;

namespace DiskStats.App.Tests;

/// <summary>
/// Zwei Steuerelemente treiben sich mit einer eigenen Bilduhr an. Wird ein solches Element
/// aus dem Fenster genommen, waehrend die Uhr laeuft, tickt sie weiter und zeichnet ins Leere —
/// ein Leck, das erst auffaellt, wenn der Prozess nach Stunden warm bleibt.
/// </summary>
public class TimerTests
{
    private static NodeStore SmallStore()
    {
        var buffer = new NodeBuffer();
        for (int i = 0; i < 6; i++)
        {
            buffer.AddDirectory(0, i + 1, $"ordner{i}", 0, EntryFlags.Directory);
            buffer.AddFile(i + 1, $"datei{i}.bin", (6 - i) * 5000L, 0, EntryFlags.None);
        }

        NodeStore store = NodeStoreBuilder.Build(buffer, @"C:\uhr");
        SizeAggregator.Aggregate(store);
        return store;
    }

    [AvaloniaFact]
    public void Das_abzeichen_haelt_seine_uhr_beim_entfernen_an_und_setzt_sie_beim_einfuegen_fort()
    {
        var badge = new DriveBadge { Busy = true };
        var window = new Window { Width = 200, Height = 200, Content = badge };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(badge.AnimationRunning);

        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(badge.AnimationRunning);

        window.Content = badge;
        Dispatcher.UIThread.RunJobs();
        Assert.True(badge.AnimationRunning);
    }

    [AvaloniaFact]
    public void Das_abzeichen_startet_beim_einfuegen_nur_wenn_es_beschaeftigt_ist()
    {
        var badge = new DriveBadge();
        var window = new Window { Width = 200, Height = 200, Content = badge };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.False(badge.AnimationRunning);
    }

    [AvaloniaFact]
    public void Die_karte_haelt_ihre_bilduhr_beim_entfernen_an()
    {
        var chart = new ChartView();
        chart.SetStore(SmallStore(), 0);

        var window = new Window { Width = 500, Height = 400, Content = chart };
        window.Show();

        for (int i = 0; i < 100 && chart.NodeCount == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.True(chart.NodeCount > 0, "Die Karte wurde nie gezeichnet.");

        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        Assert.False(chart.FrameClockRunning);

        // Zurueck im Fenster laeuft die Uhr genau dann, wenn noch etwas in Bewegung ist.
        window.Content = chart;
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(chart.AnimationRunning, chart.FrameClockRunning);
    }
}
