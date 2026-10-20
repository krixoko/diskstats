# Security policy

## Supported versions

Security fixes go into the latest released version. Please update before reporting.

## Reporting a vulnerability

Please report security issues **privately** and do not open a public issue.

- Use GitHub's private vulnerability reporting (Security tab → *Report a vulnerability*) if it
  is available on this repository, or
- email **kroxoko@gmail.com** with the affected version, steps to reproduce, and the impact you
  see.

Remove personal file names and paths from anything you attach. You can expect an answer within a
few days; please allow time for a fix before disclosing details publicly.

## What counts

DiskStats reads file systems and can move files to the Recycle Bin or to another folder after
explicit confirmation. Reports are especially welcome for:

- a way to get a protected system path onto the clean-up list or into a move,
- deleting or moving something that was not listed in the confirmation,
- reading or writing outside the folders the user selected,
- crashes or hangs caused by crafted snapshot, settings, or log files in the app data folder.
