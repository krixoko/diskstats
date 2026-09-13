using DiskStats.Core.Cleanup;

namespace DiskStats.Core.Tests;

/// <summary>
/// Diese Tests sind die Bremse vor dem Loeschen. Faellt einer davon aus, kann die Anwendung
/// etwas anbieten, das den Rechner beschaedigt — deshalb stehen hier auch die Faelle, die
/// erlaubt sein muessen: Eine Sperre, die zu viel sperrt, macht das Werkzeug nutzlos.
/// </summary>
public class ProtectedPathsTests
{
    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Windows\System32")]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"C:\Program Files")]
    [InlineData(@"C:\Program Files (x86)\Irgendwas")]
    [InlineData(@"C:\ProgramData")]
    [InlineData(@"C:\System Volume Information")]
    [InlineData(@"C:\$Recycle.Bin")]
    [InlineData(@"C:\Recovery")]
    [InlineData(@"C:\pagefile.sys")]
    [InlineData(@"C:\hiberfil.sys")]
    [InlineData(@"C:\swapfile.sys")]
    [InlineData(@"D:\Windows\explorer.exe")]
    public void Sperrt_was_das_system_braucht(string path)
        => Assert.True(ProtectedPaths.IsProtected(path), $"{path} muss gesperrt sein");

    [Theory]
    [InlineData(@"C:\Users\alex\Downloads")]
    [InlineData(@"C:\Users\alex\Downloads\film.mkv")]
    [InlineData(@"C:\Users\alex\AppData\Local\Temp")]
    [InlineData(@"C:\GIT_Projects\etwas\node_modules")]
    [InlineData(@"D:\Filme")]
    [InlineData(@"C:\Windows Alt")]
    [InlineData(@"C:\Program Files neu\kram")]
    public void Laesst_zu_was_dem_benutzer_gehoert(string path)
        => Assert.False(ProtectedPaths.IsProtected(path), $"{path} darf nicht gesperrt sein");

    [Fact]
    public void Ein_aehnlich_benannter_ordner_ist_nicht_der_systemordner()
    {
        // "C:\Windows Alt" beginnt zwar mit "C:\Windows", ist aber ein anderer Ordner.
        Assert.Null(ProtectedPaths.Reason(@"C:\Windows Alt\datei.txt"));
        Assert.NotNull(ProtectedPaths.Reason(@"C:\Windows\datei.txt"));
    }

    [Fact]
    public void Ein_ordner_namens_windows_woanders_ist_harmlos()
        => Assert.False(ProtectedPaths.IsProtected(@"C:\Users\alex\Downloads\Windows"));

    [Fact]
    public void Das_ganze_benutzerprofil_ist_gesperrt_einzelne_ordner_darin_nicht()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.True(ProtectedPaths.IsProtected(profile));
        Assert.False(ProtectedPaths.IsProtected(Path.Combine(profile, "Downloads")));
    }

    [Fact]
    public void Nennt_einen_grund()
    {
        Assert.Equal(RefusalKind.SystemFolder, ProtectedPaths.Reason(@"C:\Windows\System32")!.Kind);
        Assert.Equal("Windows", ProtectedPaths.Reason(@"C:\Windows\System32")!.Name);
        Assert.Equal(RefusalKind.WholeDrive, ProtectedPaths.Reason(@"C:\")!.Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Leere_eingaben_gelten_als_gesperrt(string path)
        => Assert.True(ProtectedPaths.IsProtected(path));

    [Fact]
    public void Auch_vorfahren_des_profils_sind_gesperrt()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        for (var parent = Directory.GetParent(profile); parent is not null; parent = parent.Parent)
            Assert.True(ProtectedPaths.IsProtected(parent.FullName), parent.FullName);
        Assert.False(ProtectedPaths.IsProtected(Path.Combine(profile, "Downloads")));
    }

    [Theory]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\another-user")]
    [InlineData(@"D:\Users\another-user")]
    public void Auch_fremde_profile_sind_als_ganzes_gesperrt(string path)
        => Assert.True(ProtectedPaths.IsProtected(path));

    /// <summary>
    /// Windows kennt jeden Ordner auch unter seinem 8.3-Kurznamen. "C:\PROGRA~1" ist
    /// "C:\Program Files" — die Sperre muss beide Schreibweisen als denselben Ort erkennen.
    /// </summary>
    [Fact]
    public void Kurznamen_umgehen_die_sperre_nicht()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? shortWindows = ShortName(windows);
        string? shortPrograms = ShortName(programs);

        // Auf Volumes ohne 8.3-Namen gibt es nichts zu pruefen.
        Assert.SkipWhen(shortWindows is null && shortPrograms is null, "keine 8.3-Namen auf diesem Datentraeger");

        Assert.NotNull(shortPrograms);
        Assert.Contains("~", shortPrograms);

        if (shortWindows is not null)
        {
            Assert.NotEqual(windows, shortWindows);
            Assert.True(ProtectedPaths.IsProtected(shortWindows), shortWindows);
            Assert.True(ProtectedPaths.IsProtected(Path.Combine(shortWindows, "System32")), shortWindows);
        }
        if (shortPrograms is not null)
        {
            Refusal? reason = ProtectedPaths.Reason(Path.Combine(shortPrograms, "Irgendwas"));
            Assert.NotNull(reason);
            Assert.Equal(RefusalKind.SystemFolder, reason.Kind);
            Assert.Equal("Program Files", reason.Name);
        }
    }

    [Fact]
    public void Kurznamen_eines_harmlosen_ordners_bleiben_erlaubt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "nur unter Windows");

        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        Assert.SkipUnless(Directory.Exists(downloads), "kein Downloads-Ordner im Profil");

        string candidate = ShortName(downloads) ?? downloads;
        Assert.False(ProtectedPaths.IsProtected(candidate), candidate);
    }

    private static string? ShortName(string path)
    {
        var buffer = new System.Text.StringBuilder(260);
        int length = GetShortPathNameW(path, buffer, buffer.Capacity);
        if (length == 0 || length > buffer.Capacity) return null;
        string result = buffer.ToString();
        return result.Equals(path, StringComparison.OrdinalIgnoreCase) ? null : result;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern int GetShortPathNameW(string path, System.Text.StringBuilder buffer, int size);
}
