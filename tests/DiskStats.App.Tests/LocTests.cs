using System.Text.RegularExpressions;
using DiskStats.App.Controls;
using DiskStats.App.Localization;
using DiskStats.Core.Cleanup;
using DiskStats.Core.Scanning;

namespace DiskStats.App.Tests;

/// <summary>
/// Uebersetzungen scheitern lautlos: Ein Schluessel, den es nicht gibt, erscheint als
/// Schluessel im Fenster; eine Platzhalterzahl, die nicht passt, wirft erst zur Laufzeit —
/// und zwar nur in der Sprache, in der niemand geprueft hat.
/// </summary>
public class LocTests
{
    private static readonly Regex Placeholder = new(@"\{(\d+)\}", RegexOptions.Compiled);

    private static string[] Slots(string value)
        => [.. Placeholder.Matches(value).Select(m => m.Value).Distinct().Order()];

    public static TheoryData<string> AllKeys()
    {
        var data = new TheoryData<string>();
        foreach (string key in Loc.Entries.Keys) data.Add(key);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllKeys))]
    public void Beide_fassungen_sind_gefuellt(string key)
    {
        (string en, string de) = Loc.Entries[key];

        Assert.False(string.IsNullOrWhiteSpace(en), $"{key}: englische Fassung fehlt");
        Assert.False(string.IsNullOrWhiteSpace(de), $"{key}: deutsche Fassung fehlt");
    }

    /// <summary>
    /// Gleiche Platzhalter in beiden Sprachen. Fehlt einer, verschwindet ein Wert lautlos;
    /// steht einer zuviel da, wirft string.Format — aber eben nur in dieser einen Sprache.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllKeys))]
    public void Beide_fassungen_haben_dieselben_platzhalter(string key)
    {
        (string en, string de) = Loc.Entries[key];

        Assert.Equal(Slots(en), Slots(de));
    }

    [Fact]
    public void Beide_sprachen_liefern_etwas_anderes_als_den_schluessel()
    {
        foreach (Language language in Enum.GetValues<Language>())
        {
            Loc.Current = language;

            foreach (string key in Loc.Entries.Keys)
                Assert.NotEqual(key, Loc.T(key));
        }

        Loc.Current = Language.English;
    }

    [Fact]
    public void Ein_unbekannter_schluessel_faellt_auf()
    {
        // Absicht: sichtbar falsch statt leer. Eine leere Beschriftung sieht aus wie Absicht.
        Assert.Equal("Gibt_Es_Nicht", Loc.T("Gibt_Es_Nicht"));
    }

    /// <summary>
    /// Jeder Schluessel, der im Quelltext steht, muss in der Tabelle stehen. Ein Tippfehler
    /// erscheint sonst als Schluessel in der Oberflaeche — gebaut wird trotzdem sauber.
    /// </summary>
    [Fact]
    public void Kein_schluessel_im_quelltext_fehlt_in_der_tabelle()
    {
        string source = SourceRoot();
        var used = new SortedSet<string>();

        var call = new Regex(@"Loc\.T\(\s*""([A-Za-z_][A-Za-z0-9_]*)""\s*[,)]", RegexOptions.Compiled);
        var markup = new Regex(@"\{loc:T\s+([A-Za-z_][A-Za-z0-9_]*)\s*\}", RegexOptions.Compiled);

        foreach (string file in Directory.EnumerateFiles(source, "*.*", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            if (!file.EndsWith(".cs") && !file.EndsWith(".axaml")) continue;

            string text = File.ReadAllText(file);
            foreach (Match m in call.Matches(text)) used.Add(m.Groups[1].Value);
            foreach (Match m in markup.Matches(text)) used.Add(m.Groups[1].Value);
        }

        Assert.True(used.Count > 50, $"Nur {used.Count} Schluessel gefunden — die Suche greift nicht.");

        string[] missing = [.. used.Where(k => !Loc.Entries.ContainsKey(k))];
        Assert.True(missing.Length == 0, "Fehlen in der Tabelle: " + string.Join(", ", missing));
    }

    /// <summary>
    /// Beschriftungen in Grossbuchstaben ("AUSWAHL") sind Oberflaechentexte, keine Konstanten.
    /// Steht eine als Literal im Quelltext, erscheint sie in beiden Sprachen deutsch — und
    /// niemand merkt es, weil die deutsche Fassung ja stimmt.
    /// </summary>
    [Fact]
    public void Kein_grossgeschriebener_oberflaechentext_steht_hart_im_quelltext()
    {
        string source = SourceRoot();
        var literal = new Regex(@"""[A-ZÄÖÜ]{3,}""", RegexOptions.Compiled);
        // Abkuerzungen, die in jeder Sprache gleich heissen.
        string[] neutral = ["\"CSV\"", "\"NTFS\"", "\"MFT\"", "\"UTF\"", "\"STATIC\"", "\"HWND\""];
        var offenders = new List<string>();

        foreach (string file in Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                || file.EndsWith("Loc.cs")) continue;

            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                // Bewusst erlaubt: Schluessel der Uebersetzungstabelle, Kommentare, P/Invoke-Namen.
                if (line.TrimStart().StartsWith("//") || line.Contains("Loc.T(") || line.Contains("DllImport")) continue;
                if (literal.Matches(line).Any(m => !neutral.Contains(m.Value))) offenders.Add($"{Path.GetRelativePath(source, file)}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0, "Hart kodiert:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Zusammengesetzte Schluessel wie <c>Loc.T("Deny_" + kind)</c> stehen nirgends im
    /// Quelltext und entgehen der Suche oben. Sie sind gerade deshalb heikel: Kommt ein Wert
    /// zur Aufzaehlung dazu, faellt der fehlende Text erst dem Benutzer auf.
    /// </summary>
    [Fact]
    public void Jeder_ablehnungsgrund_hat_einen_text()
    {
        foreach (RefusalKind kind in Enum.GetValues<RefusalKind>())
            Assert.True(Loc.Entries.ContainsKey("Deny_" + kind), $"Deny_{kind} fehlt");
    }

    [Fact]
    public void Jede_quick_win_regel_hat_titel_und_hinweis()
    {
        foreach (QuickWins.Rule rule in QuickWins.Rules)
        {
            Assert.True(Loc.Entries.ContainsKey("Win_" + rule.Key), $"Win_{rule.Key} fehlt");
            Assert.True(Loc.Entries.ContainsKey($"Win_{rule.Key}Hint"), $"Win_{rule.Key}Hint fehlt");
        }
    }

    [Fact]
    public void Jede_art_datentraeger_hat_einen_namen()
    {
        foreach (StorageKind kind in Enum.GetValues<StorageKind>())
        {
            string text = DriveBadge.NameFor(kind);
            Assert.False(text.StartsWith("Disk_"), $"Disk_{kind} fehlt in der Tabelle");
        }
    }

    /// <summary>Findet das Quellverzeichnis, vom Testverzeichnis aus aufwaerts.</summary>
    private static string SourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "src", "DiskStats.App");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            $"src/DiskStats.App nicht gefunden, ausgehend von {AppContext.BaseDirectory}");
    }
}
