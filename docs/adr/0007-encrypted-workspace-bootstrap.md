# Encrypted workspace bootstrap policy

- Status: Accepted
- Date: 2026-09-26

## Context

The encrypted-store decision requires consistent Unicode normalisation, explicitly pinned sqleet parameters, and a first user-named Category, but it does not name the normalisation form, parameter values, workspace location, or initial Category label rules. Those values affect whether a passphrase can reopen a workspace and therefore must not remain mutable implementation defaults.

Schema migration is not part of the first encrypted-workspace slice. A migration requires a validated recovery point, while backup and restore are deliberately deferred to a later slice.

## Decision

Normalise passphrases with Unicode Normalisation Form C before validation, confirmation comparison, and use as key material. Do not trim, case-fold, or compatibility-normalise passphrases. Count user-perceived text elements after normalisation and require at least 12. Recommend 16 or more characters or a multi-word passphrase without imposing composition rules.

Configure every encrypted connection before applying its key with these explicit SQLite3MC parameters:

- `cipher=chacha20`
- `legacy=0`
- `kdf_iter=64007`
- `plaintext_header_size=0`
- `hmac_check=1`

Use `temp_store=MEMORY`, disable connection pooling, and never create a plaintext working copy. The values match the current non-legacy sqleet format while making the portable database format independent of future library defaults.

The first release keeps exactly one live unlocked workspace session. `<LocalApplicationData>/dot-orbit/workspace.orb` remains the default, while the access window can choose an existing local file or a destination for a new workspace. After successful creation or validated unlock, the canonical absolute path is remembered as a device-local preference; a failed attempt never replaces it. A missing or unavailable remembered file is never recreated implicitly and instead offers retry, another choice, or the default location. Switching first drains and disposes the prior session. The extension is a discoverability convention, not a trust boundary: every existing selection validates encrypted SQLite3MC content, schema, and integrity regardless of its name.

Installations that still have the exact earlier `workspace.db` name adopt it only after a successful authenticated open has validated and, when required, migrated the store. While retaining SQLite's exclusive store lock, adoption publishes a flushed, versioned same-directory marker containing the exact encrypted file-set manifest and fingerprints, then creates non-overwriting hard links from the validated main database and its present `-journal`, `-wal`, `-shm`, and `.recovery-state.json` companions to their new names. The legacy names are removed only after every new name is verified against that manifest; on POSIX database-name removal remains inside the exclusive-open boundary, while Windows sharing rules make a racing open fail cleanup safely for a later retry. Because recovery state is replaced independently of SQLite, its legacy name is first atomically quarantined and fingerprint-checked; an unexpected replacement is preserved and blocks completion, while successful cleanup leaves a directory at the exact old sidecar path so an already-open old process cannot recreate it. A valid marker makes every checkpoint resumable without copying or re-encrypting content. Both main names without a matching marker, an invalid marker, or old/new companion combinations that cannot be attributed unambiguously fail closed without replacing either workspace. The user receives non-sensitive instructions to preserve the files and resolve the conflict. Portable `.dotorbit-recovery` files are not renamed.

Trim outer whitespace from the first Category label, reject a blank result, and preserve the remaining spelling and case. Workspace creation writes schema version 1 and the first Category as one transaction to a same-directory candidate, validates it, then atomically publishes it without replacing an existing workspace.

Schema version 1 recognises only version 1. It refuses a newer schema through a read-only validation path without modifying the file. Forward migration begins only after encrypted recovery points are implemented.

## Consequences

- Canonically equivalent passphrase input reopens the same workspace; compatibility-distinct characters remain distinct.
- Changing any pinned cipher or KDF value is a storage-format change requiring an explicit migration and portability decision.
- Losing the passphrase remains unrecoverable. The application retains no verifier, hint, recovery secret, or credential-store copy.
- First-run setup cannot publish a workspace unless its first Category is valid and durably present.
- Wrong passphrases, damage, and tampering share one public failure outcome. SQLite exception details and passphrases do not cross the storage boundary.
- A legacy filename is never treated as proof of a workspace: wrong-passphrase, damaged, unsupported-schema, and failed-migration opens do not begin filename adoption.
- The location preference is a single last-successful path, not a workspace registry. Moving, copying, renaming, multi-open tabs, and simultaneous workspace sessions remain outside this slice.
