# Architecture

`DiskStats.Core` contains file system logic without depending on the UI.
`ScanCoordinator` chooses between directory scanning and NTFS/MFT access with
a directory-scan fallback. `NodeStore` holds scan results in a compact data
model used by analysis, search, export, and snapshots.

`DiskStats.App` connects the model to the Avalonia interface. The `MainWindow.*`
files separate scanning, navigation, search, analysis, refreshing, and clean-up.
`Rendering` contains charts and layout algorithms; `Controls` includes the file
table. `Localization/Loc.cs` contains both languages.

Clean-up operations use an explicitly confirmed list. `ProtectedPaths` protects
system and profile roots. `RecycleBin` uses the Windows shell. All views must
account for removed entries and outdated scan sessions; changes to this behavior
require regression tests.

`DiskStats.Bench` measures directory scanning and, optionally, NodeStore
construction, aggregation, and snapshot processing. Results depend on the drive,
data, and file system cache and do not guarantee scan performance.

Separately, `Core/Benchmarking` measures drive speed using a freshly created
test file. `WindowsBenchmarkFile` uses aligned, unbuffered Win32 I/O with
write-through, exclusive `CREATE_NEW`, and `DELETE_ON_CLOSE`. `DiskBenchmark`
limits time and write volume, serializes tests, and removes its temporary
directory without recursive deletion. `BenchmarkWindow` displays progress and
results, prevents concurrent starts, and waits for cancellation and clean-up
before closing.

`Core/Health/DiskHealthReader` queries physical disks and reliability counters
through Windows PowerShell using a fixed script. For NVMe devices it also reads
the SMART health log with `IOCTL_STORAGE_QUERY_PROPERTY` through a handle with
zero requested access, checking the bus and serial before associating the result
with a CIM device. No disk writes, self-tests, or elevation are requested. Native
queries run inside the same hidden, cancellable process with a 25-second timeout.
Unsupported counters stay nullable; Windows health status and NVMe critical
warnings remain distinct. `HealthWindow` preserves disk selection on refresh,
clears stale readings on errors, and cancels pending queries when closed.

`tests/DiskStats.Core.Tests` tests the data model and file system functions.
`tests/DiskStats.App.Tests` tests the UI with Avalonia Headless and covers app
logic. Real MFT access with administrator privileges needs additional practical
verification; a successful fallback test does not cover that path.

`packaging/build.ps1` creates a self-contained Windows x64 distribution in a
temporary directory and packages it as MSIX. The app project includes the MIT
License and third-party notices in the distribution.
