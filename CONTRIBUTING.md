# Contributing to DiskStats

DiskStats is a Windows app built with .NET 10 and Avalonia. Contributions are
accepted under the [MIT License](LICENSE). Only submit code and content that
you have the right to contribute; third-party components require their own
license notices.

## Development and verification

Install the .NET 10 SDK on Windows and run these commands from the repository:

```powershell
dotnet build -c Release
dotnet test -c Release --no-build
dotnet run --project src/DiskStats.App -c Release
```

In a pull request, describe the problem, your changes, and the checks you ran.
Add appropriate tests for behavior changes. English and German UI strings are
stored in `src/DiskStats.App/Localization/Loc.cs`.

## Conventions

- **Comments:** write new and changed comments in English. Much of the existing code is
  commented in German; translate a block when you touch it, but do not rewrite files just for
  that.
- **User-facing text** always goes through `Localization/Loc.cs` with both languages. Core code
  reports *facts* (`Refusal`, `FileMoveException`) and the UI turns them into text. Never show
  `ex.Message` of one of our own exceptions or an enum name to the user.
- **Win32 calls** that more than one place needs live in `Core/Interop/NativeMethods.cs`;
  `SHFileOperation` exists only in `Core/Interop/ShellFileOperation.cs`. Do not add another copy
  of a declaration.
- **What may run at the same time** is decided in `App/OperationGate.cs`, not by new bool fields
  in `MainWindow`. Add a case to its tests when you add an operation.
- **Snapshots:** a file from a newer app version must never be deleted by an older one; only a
  file that is actually damaged may be discarded (see `SnapshotVersionException`).

Test scanning and deletion with purpose-made sample data. Some Windows features
depend on the file system and permissions; document which code paths you
actually verified.

A separate tool is available for local measurements:

```powershell
dotnet run --project src/DiskStats.Bench -c Release -- C:\DiskStats-Demo 4 --store
```

## Creating your own distributions

```powershell
dotnet publish src/DiskStats.App -c Release -r win-x64 --self-contained true -o publish
```

`LICENSE` and `THIRD-PARTY-NOTICES.md` are included automatically and must be
preserved when redistributing the app.

MSIX packaging also requires the Windows SDK. The checked-in manifest contains
the public package identity of the official RINGPAPERS app. For your own Store
submissions, use the identity of your Partner Center product. For your own
signed packages, use a matching publisher and your own certificate. `-Store`
validates the manifest and version; it does not upload anything.

`packaging/build.ps1 -Register` replaces an installed app with the same package
identity with a local developer registration. Use your own identity for
side-by-side testing or launch the app with `dotnet run`.

Build outputs, certificates, keys, and local working notes do not belong in
Git. Store screenshots should contain only synthetic data.

## Reporting bugs

Include the Windows and app versions, reproduction steps, and the expected and
actual behavior. Remove private filenames and paths from logs and screenshots.
Report security-related issues privately; see [SECURITY.md](SECURITY.md).

## Checks

There is no hosted CI; run the checks yourself before you send a change:

```powershell
dotnet build -c Release
dotnet test -c Release --no-build
dotnet list package --vulnerable --include-transitive
```

One test needs a real NTFS volume and is skipped by default: it attaches a virtual disk, fills it
with hardlinks, compressed and sparse files, a junction and more, and compares the MFT scan with
the directory scan. Run it in an elevated PowerShell when you touch `NtfsScanner` or
`NtfsRecordParser`:

```powershell
$env:DISKSTATS_VHD_TESTS = "1"
dotnet test tests/DiskStats.Core.Tests -c Release --filter "FullyQualifiedName~NtfsDifferentialTests"
```

Dependabot proposes NuGet updates weekly (`.github/dependabot.yml`); the Avalonia packages are
grouped because they must move together.
