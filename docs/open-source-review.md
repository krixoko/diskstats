# Public source review

Review date: September 13, 2026.

## Fresh public history

The public repository starts with a single root commit of the reviewed 1.1.0
source. Earlier development commits, branches, pull requests, and workflow logs
are not part of this repository. Commit metadata uses the maintainer's GitHub
no-reply address.

The previous development repository and local history backup are private.
Historical plans and machine-specific working notes are excluded from the public
source snapshot. Existing copies made before the reset cannot be recalled.

## Included content

- Application source, tests, build workflow, and packaging scripts.
- MIT License and required third-party license notices.
- English documentation and screenshots using synthetic demo files.
- Public product identity, Store link, and the intended support contact.

Build outputs, local archives, configuration secrets, signing keys, and MSIX
artifacts are excluded through `.gitignore`.

## Verification

The public source snapshot was checked for the maintainer's private email and
Windows username, private keys, common access-token formats, credential
assignments, and local user paths. Generic test fixtures and required third-party
copyright contacts remain. A pattern scan is not a proof that every possible
form of private data is absent.

The 1.1.0 Release suite passed 1,335 tests. The application source and screenshots
are unchanged by the history reset. A settings persistence test now runs on the
Avalonia UI thread because settings changes notify UI subscribers.
App tests run sequentially to isolate their shared application settings.
The Windows build workflow also runs against the new public root commit.
