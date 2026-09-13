namespace DiskStats.Core.Tests;

/// <summary>
/// Legt einen echten Verzeichnisbaum im Temp-Verzeichnis an. Echte Dateien statt Mocks,
/// weil genau das Zusammenspiel mit dem Dateisystem getestet werden soll.
///
/// Das Aufraeumen ist bewusst hartnaeckig: Eine frueher stillschweigend verschluckte
/// Loeschausnahme hat ueber einen Tag Testlaeufe vierundfuenfzig leere Ordner im
/// Temp-Verzeichnis hinterlassen. Ein Werkzeug gegen Plattenmuell darf selbst keinen machen.
/// </summary>
public sealed class TestTree : IDisposable
{
    private const string Marker = "diskstats-test-";

    /// <summary>Reste aelterer Laeufe werden einmal je Prozess mit weggeraeumt.</summary>
    private static int _sweptOnce;

    public string Root { get; }

    public TestTree()
    {
        SweepLeftovers();

        Root = Path.Combine(Path.GetTempPath(), Marker + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public TestTree AddDirectory(string relativePath)
    {
        Directory.CreateDirectory(Path.Combine(Root, relativePath));
        return this;
    }

    public TestTree AddFile(string relativePath, int sizeBytes)
    {
        string full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, new byte[sizeBytes]);
        return this;
    }

    /// <summary>
    /// Eine Datei mit bestimmtem Inhalt. Fuer alles, was nicht nur die Groesse betrachtet —
    /// die Duplikatsuche etwa liest wirklich hinein.
    /// </summary>
    public TestTree AddFile(string relativePath, byte[] content)
    {
        string full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, content);
        return this;
    }

    /// <summary>Fuellt eine Datei wiederholt mit einem Muster, bis die Groesse erreicht ist.</summary>
    public TestTree AddFile(string relativePath, int sizeBytes, byte fill)
    {
        byte[] content = new byte[sizeBytes];
        Array.Fill(content, fill);
        return AddFile(relativePath, content);
    }

    public string PathOf(string relativePath) => Path.Combine(Root, relativePath);

    private readonly List<string> _denied = [];

    /// <summary>
    /// Entzieht dem laufenden Konto das Leserecht auf einen Ordner — der Fall "Zugriff
    /// verweigert" mitten im Baum, den kein Mock so ehrlich nachstellt. Nur Windows; anderswo
    /// gibt die Methode false zurueck und der Test soll sich still beenden.
    /// </summary>
    public bool DenyListing(string relativePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        string full = PathOf(relativePath);
        var info = new DirectoryInfo(full);
        System.Security.AccessControl.DirectorySecurity acl = info.GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny));
        info.SetAccessControl(acl);
        _denied.Add(full);
        return true;
    }

    private static void AllowListing(string full)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var info = new DirectoryInfo(full);
            System.Security.AccessControl.DirectorySecurity acl = info.GetAccessControl();
            acl.RemoveAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                System.Security.Principal.WindowsIdentity.GetCurrent().User!,
                System.Security.AccessControl.FileSystemRights.ListDirectory,
                System.Security.AccessControl.AccessControlType.Deny));
            info.SetAccessControl(acl);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        foreach (string full in _denied) AllowListing(full);
        TryDelete(Root);
    }

    /// <summary>
    /// Mehrere Anlaeufe: Direkt nach einem Scan halten Virenscanner oder der Indexdienst
    /// gelegentlich noch kurz ein Handle, und der erste Versuch scheitert.
    /// </summary>
    private static void TryDelete(string path)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (DirectoryNotFoundException) { return; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            Thread.Sleep(30 * (attempt + 1));
        }
    }

    private static void SweepLeftovers()
    {
        if (Interlocked.Exchange(ref _sweptOnce, 1) != 0) return;

        try
        {
            DateTime cutoff = DateTime.UtcNow.AddHours(-1);

            foreach (string path in Directory.EnumerateDirectories(Path.GetTempPath(), Marker + "*"))
            {
                // Nur Altlasten: ein gleichzeitig laufender Testlauf soll ungestoert bleiben.
                if (Directory.GetCreationTimeUtc(path) > cutoff) continue;
                TryDelete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
