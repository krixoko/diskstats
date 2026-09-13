<#
    Schnuert das MSIX-Paket.

    Warum die Fassung eigenstaendig ist: Der Store liefert keine .NET-Laufzeit mit, und eine
    Abhaengigkeit davon waere ein zweiter Installationsschritt fuer den Nutzer. Der Preis sind
    rund 200 MB Paketgroesse -- vertretbar, weil der Store nur Unterschiede uebertraegt.

    Ohne Argumente entsteht nur das Paket. Mit -Register wird es im Entwicklermodus lose
    registriert, was zum Ausprobieren genuegt und keine Signatur braucht.
#>
param(
    [switch]$Register,
    # Fuer die Einreichung: prueft, dass keine Platzhalter mehr im Manifest stehen und die
    # Version des Pakets mit der der Anwendung uebereinstimmt.
    [switch]$Store,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$repo = Split-Path $root -Parent

# Bringt eine Version auf vier Stellen, damit "1.0.0" (Directory.Build.props) und "1.0.0.0"
# (AppxManifest) vergleichbar werden. Ein Vorabkennzeichen wie "-preview.1" oder "+build"
# gehoert zur Anwendungsversion, nicht zur Paketversion, und wird deshalb abgeschnitten.
function ConvertTo-Vierstellig([string]$Version) {
    $kern = ($Version.Trim() -split '[-+]', 2)[0]
    $teile = @($kern -split '\.')
    if ($teile.Count -lt 1 -or $teile.Count -gt 4 -or ($teile | Where-Object { $_ -notmatch '^\d+$' })) {
        throw "Version '$Version' ist keine Zahlenfolge der Form a.b.c[.d]."
    }
    while ($teile.Count -lt 4) { $teile += "0" }
    return ($teile | ForEach-Object { [int]$_ }) -join "."
}

# Liest <Identity Version="..."> aus dem Paketmanifest.
function Get-PaketVersion([string]$ManifestPfad) {
    $wert = ([xml](Get-Content $ManifestPfad -Raw)).Package.Identity.Version
    if ([string]::IsNullOrWhiteSpace($wert)) { throw "Keine Identity-Version in $ManifestPfad." }
    return $wert.Trim()
}

# Liest <Version> aus Directory.Build.props. Nur die Version zaehlt, nicht AssemblyVersion
# oder FileVersion: Sie ist laut Kommentar dort die eine gepflegte Stelle.
function Get-AnwendungsVersion([string]$PropsPfad) {
    # XPath statt Punktnotation: Bei mehreren PropertyGroups liefert die Punktnotation ein
    # Array mit Leerstellen, das als " 1.0.0" im Vergleich landet.
    $knoten = ([xml](Get-Content $PropsPfad -Raw)).SelectSingleNode("/Project/PropertyGroup/Version")
    if ($null -eq $knoten -or [string]::IsNullOrWhiteSpace($knoten.InnerText)) {
        throw "Kein <Version>-Element in $PropsPfad."
    }
    return $knoten.InnerText.Trim()
}

# Der Store lehnt ein Paket nicht ab, dessen Manifestversion von der Exe abweicht -- der
# Nutzer sieht dann aber im Store eine andere Nummer als in den Dateieigenschaften, und ein
# Fehlerbericht laesst sich keinem Stand mehr zuordnen. Deshalb hier, nicht spaeter.
function Assert-VersionenGleich([string]$ManifestPfad, [string]$PropsPfad) {
    $paket = Get-PaketVersion $ManifestPfad
    $anwendung = Get-AnwendungsVersion $PropsPfad
    if ((ConvertTo-Vierstellig $paket) -ne (ConvertTo-Vierstellig $anwendung)) {
        throw "Versionen weichen ab: AppxManifest.xml hat '$paket', Directory.Build.props hat '$anwendung'. Beide auf denselben Stand bringen."
    }
}

# Ein Paket mit Platzhalter-Identitaet laesst sich bauen und lokal ausprobieren, aber niemals
# einreichen. Der Fehler faellt sonst erst bei der Zertifizierung auf, Tage spaeter.
if ($Store) {
    $manifest = ([xml](Get-Content "$root\AppxManifest.xml" -Raw)).Package
    foreach ($value in @($manifest.Identity.Name, $manifest.Identity.Publisher, $manifest.Properties.PublisherDisplayName)) {
        if ([string]::IsNullOrWhiteSpace($value) -or $value -match 'PLATZHALTER') {
            throw "Identitaet im Manifest fehlt oder ist noch ein Platzhalter. Werte aus dem Partner Center eintragen."
        }
    }
    Assert-VersionenGleich "$root\AppxManifest.xml" "$repo\Directory.Build.props"
}

# Jeder Build bekommt einen leeren Paketordner. Alte Ausgabedateien werden nie mitgeliefert.
$stage = Join-Path ([IO.Path]::GetTempPath()) ("DiskStats-package-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
Copy-Item -LiteralPath "$root\AppxManifest.xml" -Destination $stage
Copy-Item -LiteralPath "$root\Assets" -Destination $stage -Recurse

Write-Host "Veroeffentliche ..." -ForegroundColor Cyan
dotnet publish "$repo\src\DiskStats.App" -c $Configuration -r win-x64 --self-contained true `
    -o "$stage\app" --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen" }

# makeappx liegt je nach installiertem SDK in einem anderen Unterverzeichnis
$kit = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Directory |
    Where-Object { Test-Path "$($_.FullName)\x64\makeappx.exe" } |
    Sort-Object Name -Descending | Select-Object -First 1
if (-not $kit) { throw "makeappx.exe nicht gefunden - Windows SDK fehlt" }

$msix = "$root\DiskStats.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }

Write-Host "Schnuere Paket ..." -ForegroundColor Cyan
& "$($kit.FullName)\x64\makeappx.exe" pack /d $stage /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx fehlgeschlagen" }

Write-Host "Fertig: $msix" -ForegroundColor Green

if ($Register) {
    # Den Namen aus dem Manifest lesen statt ihn zu wiederholen: Beim Wechsel der Identitaet
    # lief die Suche sonst ins Leere und meldete einen leeren Startbefehl.
    $name = ([xml](Get-Content "$root\AppxManifest.xml")).Package.Identity.Name

    Get-AppxPackage -Name $name -ErrorAction SilentlyContinue | Remove-AppxPackage
    # Die lose Registrierung braucht ihren Paketordner auch nach Skriptende.
    Add-AppxPackage -Register "$stage\AppxManifest.xml"

    $p = Get-AppxPackage -Name $name
    Write-Host "Registriert. Start: shell:AppsFolder\$($p.PackageFamilyName)!DiskStats" -ForegroundColor Green
}

if (-not $Register) {
    $resolvedStage = [IO.Path]::GetFullPath($stage)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedStage.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path $resolvedStage -Leaf) -notmatch '^DiskStats-package-[a-f0-9]{32}$') {
        throw "Unerwarteter temporaerer Paketpfad: $resolvedStage"
    }
    Remove-Item -LiteralPath $resolvedStage -Recurse -Force
}
