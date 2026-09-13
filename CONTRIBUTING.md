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
Report security-related issues privately to kroxoko@gmail.com.
