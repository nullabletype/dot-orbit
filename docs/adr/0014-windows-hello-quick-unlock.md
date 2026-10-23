# Windows Hello quick unlock

- Status: Accepted
- Date: 2026-10-22

## Context

The portable workspace passphrase currently opens the encrypted SQLite3MC workspace and every encrypted recovery point. It is deliberately the only recovery credential. Requiring it on every launch is secure and portable, but an optional device-local shortcut can improve routine Windows use if the shortcut does not become password recovery, weaken the storage boundary, or make claims that the host platform cannot prove.

The platform research in [`quick-unlock-platform-research.md`](../planning/quick-unlock-platform-research.md) distinguishes vendor guarantees from dot-orbit conclusions and observed behaviour that still requires a Windows 11 probe. The decisive constraints are:

- Windows can request user verification for a desktop window through `IUserConsentVerifierInterop`, but the result identifies only successful Windows verification, not whether PIN, fingerprint, face, or another configured method satisfied it.
- DPAPI-NG `LOCAL=user` protects a blob for the same user on the same local computer. It does not bind the blob to dot-orbit, its executable, or a Windows Hello result.
- the current macOS deliverable is an unsigned bare executable whose code-directory identity changes with changed executable bytes, so it cannot supply stable application identity for a protected Keychain item across upgrades;
- generic freedesktop Secret Service permits provider-dependent unlocked state and prompts and cannot promise fresh authentication for each retrieval; and
- the portable passphrase must remain available on every launch and must remain the only credential that opens a copied workspace or encrypted recovery on another device.

## Decision

Add a planned, optional **Quick unlock with Windows Hello** capability on supported Windows 11 hosts only. It is off by default and is never required to use dot-orbit. macOS and Linux remain passphrase-only under the current distribution and provider-neutral boundaries and do not show a disabled or misleading quick-unlock control.

Quick unlock is convenience authorization for one local unlock attempt. It does not replace the portable passphrase, change SQLite3MC encryption, open a recovery point, recover a forgotten passphrase, or establish the identity of the dot-orbit executable.

### Threat model and wording

The accepted claim is deliberately narrow:

| Scenario | Accepted guarantee |
| --- | --- |
| Stolen powered-off device or copied protected record | The DPAPI-NG `LOCAL=user` record is usable only by the same operating-system user on the same local computer. The workspace and recovery files remain independently passphrase-encrypted. |
| Unlocked user session or another same-user process | Not protected. Another process running as the user may be able to read and unprotect the same record. |
| Administrator or SYSTEM access | Not protected. |
| Malware, process injection, or compromised operating system | Not protected. |
| Application replacement or update | Not protected by DPAPI-NG application identity. Orbit validates its own workspace and rotation bindings, but Windows does not bind release of the record to this executable. |
| Protected-record disclosure | A disclosed blob remains limited by `LOCAL=user`, but a same-user process on the originating computer may unprotect or restore it. The generation check rejects an old blob against the current post-rotation workspace; it is not revocation or anti-rollback. |

Product and accessibility text says **Windows Hello** or **system authentication**. It never says `fingerprint-only`, `biometric authentication`, or that a particular factor was used. Fingerprint or face may satisfy the Windows prompt when configured, but dot-orbit cannot select or attest to either factor.

“Per-attempt verification” means that Orbit starts one new `UserConsentVerifier` operation for each quick-unlock attempt and accepts only that operation's result. Microsoft does not guarantee that every operation displays a new prompt or requires a new gesture. Orbit does not call the request “fresh user presence,” and no security claim relies on prompt appearance or factor recency. If future platform evidence supplies a stronger enforceable primitive, adopting it requires a new decision.

### Protected material and native boundary

After a successful current-passphrase unlock, explicit enrolment creates a versioned protected record containing:

- the normalized portable passphrase bytes needed by the ordinary workspace-open path;
- the exact random workspace identity from encrypted workspace metadata;
- a SHA-256 digest of the canonical local workspace location; and
- the current passphrase-rotation generation.

The workspace identity is a new random 256-bit value created with the workspace or by a forward migration and retained by that workspace's encrypted recoveries. The rotation generation is a checked non-zero integer in the same encrypted metadata and changes in the validated rotation candidate. Neither value advances the user-data Workspace revision. The location digest is SHA-256 over a versioned Windows file-location identity containing the volume serial, file identifier, and final path obtained from an opened workspace handle. It is never treated as secret or as authentication, but remains inside the protected record and diagnostics never emit it or the underlying identity. A copy, rename, move, replacement, or reparse to another resolved location changes that identity and requires passphrase unlock and re-enrolment.

DPAPI-NG protects the complete record with a `LOCAL=user` descriptor. The record is one versioned, size-bounded, closed binary value under dot-orbit's canonical per-user local-application-data directory, outside the workspace and its recovery directory. The adapter rejects an unknown version, non-canonical length, trailing data, reparse point, non-regular file, and an owner or DACL other than the exact current-user-and-SYSTEM policy. Creation uses a flushed same-directory temporary file and atomic no-follow publication or replacement; concurrent enrol, forget, and rotation record operations are serialized. The record is excluded from encrypted recovery points, plaintext exports, workspace copies, logs, diagnostics, screenshots, real-data tests, crash evidence, CI output, and GitHub artifacts. Synthetic secret-free fixtures may exercise parsing and failure handling. No plaintext settings or fallback file is permitted.

The workspace identity, location digest, and rotation generation are application-level bindings inside the protected payload; they are not DPAPI-NG guarantees. The location digest is checked before attempting open. The recovered passphrase then authenticates a validation-only connection that reads only the encrypted identity and generation metadata. A mismatch closes that connection before user data is read or an unlocked session is constructed; a match permits construction of the ordinary session. A workspace copy, move, replacement, or restore changes the file-location identity and therefore requires its passphrase even when its encrypted identity matches. An old protected record cannot unlock the current post-rotation workspace, but a copied record restored after **Forget** remains usable by the same Windows user on the same computer while the workspace generation is unchanged. A paired rollback to both an old workspace and its matching old record is also not prevented. Strong revocation or anti-rollback would require an independent non-rollbackable anchor and is outside this contract.

Native interop remains in narrow Windows desktop adapters. Core owns platform-neutral lifecycle decisions and typed outcomes, not HWND discovery, Windows Runtime types, DPAPI-NG handles, or broad credential-store services. The adapter accepts the live owning-window handle and byte-oriented buffers. It uses `NCRYPT_SILENT_FLAG`, returns fixed non-sensitive outcomes, zeroes adapter-owned native and managed mutable plaintext buffers in `finally`, and releases native allocations immediately after handing the passphrase to the ordinary validated open path. The existing workspace session retains its managed `WorkspacePassphrase` while unlocked as already allowed by DATA-014; this decision does not claim to zero that immutable session-lifetime value. Known-buffer zeroing bounds additional quick-unlock exposure but cannot promise removal of runtime copies, paging, crash capture, or data visible to a compromised process.

### Enrolment, unlock, forget, and rotation

Enrolment is explicit and is offered only after the current passphrase has successfully opened and validated the workspace. Enabling quick unlock must not advance the Workspace revision or change workspace or recovery bytes.

Every quick-unlock attempt:

1. starts one HWND-owned Windows Hello/system-authentication operation for the current unlock window;
2. remains valid only while that initiating window and attempt are current;
3. proceeds only when that operation returns `Verified`;
4. unprotects and strictly parses the local record, validates the file-location digest, then authenticates one validation-only workspace connection and compares the encrypted workspace identity and rotation generation without reading user data;
5. constructs the ordinary unlocked session only after those values match; and
6. releases and zeroes adapter-owned plaintext buffers as soon as handoff completes or the attempt fails.

Passphrase entry remains visible, enabled, and immediately reachable before, during, and after the native prompt. Submitting the passphrase, closing the window, or shutting down invalidates the quick-unlock attempt; a later native completion is ignored. Cancellation, denial, unavailable or unconfigured verification, policy disablement, device busy, exhausted retries, missing/corrupt/denied records, native errors, a mismatched record, or an unsuccessful workspace open all end the quick attempt and leave the passphrase path usable. There is no automatic retry, alternate credential-store fallback, or weaker unprompted path.

Permanent support and per-attempt availability are separate. On Windows 11 where the adapter and record store are supported, Settings shows enrolled/not-enrolled status even when Windows Hello is temporarily unavailable. **Forget quick unlock on this device** deletes only the device-local protected record and does not modify, re-encrypt, damage, or lock the workspace. Already absent is success. After any denied, native, or I/O outcome, the record store rechecks state: confirmed absence is success; a present or unreadable result is a fixed failure and retains enrolled or needs-attention status. Forget is not revocation of a previously copied blob and must remain possible without invoking Windows Hello.

Passphrase rotation writes a new generation into the encrypted candidate and validates that candidate. Within the serialized quick-unlock lifecycle it must then confirm idempotent record removal before publishing the candidate as the accepted working store. A removal outcome that cannot confirm absence aborts publication, cleans up the candidate, and preserves the original workspace; the record may safely be present or absent and is reconciled on the next status read. It never silently rewraps the old credential. After the rotated workspace has been validated with the new passphrase, the user may explicitly enrol again. An interruption before replacement can leave the original workspace usable only by passphrase; an interruption after replacement can leave an absent or stale record that cannot open or match the current workspace. The passphrase path remains authoritative in both cases.

### Runtime capability and accessibility

Permanent support is detected at runtime from Windows 11, the HWND interop path, and the protected-record adapter. Per-attempt Windows Hello availability is checked separately. Missing hardware, unconfigured or changed verification factors, policy restrictions, or native-service failure never block or hide passphrase unlock. Orbit is not bound to a biometric enrolment set: after any factor change, the next attempt depends only on the result of a new current Windows verification request.

The unlock surface initially focuses the passphrase field. Its logical tab order keeps the passphrase field and show/hide control before **Use passphrase**, followed by **Quick unlock with Windows Hello** when an enrolled record and per-attempt capability are available. Starting verification marks the quick-unlock action busy and unavailable to a duplicate invocation while leaving passphrase input and submission enabled. The native prompt is owned by the unlock window. Cancellation, unavailable capability, denial, or failure clears busy state, returns focus to the passphrase field, and announces a fixed content-free outcome through a polite live region. Passphrase submission or window closure invalidates the pending attempt; its later completion is ignored and cannot move focus or reopen UI. A successful current attempt closes the unlock window through the ordinary successful-open flow. No state relies on colour or an icon alone, and cancellation never closes the application or damages enrolment.

Within unlocked Settings, the button **Set up quick unlock with Windows Hello** is present only when not enrolled and per-attempt capability is available. It uses the already validated in-memory passphrase and does not show another Windows Hello prompt. While it writes, it exposes a busy state and rejects duplicate activation. Success replaces it with enrolled status and **Forget quick unlock on this device**, focuses that Forget button, and politely announces `Quick unlock set up on this device`. Failure retains and focuses the Setup button and announces a fixed failure. Successful Forget focuses the Setup button when available or otherwise the Security heading, and politely announces `Quick unlock removed from this device`; failed Forget retains and focuses the Forget button with a fixed failure announcement. Status is exposed as text and accessible state, never by colour or icon alone.

### Required Windows 11 evidence

Before production implementation is admitted, a disposable secret-free Windows 11 probe must exercise the actual Avalonia HWND and self-contained single-file package. It records only fixed operational outcomes and covers:

- availability and repeated verification requests with configured PIN and available biometric factors, cancellation, retries, and configuration changes, recording whether a prompt or new gesture occurs without treating either as guaranteed;
- prompt ownership, z-order, keyboard cancellation, focus restoration, screen-reader behaviour, owner-window closure, and races with passphrase submission or shutdown;
- same-user same-process and cross-process DPAPI-NG round trips, another local user, copied blobs on another disposable machine where available, changed executable bytes, profile replacement, corrupt/truncated/missing records, and `NCRYPT_SILENT_FLAG` status codes; and
- create, retrieve, validate, remove, update/replacement, and unavailable-capability behavior through the real packaged boundary.

The probe is not production code and must not handle a real passphrase, workspace, recovery file, user content, or durable credential. Its raw output remains local or in ordinary ephemeral CI logs; it is not uploaded as a GitHub artifact. Observed prompt behaviour does not become a stronger product claim than the owning platform documentation supports.

## Rejected alternatives

- **Credential Locker:** rejected because ordinary credential retrieval does not create Orbit's explicit per-attempt `UserConsentVerifier` request or the required closed workspace/location/generation payload contract.
- **Cryptographically coupling Windows Hello to DPAPI-NG:** rejected because the selected APIs expose no verification token or platform transaction connecting the two operations. Product wording records this limitation.
- **macOS Keychain under the current package:** rejected because the unsigned version-specific code-directory identity is not stable across changed releases. Developer ID membership, notarisation, installers, App Store distribution, or a newly managed signing identity are not prerequisites for dot-orbit.
- **Generic Linux Secret Service:** rejected because the specification permits already-unlocked collections, provider-dependent prompts, and unlocked state shared beyond one application. A provider-specific design would be a separate future decision.
- **Storing a verifier, hint, recovery secret, or portable wrapped key:** rejected because it would either fail to unlock, create a recovery backdoor, or weaken the passphrase-only portability boundary.
- **Custom cryptography or biometric handling:** rejected. Dot-orbit never stores, processes, or compares biometric templates and does not invent a wrapping protocol where maintained OS primitives suffice.

## Consequences

- The portable passphrase remains the sole recovery and cross-device credential and is always available at launch.
- Windows gains an optional device-local convenience path after separately verified implementation slices; macOS and Linux remain honest passphrase-only experiences.
- Quick unlock narrows the physical-copy scenario and requests system verification in Orbit's normal path, but Windows does not guarantee a new prompt or gesture and it does not defend an unlocked or compromised same-user session.
- A stale protected record cannot unlock the current post-rotation workspace because the workspace generation is validated independently of DPAPI-NG release. Forget is deletion rather than revocation, and paired workspace/record rollback remains possible within the excluded same-user/device threat boundary.
- The Windows adapter, enrolment lifecycle, unlock/Settings UI, rotation integration, and supported-platform verification remain separate implementation slices with explicit dependencies.
- The Windows 11 probe and independent security/accessibility review are acceptance evidence for those slices; documentation and a successful cross-compile are not substitutes for native observation.
