# DiskStats user guide

## Quick start

1. Select a drive or folder and start a scan.
2. Explore large areas in the treemap or open the **Files** view.
3. Sort by size or allocated space and narrow the results with **Filters**.
4. Review the results and add the items you want to remove to the clean-up list.
5. Check the clean-up list before confirming deletion.

Use **Refresh folder** to rescan part of the tree.
**Watch changes** updates affected folders automatically.

## Features

| Feature | What it does |
|---|---|
| Nine views | Treemap, Folders, Files, Sunburst, Flame, Bubbles, Mind Map, Top Sizes, and Age Map. |
| Complete file table | Scrolling, multiple selection, and sorting by name, size, allocated space, modification date, file count, hardlinks, or path. |
| Actual disk usage | Distinguishes logical file size from allocated space and accounts for compressed files, sparse files, and hardlinks. |
| Filters and exclusions | Narrows results or skips unwanted directories while scanning. |
| Duplicate detection | Finds identical files by comparing sizes, checking samples, and comparing the full contents. |
| Scan comparison | Shows what has grown, shrunk, appeared, or disappeared since an earlier scan. |
| Quick Wins | Finds potential space consumers such as caches and build folders for manual review. |
| Refresh and monitoring | Rescans individual subtrees and reacts to file system changes. |
| NTFS/MFT scanning | Reads NTFS metadata directly and falls back to directory scanning when needed. |
| Clean-up list and export | Lets you review deletions together, open results in Explorer, and export data as CSV. |
| Disk speed | Measures sequential and random reads/writes, IOPS, and average access time. |
| Drive health | Reads Windows health status and supported SMART metrics, including temperature, endurance used, power-on hours, and error counters. |

## Finding and filtering files

In **Files**, click a column header to sort. Double-click to open a folder;
**Include subfolders** also shows its descendants. You can add multiple selected
rows to the clean-up list together.

**Filters** applies to the file table. Filter names and relative paths with glob
patterns or regular expressions, set size limits in bytes, or filter by days
since the last change. Empty fields leave the results unrestricted.

**Scan exclusions** are saved and applied while reading the file system:

- `node_modules` excludes entries with that name at any depth.
- `*.log` excludes files with that extension.
- `archive/**` excludes content under the relative `archive` folder.

Changing exclusions starts a new scan. You can also exclude an item through
the context menu. Exclusions apply to refreshes and monitoring as well.

## Previewing files

Select a file and press **Space**, or choose **Preview** in the right sidebar.
Images fit inside the preview window. Text and source files are displayed as plain
text (HTML is not executed), with a limit of 128 Ki characters. MP4 and other listed
video formats play inside the app through Windows Media Foundation, with play,
pause, and restart controls. Video support depends on the installed system codecs.
Press **Escape** or **Space** to close the preview. Unsupported formats show a message.

## Moving files

Use **Ctrl-click** to select files or folders, then choose **Move to…** in the right
sidebar. Select a destination folder, including a folder on another drive. DiskStats
checks current source sizes and available space, then lists the paths for confirmation.
Parent folders cover any selected children so entries are not moved twice.

Windows handles the actual move, with progress, cancellation, and prompts for name
conflicts. Nothing is silently overwritten. After an attempted move, DiskStats rescans
the source, including after cancellation or partial failure. Already moved entries
stay at the destination if you cancel. Protected system paths, the scan root, and
linked/cloud-placeholder entries are not accepted for moving. The capacity check
conservatively requires room for the logical size of all selected files.

## Storage history

Select a folder and choose **History** in the right sidebar. For a file, history shows
its parent folder. Choose 7, 30, or 90 days, or all retained scans. The chart, dated
measurements, total change, and changed-child list describe that folder. Click a
changed entry to return to it in the scan if it still exists.

History uses up to six retained snapshots per scan root, under the existing 12-month
and global storage limits. It does not schedule background scans. At least two saved
scans are needed. The closest available scan before the period may serve as the
baseline; the exact comparison dates are always shown. A folder absent from a scan
is a gap, not a measured zero. Results reflect the scan's exclusions and readability.

## Saving filters

In **Filter**, configure the query, enter a filter name, and choose **Save filter**.
Saved filters appear in the left sidebar for one-click reuse on the current folder
and its descendants. They persist across restarts. The selector in the Filter dialog
loads a saved query for editing; saving an existing name asks before replacement.
**Remove saved filter** removes the preset only. Up to 30 presets can be stored.

Presets contain name/path patterns, regex mode, size limits, and age. Scan exclusions
remain separate and are never changed by applying a preset.

## File size and allocated space

The header, file table, and detail view show allocated bytes where Windows
provides that metadata. Unknown allocation is marked accordingly. Charts
continue to show **logical file sizes**.

Hardlinks share the same file data. DiskStats counts that allocation once and
assigns it to the lexicographically first measured path. Deleting one hardlink
does not free that space while other links to the same file remain. The displayed
allocation therefore does not guarantee how much space a deletion will recover.

## Refreshing and scan methods

**Refresh folder** rescans the current folder and its descendants. In the context
menu, it applies to the selected folder or the parent of a selected file. Other
subtrees remain in memory.

**Watch changes** reacts to creation, deletion, modification, and renaming.
Events are batched. If the event buffer overflows, the entire scan is reconciled.
Turning monitoring off, switching scans, or closing the app ends the current
monitoring session.

Settings offers three scan methods:

| Method | Behavior |
|---|---|
| Automatic | Attempts direct MFT access for entire local NTFS drives; uses directory scanning for individual folders. |
| Directory scan | Reads files and folders through regular file system APIs. |
| NTFS / MFT | Attempts read-only access to NTFS metadata, including for selected folders. |

Direct MFT access requires administrator privileges. If permissions are missing,
the file system is unsupported, or NTFS structures cannot be fully interpreted,
DiskStats falls back to directory scanning. The status bar shows the method used
and, where applicable, the reason for the fallback.

**Current verification status:** The parser and directory-scan fallback have been
tested. A real direct MFT scan with administrator privileges and a performance
comparison are still pending.

## Measuring disk speed

The **speedometer button in the upper-right corner** opens the disk speed test.
Select a local drive or USB storage device and click **Start test**. Results show:

- Sequential reads and writes in **MB/s**, using 1 MiB blocks.
- Random 4 KiB reads and writes in **MB/s**, **IOPS**, and **ms** of average
  access time.
- Random 64 KiB reads and writes for larger data blocks.
- A mixed 4 KiB test with **70% reads and 30% writes**. MB/s and IOPS report
  the combined throughput of both operation types.

The test uses a fully written **1 GiB file (1,024 MiB)** on the selected drive.
It requires at least 1.25 GiB of free space. Each of the seven measurement phases
runs for roughly up to three seconds or stops at its data limit; preparation
takes additional time. Each run writes less than 4.2 GiB. The mixed test finishes
each group of seven reads and three writes to preserve the exact ratio.
**Cancel** or closing the window stops the test after the current request and
removes the test file. Existing files are not overwritten. Missing write
permissions or a disconnected drive produce an error message.

The test uses one request at a time (**Q1T1**), bypasses the Windows file cache,
and uses write-through for writes. The device cache may still affect results.
These values describe a short file-based test; they are not directly comparable
to other CrystalDiskMark profiles or sustained writes after an SSD cache is
exhausted. Other applications and drive activity affect the results. DiskStats
pauses its own automatic refreshes while the window is open and only opens the
test once active scans and file operations have finished.

## Drive health

The **heart/pulse button in the upper-right corner** opens drive health. Select a
physical disk to see the status reported by Windows and the available temperature,
endurance used, power-on hours, and read/write error counters. Supported NVMe disks
also provide critical SMART warnings, media/data integrity errors, and unsafe
shutdown counts directly from their health log, often without administrator rights.
NVMe warnings are shown separately from the Windows status.

This is a read-only query: it does not write a test file, run device self-tests, or
request elevation. **Refresh** reads the latest values. Closing the window cancels
the query; a provider timeout limits each query to 25 seconds.

Availability depends on the device, driver, USB adapter, and permissions. Missing
values are marked **Not available**. The endurance value is the manufacturer's
reported percentage used, not a calculated health score; NVMe values can exceed
100%. A healthy status does not guarantee that a disk will not fail. An unsafe
shutdown count alone does not prove data loss or a defective drive.

## Controlled clean-up

Quick Wins are suggestions for review: a folder name alone cannot tell you
whether its contents are disposable. DiskStats does not delete these results
automatically.

Selected items appear in a clean-up list. The confirmation shows the item count
and data size. System folders and the scan root cannot be added to the list.

Deletion uses the Windows shell and requests the Recycle Bin. If Windows cannot
move an item there, for example because of its size or drive, an additional
permanent-deletion warning may appear. Review that prompt before continuing.

## Controls

The following shortcuts navigate chart views. Input fields and the file table
use the controls appropriate to those elements.

| Key | Action |
|---|---|
| Arrow keys | Move the selection between siblings and hierarchy levels |
| Enter | Open the selected folder |
| Backspace | Go up one level |
| Home | Return to the scan root |
| Delete | Add the selection to the clean-up list |
| Ctrl+F | Open search |
| F5 | Rescan |
| Esc | Close search or clear the selection |

The context menu includes **Show in Explorer**, **Copy path**, CSV export,
refreshing, and exclusions. Change the language and appearance in Settings.
A language change takes effect on the next launch.

## Privacy and local data

DiskStats processes scan data locally and does not send it to a server.
Settings, snapshots, and the error log are stored under
`%LOCALAPPDATA%\DiskStats`, or in the package container for the Store version.

- **Snapshots:** Up to 400 MB in total, six per folder, and twelve months old.
- **Error log:** Up to 4 MB and twelve months old.

Settings shows the space used and lets you remove stored data.

## Requirements and permissions

The distributed app targets **Windows x64**. The self-contained package includes
the required .NET runtime, so no separate runtime installation is needed.

A regular scan does not require administrator privileges. Inaccessible areas
may be missing; the status bar reports access issues. The unpackaged app can
offer to restart with administrator privileges at launch. You can disable this
option; it is not available in the packaged Store version.


