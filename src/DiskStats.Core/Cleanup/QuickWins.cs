using DiskStats.Core.Storage;

namespace DiskStats.Core.Cleanup;

/// <summary>Wie folgenlos das Loeschen ist.</summary>
public enum WinRisk
{
    /// <summary>Entsteht beim naechsten Gebrauch neu. Kostet hoechstens Zeit.</summary>
    Regrows,

    /// <summary>Kann weg, aber nicht blind — hier stecken Entscheidungen oder eigene Dateien drin.</summary>
    LookFirst,
}

/// <summary>
/// Findet moegliche Platzfresser anhand ihrer Namen. Namen allein beweisen weder,
/// dass der Inhalt entbehrlich ist, noch dass er wiederhergestellt werden kann.
/// Daher werden diese Treffer nur zur Durchsicht angeboten.
/// </summary>
public static class QuickWins
{
    /// <summary>Wo ein Name ueberhaupt etwas bedeutet.</summary>
    public enum Context
    {
        /// <summary>Der Name genuegt — es gibt keine andere Lesart.</summary>
        Anywhere,

        /// <summary>Nur neben einer Projektdatei oder einem .git — sonst heisst "bin" nur "bin".</summary>
        Project,

        /// <summary>Nur direkt im Benutzerprofil, erkennbar an ntuser.dat daneben.</summary>
        Profile,
    }

    /// <summary>
    /// Titel und Hinweis stehen nicht hier, sondern hinter <see cref="Key"/> in der
    /// Oberflaeche: Eine Regel ist eine Tatsache ueber Ordnernamen, kein deutscher Satz.
    /// </summary>
    public sealed record Rule(string Key, WinRisk Risk, string[] Folders, Context Where = Context.Anywhere);

    public sealed record Result(Rule Rule, long Bytes, IReadOnlyList<int> Nodes)
    {
        public int Count => Nodes.Count;
    }

    /// <summary>
    /// Kleiner lohnt kein Fund: Ein bin-Ordner mit zwanzig Kilobyte ist Laerm in einer Liste,
    /// die Platz verspricht.
    /// </summary>
    public const long DefaultMinBytes = 1024 * 1024;

    /// <summary>
    /// Nach Ordnernamen, an beliebiger Stelle im Baum. Ein Treffer wird als Ganzes genommen
    /// und nicht weiter betreten — sonst zaehlte ein node_modules im node_modules doppelt.
    ///
    /// "Waechst nach" bekommt nur, was ohne Zweifel neu entsteht: Build-Ausgabe neben ihrer
    /// Projektdatei und reine Werkzeug-Zwischenspeicher. Alles, was Konfiguration, Zugangsdaten
    /// oder eigene Dateien enthalten kann, bleibt zum Ansehen.
    /// </summary>
    public static readonly Rule[] Rules =
    [
        new("PackageDirs", WinRisk.LookFirst, ["node_modules", "bower_components", "vendor"], Context.Project),

        new("PackageCaches", WinRisk.LookFirst,
            [".npm", "_cacache", ".nuget", ".cargo", ".m2", ".gradle", "pip", "wheels", "uv"]),

        new("BuildOutput", WinRisk.Regrows, ["bin", "obj", "target", "dist", ".next", ".nuxt", "DerivedData"], Context.Project),

        new("VirtualEnvs", WinRisk.LookFirst, [".venv", "venv"], Context.Project),

        new("PyCaches", WinRisk.Regrows, ["__pycache__", ".pytest_cache", ".mypy_cache"]),

        new("AppCaches", WinRisk.LookFirst,
            ["Cache", "Caches", "CacheStorage", "Code Cache", "GPUCache", "ShaderCache",
             "cache2", "Service Worker"]),

        new("TempFiles", WinRisk.LookFirst, ["Temp", "tmp", "CrashDumps", "WER"]),

        new("WindowsOld", WinRisk.LookFirst, ["Windows.old", "$WINDOWS.~BT", "$WINDOWS.~WS"]),

        new("Downloads", WinRisk.LookFirst, ["Downloads"], Context.Profile),

        new("VmImages", WinRisk.LookFirst, ["wsl", "DockerDesktopWSL", "vm_bundles", "Virtual Machines"]),
    ];

    /// <summary>Dateien, die einen Ordner als Projekt ausweisen. Endungen gelten fuer jeden Namen.</summary>
    private static readonly HashSet<string> ProjectFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "package.json", "composer.json", "Cargo.toml", "pyproject.toml", "requirements.txt", "setup.py",
        "go.mod", "pom.xml", "build.gradle", "build.gradle.kts", "CMakeLists.txt", "Makefile",
        "Directory.Build.props", "global.json", "mix.exs", "Gemfile", "Package.swift",
    };

    private static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".xcodeproj", ".vcxproj",
    };

    public static IReadOnlyList<Result> Find(NodeStore store, int root, long minBytes = DefaultMinBytes)
    {
        var lookup = new Dictionary<string, Rule>(StringComparer.OrdinalIgnoreCase);
        foreach (Rule rule in Rules)
            foreach (string folder in rule.Folders)
                lookup.TryAdd(folder, rule);

        var found = new Dictionary<Rule, (long Bytes, List<int> Nodes)>();
        var stack = new Stack<int>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            int node = stack.Pop();
            int start = store.ChildStart[node];
            int count = store.ChildCount[node];

            // Was neben den Kandidaten liegt, entscheidet mit — einmal je Ordner nachsehen.
            bool isProject = false, isProfile = false;
            for (int i = start; i < start + count; i++)
            {
                if (store.IsRemoved(i)) continue;
                string name = store.GetName(i);
                if (store.IsDirectory(i)) { isProject |= name.Equals(".git", StringComparison.OrdinalIgnoreCase); continue; }
                isProject |= ProjectFiles.Contains(name) || ProjectExtensions.Contains(Path.GetExtension(name));
                isProfile |= name.Equals("ntuser.dat", StringComparison.OrdinalIgnoreCase);
            }

            for (int i = start; i < start + count; i++)
            {
                if (!store.IsDirectory(i) || store.IsRemoved(i)) continue;

                long size = store.Size[i];
                if (size <= 0) continue;

                if (lookup.TryGetValue(store.GetName(i), out Rule? rule)
                    && rule.Where switch { Context.Project => isProject, Context.Profile => isProfile, _ => true })
                {
                    if (size < minBytes) continue;   // zu klein fuer einen Fund, aber auch nicht weiter absteigen
                    // Getroffen: als Ganzes nehmen und nicht weiter absteigen.
                    if (!found.TryGetValue(rule, out (long Bytes, List<int> Nodes) sum))
                        sum = (0, []);

                    sum.Nodes.Add(i);
                    found[rule] = (sum.Bytes + size, sum.Nodes);
                    continue;
                }

                stack.Push(i);
            }
        }

        // Erst was nachwaechst, dann was man ansehen muss. Nach Groesse allein stuende der
        // Downloads-Ordner meist ganz oben — die eine Zeile, bei der Zugreifen falsch waere.
        return found
            .Select(e => new Result(e.Key, e.Value.Bytes, e.Value.Nodes))
            .OrderBy(r => r.Rule.Risk)
            .ThenByDescending(r => r.Bytes)
            .ToList();
    }

    /// <summary>
    /// Was gefahrlos zurueckzuholen ist — die Zahl, die oben in der Liste steht.
    ///
    /// Absichtlich ohne die anzusehenden Funde: Der Downloads-Ordner ist meist der groesste
    /// Posten, und ihn mitzuzaehlen hiesse, mit Platz zu werben, den niemand einfach freigeben
    /// kann. Eine zu grosse Zahl ist hier schlimmer als eine zu kleine.
    /// </summary>
    public static long ReclaimableOf(IReadOnlyList<Result> results)
        => results.Where(r => r.Rule.Risk == WinRisk.Regrows).Sum(r => r.Bytes);
}
