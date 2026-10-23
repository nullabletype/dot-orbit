# Quick-unlock platform research

Research date: 2026-10-22

Issue: [#82 — Decide the threat model and native quick-unlock architecture](https://github.com/nullabletype/dot-orbit/issues/82)

All linked external sources were checked on 2026-10-22. This note uses three evidence labels:

- **Source guarantee** is stated by an owning vendor specification or API reference.
- **dot-orbit inference** follows from the documented API boundary but is not itself an explicit platform promise.
- **Probe required** is material behaviour that the documentation does not settle and must be observed with a disposable, secret-free Windows 11 probe before implementation.

The issue owner has accepted a conservative Windows-only product contract. This research records the platform evidence for that decision; it does not implement quick unlock.

## Conclusions

1. Windows can support an optional **Quick unlock with Windows Hello** flow by starting a new user-verification operation for every unlock and accepting only that operation's `Verified` result. Microsoft does not document which configured factor satisfied `Verified`, so product and accessibility text must say **Windows Hello** or **system authentication**, not `fingerprint-only` or `biometric authentication`.
2. A desktop application must use the HWND-bound `IUserConsentVerifierInterop::RequestVerificationForWindowAsync` path. The plain `UserConsentVerifier.RequestVerificationAsync` method is the UWP path and is not the supported desktop invocation.
3. DPAPI-NG `LOCAL=user` is an appropriate device-local storage boundary: only the same user on the same local computer can decrypt. It is not an Orbit application-identity boundary, and it is not cryptographically coupled to the Windows Hello verification result.
4. The current macOS archive has no stable application identity across changed binaries. Apple Keychain can enforce user presence, but its application trust tracking depends on code-signing requirements; an otherwise unsigned program is identified by a code-directory hash that changes when the executable changes. macOS therefore remains passphrase-only under the current unsigned bare-executable distribution boundary.
5. Generic freedesktop Secret Service cannot promise fresh per-use authentication. A provider may return an already-unlocked item without prompting, may unlock a whole collection, may share unlocked state across applications, and is not required by the specification to implement access control or master-password locking. Linux therefore remains passphrase-only.
6. Quick unlock adds convenience and a system-verification step to Orbit's own path. It does not defend against a same-user process that can read the protected record, administrator/SYSTEM access, malware, application replacement, or a compromised operating system. The portable passphrase remains the only cross-device and recovery credential.

## 1. Windows user verification and honest factor wording

### Source guarantees

`UserConsentVerifier.RequestVerificationAsync` “performs a verification” using a device such as a Microsoft Passport PIN, Windows Hello, or a fingerprint reader, and returns a `UserConsentVerificationResult`. Microsoft describes it as a request for user consent before a sensitive operation ([request API](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier.requestverificationasync?view=winrt-26100)).

The result enum provides only `Verified`, `DeviceNotPresent`, `NotConfiguredForUser`, `DisabledByPolicy`, `DeviceBusy`, `RetriesExhausted`, and `Canceled`. `Verified` means only “The user was verified”; it does not identify the factor used ([result enum](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverificationresult?view=winrt-26100)). Availability similarly reports only whether an authentication device is available, missing, unconfigured, disabled by policy, or busy ([availability check](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier.checkavailabilityasync?view=winrt-26100), [availability enum](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifieravailability?view=winrt-26100)).

Microsoft's Windows Hello login tutorial says an explicit `UserConsentVerifier` request can force the user to sign in again and may ask for the signed-in Microsoft-account password or the Windows Hello PIN ([Windows Hello login tutorial](https://learn.microsoft.com/en-us/windows/apps/develop/security/windows-hello-login)). That is additional evidence that the verifier must not be described as biometric-only.

### dot-orbit inference

For Orbit, per-attempt system verification means creating a new verification operation for each quick-unlock attempt, associating its completion with that one pending attempt, and continuing only when that operation returns `Verified`. Every other enum value, exception, stale completion, or cancellation fails closed to the already-available passphrase screen. It does not mean that Windows guarantees a new prompt or gesture, and the accepted security claim does not depend on either.

The API does not return a factor identity, biometric attestation, or assurance-class token. Orbit therefore cannot select or attest to fingerprint-only authentication, even if a fingerprint happens to satisfy a particular prompt. Capability detection is a runtime availability decision, not proof that a particular future request will succeed.

### Probe required

Microsoft does not document a no-caching interval or promise that every back-to-back request displays a new prompt and requires a new gesture. On supported Windows 11 builds, the disposable probe must record the behaviour of repeated requests after PIN, fingerprint, face, cancellation, retries exhausted, locked/unlocked session transitions, and verifier configuration changes. Observed prompt behaviour informs the implementation and test matrix but does not elevate one observed factor to a platform guarantee.

## 2. Desktop HWND ownership

### Source guarantees

Microsoft's desktop interop guidance says `UserConsentVerifier` implements `IUserConsentVerifierInterop`. Desktop C# applications must call `UserConsentVerifierInterop.RequestVerificationForWindowAsync`, and C++/WinRT applications must call `IUserConsentVerifierInterop::RequestVerificationForWindowAsync`, instead of calling the UWP method ([desktop WinRT UI guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/display-ui-objects)).

The native method accepts the active application's HWND and the message shown for the request. Its minimum supported client is Windows build 22000 ([interop interface](https://learn.microsoft.com/en-us/windows/win32/api/userconsentverifierinterop/nn-userconsentverifierinterop-iuserconsentverifierinterop), [interop method](https://learn.microsoft.com/en-us/windows/win32/api/userconsentverifierinterop/nf-userconsentverifierinterop-iuserconsentverifierinterop-requestverificationforwindowasync)).

### dot-orbit inference

The Windows adapter must remain a narrow desktop interop boundary. It receives the live owning-window handle and a cancellation/lifetime scope from the UI; it must not discover global windows or show an unowned prompt. Completion belongs only to the initiating unlock attempt. Closing the window, choosing passphrase unlock, or shutting down invalidates that attempt, even if the native operation completes later.

### Probe required

Official examples cover WinUI, WPF, and Windows Forms, not Avalonia ([HWND retrieval guidance](https://learn.microsoft.com/en-us/windows/apps/develop/ui/retrieve-hwnd)). The Windows 11 probe must establish:

- how and when the current Avalonia top-level exposes a valid HWND;
- whether the C# interop projection survives the repository's .NET 10 self-contained single-file publish;
- prompt ownership, z-order, modality, focus restoration, keyboard cancellation, and screen-reader announcement behaviour;
- results when the owner is hidden, minimised, closed, or destroyed while verification is pending; and
- safe handling of simultaneous passphrase submission, native completion, and application shutdown.

## 3. DPAPI-NG `LOCAL=user`

### Source guarantees

`NCryptProtectSecret` encrypts keys, key material, or passwords to a protection descriptor; `NCryptUnprotectSecret` decrypts the protected blob ([protect API](https://learn.microsoft.com/en-us/windows/win32/api/ncryptprotect/nf-ncryptprotect-ncryptprotectsecret), [unprotect API](https://learn.microsoft.com/en-us/windows/win32/api/ncryptprotect/nf-ncryptprotect-ncryptunprotectsecret)). Both are desktop APIs available since Windows 8. `NCRYPT_SILENT_FLAG` requests that the key service provider display no UI.

For the `LOCAL` protection descriptor, Microsoft defines three scopes. `LOCAL=logon` ends at logoff or reboot; `LOCAL=machine` permits all users on the local computer; and `LOCAL=user` permits only the current user on the local computer to decrypt ([CNG DPAPI constants](https://learn.microsoft.com/en-us/windows/win32/seccng/cng-dpapi-constants)). Microsoft's data-protection example uses `LOCAL=user` for the current user's SID and warns that protected data cannot be recovered when its protection context disappears, for example after user-profile deletion or machine reimaging ([Windows data-protection guidance](https://learn.microsoft.com/en-us/windows/apps/develop/security/data-protection)).

### dot-orbit inference

`LOCAL=user` is the correct accepted scope. A copied protected blob is not a portable credential for another computer or operating-system account. Conversely, the descriptor contains no Orbit package, signer, executable, or workspace identity. `NCryptUnprotectSecret` accepts the protected blob, flags, an optional window, and allocation parameters; it accepts no application identity and no `UserConsentVerificationResult` or proof token.

Therefore:

- a same-user process that obtains the record can call the same unprotect API;
- replacing or updating Orbit does not invalidate the blob through DPAPI-NG application identity;
- Windows user verification and DPAPI-NG release are two application-composed steps, not one cryptographically coupled platform transaction; and
- administrator/SYSTEM, malware, and a compromised OS are not excluded by any guarantee cited here and remain outside the claimed protection boundary.

The accepted protected record must carry its random encrypted workspace identity, Windows file-location identity digest, and passphrase-rotation generation inside the protected payload. Those bindings are Orbit validation, not DPAPI-NG descriptor semantics. Orbit can check the location before open and must close a validation-only authenticated connection before reading user data or exposing a session when its workspace identity or generation does not match. Deleting the local record is the forget operation; DPAPI-NG documents no revocation handle that could invalidate a protected blob copied earlier. Forget therefore does not revoke a copy, and paired rollback of workspace plus matching record is not prevented. Generation validation rejects an old record only against the current post-rotation workspace.

Orbit should pass `NCRYPT_SILENT_FLAG` for protect and unprotect. The Windows Hello prompt is the only expected UI; any DPAPI-NG status that would require or produce additional UI fails closed rather than silently weakening the flow.

### Probe required

The secret-free Windows 11 probe must cover:

- protect and unprotect with `LOCAL=user` under the same process and under a distinct process for the same user;
- failure under another local user and, where a disposable VM is available, after copying the blob to another computer;
- changed executable bytes/application replacement to demonstrate the absence of app binding;
- corrupt, truncated, missing, duplicate, and deleted records;
- profile deletion/recreation or an equivalent disposable-user test;
- `NCRYPT_SILENT_FLAG`, returned status codes, and any unexpected native UI; and
- the real packaged single-file boundary, not only a development host process.

## 4. macOS Keychain and the current package identity

### Source guarantees

Apple Keychain access controls can require `userPresence`, which permits either biometry or the device passcode. Apple explicitly says the system chooses the mechanism; `userPresence` is equivalent to biometry-or-passcode rather than biometric-only authentication ([user-presence flag](https://developer.apple.com/documentation/security/secaccesscontrolcreateflags/userpresence), [restricting Keychain item accessibility](https://developer.apple.com/documentation/security/restricting-keychain-item-accessibility)). Apple's protected-item example says Keychain Services can request authentication whenever the item is read and describes `.userPresence` as biometric authentication with device-passcode fallback ([Keychain authentication sample](https://developer.apple.com/documentation/localauthentication/accessing-keychain-items-with-face-id-or-touch-id)).

On macOS, a Keychain item's access-control list governs which applications may use it. If the caller is not a trusted application, the system can prompt the user and can add the application after “Always Allow” ([Keychain access-control lists](https://developer.apple.com/documentation/security/access-control-lists)). Apple's code-signing guide states that Keychain access controls track a creating application by its designated requirement, and that two changed binaries remain the same application only when they satisfy the same designated requirement ([code requirements and Keychain tracking](https://developer.apple.com/library/archive/documentation/Security/Conceptual/CodeSigningGuide/AboutCS/AboutCS.html)).

For otherwise unsigned code, macOS uses a code-directory-hash requirement so that Keychain or Parental Controls can recognise the program. Apple also states that the code directory changes whenever the program changes nontrivially, making that requirement identify one specific version ([code-signing requirement language](https://developer.apple.com/library/archive/documentation/Security/Conceptual/CodeSigningGuide/RequirementLang/RequirementLang.html)).

The repository's current macOS deliverable is a self-contained single-file executable in a compressed tar archive. It deliberately adds no installer, signing, notarisation, `.app` bundle, or package identity ([release packaging](../development/release-packaging.md)).

### dot-orbit inference

The current executable is recognised by a requirement tied to that build's code-directory hash, not one that reliably treats the next changed executable as the same application. A Keychain record trusted to today's unsigned binary can therefore prompt again or cease to be silently accessible after an upgrade, while weakening the ACL to broad user access would abandon the application-identity property the design needs.

A stable self-signed identity could technically provide a stable designated requirement, but distributing and safeguarding such a private signing identity would be a new release-security and operator workflow, not a property of the current unsigned archive. It is outside issue #82's accepted constraints just as Developer ID membership, notarisation, installers, and App Store distribution are. macOS remains explicitly unsupported for quick unlock rather than presenting a misleading or fragile option.

## 5. Linux Secret Service limitations

### Source guarantees

The freedesktop Secret Service API stores secrets in a service in the user's login session. The service usually stores them encrypted and may need the user to unlock the service before retrieval. Lookup attributes are not secret and may be stored unencrypted ([Secret Service introduction](https://specifications.freedesktop.org/secret-service/latest-single/#introduction)).

Locking is intentionally provider-dependent. A service may unlock a whole collection when one item is requested; may scope unlocked state to one client or make it available to other applications; may relock at any time; and may immediately return an unlocked item with no prompt ([locking and unlocking](https://specifications.freedesktop.org/secret-service/latest/unlocking.html)). A prompt, when returned, is displayed by the service and may request information such as a master password, but a prompt is not mandatory for every unlock or retrieval ([prompts](https://specifications.freedesktop.org/secret-service/latest/prompts.html)). The specification explicitly does not mandate a master password or any form of application access control ([features not included](https://specifications.freedesktop.org/secret-service/latest-single/#not-included)).

Secret transfer also varies. D-Bus traffic passes through multiple processes and may be cached; the specification offers encrypted transfer to reduce plaintext exposure but does not claim resistance to active man-in-the-middle attacks. Its `plain` algorithm performs no encryption, and the specification strongly recommends that services support it ([secret transfer](https://specifications.freedesktop.org/secret-service/latest-single/#transfer-secrets)).

### dot-orbit inference

The generic API can store a device/session-local secret, but it cannot prove that the active provider performed fresh system authentication for this retrieval, what factor a prompt used, or that another same-user application cannot use already-unlocked state. Provider-specific extensions could offer stronger behaviour, but supporting one would be a separate platform contract and capability matrix. Linux therefore remains passphrase-only and shows no unavailable or disabled biometric control.

## 6. Plaintext lifetime and diagnostics

### Source guarantees

`NCryptProtectSecret` receives plaintext bytes from the caller. `NCryptUnprotectSecret` returns decrypted bytes in a caller-owned allocation; with the default allocator, the caller must release it with `LocalFree` ([protect API](https://learn.microsoft.com/en-us/windows/win32/api/ncryptprotect/nf-ncryptprotect-ncryptprotectsecret), [unprotect API](https://learn.microsoft.com/en-us/windows/win32/api/ncryptprotect/nf-ncryptprotect-ncryptunprotectsecret)). Microsoft documents `LocalFree` as releasing the allocation, not overwriting it ([`LocalFree`](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-localfree)).

Microsoft's password-handling guidance says to collect secrets as late as possible, discard them as early as possible, overwrite their buffers with `SecureZeroMemory`, and never place credential material in diagnostic logs, event logs, or crash dumps ([handling passwords](https://learn.microsoft.com/en-us/windows/win32/secbp/handling-passwords)). `SecureZeroMemory` is specifically designed so the compiler cannot optimise away the overwrite ([secure zeroing](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/aa366877(v=vs.85))).

### dot-orbit inference

The native adapter should expose byte-oriented operations, not return a managed `string`. It should keep the passphrase and unprotected payload in the smallest practical owned mutable buffers, validate workspace and generation without producing diagnostic copies, zero the native plaintext buffer before `LocalFree`, zero adapter-owned managed mutable buffers in `finally`, and release them immediately after handoff or failure. The current ordinary workspace session retains an immutable managed passphrase while unlocked; issue #82 does not claim to erase or refactor that DATA-014 lifetime. Zeroing adapter-owned buffers narrows additional lifetime but cannot prove removal of copies made by runtimes, libraries, crash capture, paging, or a compromised process.

The record, passphrase, derived keys, decrypted payload, workspace path, workspace-derived identifiers, and content-derived hashes must never appear in logs, exception text, metrics, screenshots, real-data tests, CI output, or artifacts. Secret-free synthetic records may exercise strict parsing, corruption, and failure handling without containing a real credential or workspace value. Operational diagnostics should use fixed result codes and non-sensitive phase names only. The protected record remains local application state and is excluded from the workspace, recovery files, backups, exports, and GitHub artifacts.

## 7. Accepted implementation boundary

The sourced platform facts support this bounded contract:

- Offer quick unlock only on supported Windows 11 hosts where runtime capability detection succeeds.
- Keep it off by default and enrol only after a successful current-passphrase unlock.
- Request one HWND-owned Windows Hello/system-authentication operation per quick-unlock attempt; accept only `Verified` for that still-current attempt.
- Unprotect a device-local `LOCAL=user` record only after verification, validate the canonical local-location digest, then use a preliminary ordinary workspace open to validate the encrypted workspace identity and rotation generation before exposing an unlocked session.
- Keep passphrase entry immediately available before, during, and after the native prompt.
- Treat cancellation, denial, unavailable/busy/unconfigured verification, native exception, missing/corrupt/mismatched record, and workspace-open failure as a closed quick-unlock attempt with ordinary passphrase fallback.
- Forget only the local record. Passphrase rotation invalidates the prior generation and requires explicit re-enrolment after the rotated workspace has been validated.
- Do not describe this as password recovery, fingerprint-only authentication, biometric-only authentication, application identity, or protection from same-user/admin/malware/OS compromise.
- Do not expose quick-unlock controls on macOS or Linux under the accepted distribution and provider-neutral boundaries.

## Primary sources

- Microsoft, [`UserConsentVerifier`](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier?view=winrt-26100), [`RequestVerificationAsync`](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverifier.requestverificationasync?view=winrt-26100), and [`UserConsentVerificationResult`](https://learn.microsoft.com/en-us/uwp/api/windows.security.credentials.ui.userconsentverificationresult?view=winrt-26100)
- Microsoft, [desktop WinRT UI interop](https://learn.microsoft.com/en-us/windows/apps/develop/ui/display-ui-objects) and [`IUserConsentVerifierInterop`](https://learn.microsoft.com/en-us/windows/win32/api/userconsentverifierinterop/nn-userconsentverifierinterop-iuserconsentverifierinterop)
- Microsoft, [CNG DPAPI constants](https://learn.microsoft.com/en-us/windows/win32/seccng/cng-dpapi-constants), [`NCryptProtectSecret`](https://learn.microsoft.com/en-us/windows/win32/api/ncryptprotect/nf-ncryptprotect-ncryptprotectsecret), and [`NCryptUnprotectSecret`](https://learn.microsoft.com/en-us/windows/win32/api/ncryptprotect/nf-ncryptprotect-ncryptunprotectsecret)
- Apple, [Keychain item accessibility](https://developer.apple.com/documentation/security/restricting-keychain-item-accessibility), [Keychain access-control lists](https://developer.apple.com/documentation/security/access-control-lists), and [code-signing requirements](https://developer.apple.com/library/archive/documentation/Security/Conceptual/CodeSigningGuide/AboutCS/AboutCS.html)
- freedesktop.org, [Secret Service API 0.2 draft](https://specifications.freedesktop.org/secret-service/latest-single/), [locking](https://specifications.freedesktop.org/secret-service/latest/unlocking.html), and [prompts](https://specifications.freedesktop.org/secret-service/latest/prompts.html)
