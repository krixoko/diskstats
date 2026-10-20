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

`App/OperationGate` is the single place that says which operations exclude which: scanning,
refreshing a folder, changing files (clean-up and move share one state) and the open benchmark.
Windows decide with named predicates (`CanBrowse`, `CanChangeFiles`, ...) instead of combining
bool fields; its tests pin the compatibility table.

`Core/Interop` holds the Win32 declarations that more than one class needs (`NativeMethods`) and
the one `SHFileOperation` wrapper used by both the Recycle Bin and move. Declarations that belong
to one class and its structures stay with it.

The NTFS scan reports progress through `WalkOptions.MftProgress` (reading the MFT, then building the
tree), throttled to whole percents by `ScanProgress`; the directory scan shows its tree while
it grows and needs no such callback.

Snapshots are a local cache with a version number. A file from a *newer* version raises
`SnapshotVersionException` and is left in place; only unreadable or implausible files are
deleted. This keeps a downgrade or a side-by-side older build from erasing the history.

## Known limitations

- **MFT scan memory:** every in-use MFT record is held as an object until the tree is built
  (data runs are dropped except for `$MFT` itself). On volumes with tens of millions of files this
  needs gigabytes. A struct-of-arrays layout or a streaming build would fix it.
- **Drive health** shells out to Windows PowerShell and, for NVMe, compiles a small C# helper with
  `Add-Type` on every query. That is slow, depends on the language mode of the machine, and looks
  like a malware pattern to some scanners. The clean fix is to query WMI and the NVMe IOCTL in
  process; it needs real hardware to verify.
- **Allocation of sparse files** differs between the scan methods: for an uncompressed sparse file
  (10 MiB, 4 KiB written) the directory scan reports 0 bytes and the MFT scan 65,536. NTFS allocates
  sparse files in 64 KiB units, so the MFT value looks right and the directory scan's
  `FileCompressionInfo` value looks like an API quirk, but this is not settled. The opt-in
  `NtfsDifferentialTests` names it as a known deviation; every other measured case agrees.
- **Recycle Bin** uses `SHFileOperation`, which is bound to classic path lengths. When it fails for
  a path of 260+ characters the status line says so; `IFileOperation` would remove the limit.
- **`MainWindow`** is still a large code-behind split over partial classes. `OperationGate` removed
  the scattered state flags, but a view-model layer is the next step if more features land.

`tests/DiskStats.Core.Tests` tests the data model and file system functions.
`tests/DiskStats.App.Tests` tests the UI with Avalonia Headless and covers app
logic. Real MFT access with administrator privileges needs additional practical
verification; a successful fallback test does not cover that path.

`packaging/build.ps1` creates a self-contained Windows x64 distribution in a
temporary directory and packages it as MSIX. The app project includes the MIT
License and third-party notices in the distribution.
