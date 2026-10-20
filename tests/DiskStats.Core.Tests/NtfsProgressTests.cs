using DiskStats.Core.Scanning;

namespace DiskStats.Core.Tests;

/// <summary>
/// Das Lesen der MFT liefert erst am Ende einen Baum. Ohne Fortschrittsmeldung bliebe die
/// Oberflaeche bei einem Laufwerksscan minutenlang stumm.
/// </summary>
public sealed class NtfsProgressTests
{
    [Fact]
    public void Meldet_hoechstens_einmal_je_prozent_und_nie_rueckwaerts()
    {
        var seen = new List<double>();
        var progress = new ScanProgress(seen.Add);

        // Zehntausende feine Schritte, wie in der Leseschleife.
        for (int i = 0; i <= 50_000; i++) progress.Report(i / 50_000.0);

        Assert.True(seen.Count <= 101, $"{seen.Count} Meldungen");
        Assert.Equal(1.0, seen[^1]);
        for (int i = 1; i < seen.Count; i++) Assert.True(seen[i] > seen[i - 1]);
    }

    [Fact]
    public void Ein_ruecksprung_wird_verschluckt()
    {
        var seen = new List<double>();
        var progress = new ScanProgress(seen.Add);

        progress.Report(0.5);
        progress.Report(0.2);

        Assert.Equal([0.5], seen);
    }

    [Theory]
    [InlineData(-3.0, 0.0)]
    [InlineData(7.0, 1.0)]
    public void Begrenzt_werte_ausserhalb_von_null_bis_eins(double input, double expected)
    {
        var seen = new List<double>();
        new ScanProgress(seen.Add).Report(input);

        Assert.Equal([expected], seen);
    }

    [Fact]
    public void Nan_wird_wie_null_behandelt_und_ohne_empfaenger_passiert_nichts()
    {
        var seen = new List<double>();
        new ScanProgress(seen.Add).Report(double.NaN);
        Assert.Equal([0.0], seen);

        // Ohne Empfaenger ist Melden ein No-op und wirft nicht.
        new ScanProgress(null).Report(0.5);
    }

    [Fact]
    public void Der_baumaufbau_meldet_den_rest_des_balkens()
    {
        NtfsFileRecord[] records =
        [
            new(16, true, FileAttributes.Directory, 0, 0, 0, 1, [new(5, "a")], [], false),
            new(17, true, FileAttributes.Directory, 0, 0, 0, 1, [new(16, "b")], [], false),
            new(18, false, FileAttributes.Normal, 10, 4096, 0, 1, [new(17, "c.bin")], [], false),
        ];
        var seen = new List<double>();

        NtfsScanner.BuildTree(records, 5, 1, @"C:\fixture", new WalkOptions(), default,
            errors: null, progress: new ScanProgress(seen.Add));

        Assert.NotEmpty(seen);
        // Das Lesen belegt die ersten 80 Prozent; der Aufbau meldet nur den Rest.
        Assert.All(seen, v => Assert.InRange(v, 0.8, 1.0));
    }

    [Fact]
    public void Datenlaeufe_werden_auf_wunsch_nicht_behalten_aber_weiter_geprueft()
    {
        byte[] raw = NtfsScannerTests.Record(nonresident: true);

        NtfsFileRecord kept = Assert.IsType<NtfsFileRecord>(NtfsRecordParser.Parse(raw, 42, 512));
        NtfsFileRecord slim = Assert.IsType<NtfsFileRecord>(NtfsRecordParser.Parse(raw, 42, 512, keepDataRuns: false));

        Assert.Single(kept.DataRuns);
        Assert.Empty(slim.DataRuns);
        // Alles andere bleibt gleich.
        Assert.Equal(kept.Size, slim.Size);
        Assert.Equal(kept.Allocation, slim.Allocation);
        Assert.Equal(kept.Names, slim.Names);
    }

    [Fact]
    public void Ein_kaputter_datenlauf_beschaedigt_den_record_auch_ohne_behalten()
    {
        byte[] raw = NtfsScannerTests.Record(nonresident: true);
        // Kopfbyte 0x11 = je ein Byte Laenge und Versatz; 0x19 verspricht neun Laengenbytes — mehr als moeglich.
        int run = raw.AsSpan().IndexOf(new byte[] { 0x11, 3, 10, 0 });
        Assert.True(run > 0);
        raw[run] = 0x19;

        Assert.Throws<InvalidDataException>(() => NtfsRecordParser.Parse(raw, 42, 512, keepDataRuns: false));
    }

    // Das Fixture-Attribut bietet acht Byte Platz; groessere Werte (100 -> 104) prueft der
    // Differenztest auf einem echten NTFS-Laufwerk.
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 8)]
    [InlineData(4, 8)]
    [InlineData(5, 8)]
    [InlineData(8, 8)]
    public void Resident_gespeicherte_daten_werden_wie_bei_windows_auf_acht_byte_aufgerundet(int bytes, long expected)
    {
        byte[] raw = NtfsScannerTests.Record(nonresident: false);
        // Wertlaenge des unbenannten $DATA-Attributs: das Attribut steht nach $STANDARD_INFORMATION
        // (72 Byte ab 56) und $FILE_NAME; "data" (4 Byte) ist sein Inhalt.
        int attribute = raw.AsSpan().IndexOf("data"u8) - 24;
        Assert.Equal(0x80u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(attribute)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(attribute + 16), (uint)bytes);

        NtfsFileRecord record = Assert.IsType<NtfsFileRecord>(NtfsRecordParser.Parse(raw, 42, 512));

        Assert.Equal(bytes, record.Size);
        Assert.Equal(expected, record.Allocation);
    }
}
