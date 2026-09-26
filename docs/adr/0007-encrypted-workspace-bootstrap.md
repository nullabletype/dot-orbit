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

The first release uses one workspace at `<LocalApplicationData>/dot-orbit/workspace.db`. The path is derived at startup and is not a workspace picker or multi-workspace registry.

Trim outer whitespace from the first Category label, reject a blank result, and preserve the remaining spelling and case. Workspace creation writes schema version 1 and the first Category as one transaction to a same-directory candidate, validates it, then atomically publishes it without replacing an existing workspace.

Schema version 1 recognises only version 1. It refuses a newer schema through a read-only validation path without modifying the file. Forward migration begins only after encrypted recovery points are implemented.

## Consequences

- Canonically equivalent passphrase input reopens the same workspace; compatibility-distinct characters remain distinct.
- Changing any pinned cipher or KDF value is a storage-format change requiring an explicit migration and portability decision.
- Losing the passphrase remains unrecoverable. The application retains no verifier, hint, recovery secret, or credential-store copy.
- First-run setup cannot publish a workspace unless its first Category is valid and durably present.
- Wrong passphrases, damage, and tampering share one public failure outcome. SQLite exception details and passphrases do not cross the storage boundary.
- Multi-workspace selection, custom workspace paths, migrations, backup, restore, and passphrase rotation remain outside this slice.
