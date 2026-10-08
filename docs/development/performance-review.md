# Performance review baseline

Status: issue #86 investigation and issue #87 incremental-projection follow-up, measured 2026-10-08.

## Scope and reported symptom

In a follow-up to issue #86, the user reported a 0.5–1.0 second delay on Windows after every write or content-change operation. The affected app version, Windows version, hardware and workspace size are not available, so that exact environment cannot be reconstructed. A hosted Windows 2025 run with synthetic data reproduced the same class and magnitude of repeated write stall. This review establishes a repeatable baseline and isolates the write path without changing domain behaviour, encryption, data integrity or accessibility.

This is a measurement report, not an optimisation. Follow-up changes must retain the automatic-save, revision ordering, navigation flush, failure, retry and discard requirements in `EDIT-001` through `EDIT-003`.

## Reproduction command

Build the Release desktop application from a clean commit, then run its synthetic performance mode. The harness reads the commit from the assembly informational version instead of accepting a caller-supplied value. Commit-bound evidence still requires the repository's `--evidence --expected-sha` gate because a local build alone cannot prove that its source tree was clean.

```sh
dotnet build src/DotOrbit.Desktop/DotOrbit.Desktop.csproj --configuration Release -p:RestoreLockedMode=true
dotnet src/DotOrbit.Desktop/bin/Release/net10.0/dot-orbit.dll \
  --performance-review \
  --performance-tasks=1000 \
  --performance-iterations=20 \
  --performance-budget-ms=100
```

The native executable can be used instead of `dotnet <dll>` on packaged or hosted runners. Linux requires the same Xvfb setup as the native smoke journey. Exit code `26` means at least one warm measured p95 exceeded the requested budget; it is the expected baseline result at 1,000 Tasks. The single cold save and startup observations are reported but do not determine the exit code because one sample is not a stable percentile.

The harness creates a temporary encrypted SQLite3MC workspace through the production persistence API, opens the real Avalonia main window, measures one cold inspector save, warms each interaction and records aggregate timings and allocation/GC counts. It uses a fixed UTC clock so projection membership is repeatable. It never reads a user workspace. Output contains only commit, configuration, OS/runtime, synthetic record counts and aggregate measurements; deletion of the temporary encrypted workspace is best-effort on exit.

## Baseline environment

- Product baseline: `a120fadeae0b63048a9d370b4ac8298875789d2d` (`origin/main` at the start of the investigation).
- Exact measurement commits: `4e477bb01ef39df6969715fa7dcf7438fcae1e11` for the 20-interaction scaling runs and `bcd503ecaeb61186134f34aead93476230948fbe` for the 100-interaction stress run (same product behaviour; the latter makes the cold observation non-gating).
- Hosted cross-platform measurement: pull-request merge commit `c7ca88d6a24249594cb325e350e3a615822c6a01`, run `37847387193`, on the shared `windows-2025` and `macos-26` runners. The temporary measurement step was removed after its aggregate console evidence was recorded; it uploaded no artifacts.
- Build: Release, locked dependencies.
- Host: Mac mini (Apple M4, 10 cores, 16 GB), macOS 27.0.1 arm64.
- Runtime: .NET 10.0.12; SDK 10.0.401.
- Data: 8 Categories, 8 Participants, approximately one Project per 20 Tasks, mixed standalone and Project Tasks, Today membership, dates, Participant associations and 5% completed/archived Tasks.
- Interaction method: one cold write after selecting its inspector, 3 warm-up writes/interactions, then 20 repetitions for the main baseline. `startup-to-open` is one cold main-window construction-to-open observation after the encrypted session has already been created; it is not full process launch/unlock time.

## Results

All times are milliseconds. The table reports p95 direct-command-to-Avalonia-application-idle latency. This is a repeatable diagnostic boundary, not true pointer/key-input-to-presented-frame latency. `action` and `stable` values were nearly identical for full reload and save, which shows that little additional work was queued before the application-idle callback; it does not measure later frame presentation.

| Synthetic Tasks | View switch | Snapshot read | Storage update | Projection refresh | Task title save | Window construction-to-open |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 33 | 1.7 | 15.3 | 20.1 | 72.4 | 85.1 | 410.3 |
| 250 | 6.9 | 15.4 | 21.1 | 95.0 | 95.4 | 555.1 |
| 1,000 | 27.1 | 36.5 | 25.3 | 159.9 | 191.5 | 993.2 |

The single cold title-save observation rose with workspace size: command-to-idle was 114.3 ms at 33 Tasks, 347.4 ms at 250 Tasks and 942.0 ms at 1,000 Tasks. Because this includes first-use UI work and is one observation per process, it is diagnostic rather than a percentile or gate.

### Hosted Windows and macOS evidence

The shared runners are not controlled performance hardware, so these distributions support diagnosis but are not regression thresholds. Both used 1,000 Tasks and 20 warm repetitions against the same tested tree.

| Runner | Storage update p50 / p95 | Full reload p50 / p95 / max | Task title save p50 / p95 / max | Cold title save to idle |
| --- | ---: | ---: | ---: | ---: |
| `macos-26` | 46.9 / 54.5 | 423.9 / 532.4 / 2,114.4 | 485.4 / 575.0 / 596.0 | 2,650.0 |
| `windows-2025` | 81.7 / 157.6 | 209.0 / 275.1 / 1,697.3 | 558.7 / 2,335.2 / 2,934.4 | 1,646.4 |

The Windows median title save of 559 ms reproduces the user's reported 0.5–1.0 second pause on synthetic data, while its multi-second p95 shows a severe long tail. The direct Windows storage update alone exceeds the 100 ms interaction target at p95, and the full reload independently contributes a material UI-thread stall. This evidence justifies both incremental UI application and a serialized asynchronous writer; doing only one leaves the other synchronous pause in place.

At 1,000 Tasks, each empty Task-Bin and Project-Bin read independently cost 15.3 ms and 18.2 ms p95. A refresh performs both reads in addition to the workspace snapshot read.

A 100-interaction stress run at 1,000 Tasks reproduced the long tail:

- full reload: 141.6 ms median, 149.5 ms p95, 534.4 ms maximum and 16.0 MB median allocation per interaction;
- Task title save: 168.2 ms median, 193.4 ms p95, 204.4 ms maximum and 18.6 MB median allocation per interaction;
- 100 full reloads coincided with 224 generation-0 and 56 generation-1 collections;
- 100 title saves coincided with 223 generation-0 and 53 generation-1 collections.

## Incremental projection follow-up

Issue #87 replaces the post-write full reload for existing Project and Task field edits with a committed-record apply path. Ordinary title, description, Category, date, colour and existing-Participant association edits consume the authoritative record returned by the mutation and do not reread the workspace or either Bin. Creating a new Participant still performs one workspace read because the current mutation result does not return the newly created Participant records; that narrower result contract remains part of the storage work tracked by #90.

Project, Task, Category-group, Upcoming-group, Today-wrapper and Archive-search identities are reconciled by key. A Category or due-date edit updates only the affected groups, while title and description edits leave projection collections unchanged. Active Archive search also reconciles record-local edits without falling back to a full desktop reload. This preserves inspector focus, row selection, disclosure state and accessible names while updating derived Category, date and navigation-count presentation.

A pre-publication local Release comparison on the same Apple M4 host, 1,000-Task shape and 100 warm interactions produced:

| Operation | p50 | p95 | maximum | Median allocation |
| --- | ---: | ---: | ---: | ---: |
| Incremental Task projection apply | 0.069 ms | 0.111 ms | 0.532 ms | 18,536 bytes |
| Complete Task title save | 23.969 ms | 25.672 ms | 27.867 ms | 2,558,472 bytes |
| Retained full reload observation | 134.160 ms | 152.952 ms | 718.111 ms | 13,563,648 bytes |

The harness now gates incremental projection apply at 50 ms p95, complete title save at 100 ms p95 and 200 ms maximum, and title-save allocation below 4 MB median. It continues to report the full reload as an observation because the following cross-projection operations intentionally retain it: initial/external refresh; creation and quick add; Category creation, deletion and reorder; Project, shared-Task and Project-Task reorder; completion and reopen; Today membership, lane, reorder and clear; archive, restore and bulk archive; Bin operations; Task attachment/context changes; and local-date/time-zone rollover. These transitions change membership or ordering across multiple projections and are outside #87's record-local edit boundary.

A local 10-second macOS CPU sample was also taken during an earlier synthetic 100-interaction diagnostic run. It confirmed that the measured work and collections occur on the application/UI thread and reported a 1.9 GB physical footprint at the sampled point. Managed stack symbols were incomplete, so method attribution comes from the one-variable harness measurements and source inspection rather than guessed profiler frames. The raw sample stayed local and is not a repository or GitHub artifact.

## Confirmed contributors

### Full synchronous reload after every write

Project and Task saves call `ReloadAndKeepInspector`, and `Reload` synchronously reads the workspace, Task Bin and Project Bin before rebuilding or resynchronising every desktop projection. It clears Projects, archived Projects, Category groups, Today groups, Completed groups, Archive groups, Bin rows and participant/category choices, even when one Task title changed. The rebuild also contains repeated linear `Single`, `Contains` and per-Project Task scans.

This is the dominant measured macOS path: at 1,000 Tasks, the complete reload was 145.9 ms median while a direct storage update was 25.3 ms p95. The reload includes its storage reads as well as rebuilding desktop projections, so the measurement does not attribute all of that time to collection work. It allocated about 16.0 MB per interaction and accounted for most observed collections. Action and stable timings differed by less than 1 ms at the median before Avalonia application idle; presented-frame timing remains unmeasured.

### Repeated encrypted connection setup and full reads

The persistence coordinator opens and configures a new SQLite3MC connection for every read and write with pooling disabled. A Task update reads the full snapshot before and after its row update, every committed mutation rebuilds the complete archive search index from another full snapshot, and the desktop refresh then performs three separate reads/connections.

On the local Mac, direct update was about 25 ms p95, while the three post-write reads contributed roughly 15–18 ms p95 each before the remaining reload work. Hosted Windows increased direct update to 157.6 ms p95 and Bin reads to roughly 23 ms p95, but these measurements do not isolate connection/key-derivation cost from mutation queries, filesystem behavior, host contention or security scanning. Repeated open/key-derivation is therefore a stronger Windows hypothesis, not a demonstrated cause.

### Allocation and GC amplify the long tail

The full rebuild creates new grouping and wrapper objects, replaces collections and raises broad change notifications on every write. At 1,000 Tasks it allocates about 19 MB per title save. The 100-interaction run's frequent generation-0/1 collections correlate with refresh maxima of 0.64–0.85 seconds, making allocation pressure a leading contributor hypothesis rather than a demonstrated per-sample cause.

### View-only interactions are a control, not the reported defect

Warm Task selection and view switching stayed below 32 ms p95 at 1,000 Tasks. The observed regression is therefore tied to mutation plus refresh, not navigation state changes alone. Non-virtualised `ItemsControl` surfaces remain a scaling concern for startup and large visible lists, but they were not the main repeated write stall in this investigation.

## Asynchronous-writer measurement contract

Issue #88 separates Task-title save measurement into two intentionally different boundaries. The `task-title-save-action` sample ends after the immutable revision has been admitted to the single-writer path, so it measures synchronous UI-thread work and has a 50 ms p95 budget; its allocation value remains current-thread allocation for that bounded UI action. The corresponding `task-title-save-stable` sample awaits that exact revision's committed result before awaiting Avalonia application idle; it therefore cannot report a false green while encrypted persistence is still running. Stable allocation uses process-wide total allocated bytes across that whole boundary so writer-thread allocations are included, and the 4 MB median gate is applied to this value. Stable latency retains its 100 ms p95 and 200 ms maximum constraints. The cold observation uses the same boundaries but remains non-gating.

## Responsiveness targets

Microsoft classifies a button's first response as a fast interaction with 100 ms ideal and 200 ms maximum, and recommends Release measurements on representative hardware. Apple likewise treats more than 100 ms of synchronous main-thread work for a discrete interaction as a noticeable hang. These platform targets support the following dot-orbit acceptance thresholds:

- Project and Task write-to-visible-stable latency: p95 at or below 100 ms and maximum at or below 200 ms over 100 warm interactions in a 1,000-Task encrypted synthetic workspace on macOS, Windows and Linux.
- Synchronous UI-thread work for a discrete save/change action: p95 at or below 50 ms, leaving headroom for framework input and rendering.
- Encrypted storage update: p95 at or below 50 ms at 1,000 Tasks; navigation and close must await a pending write without losing or reordering revisions.
- Task title save allocation: median below 4 MB at 1,000 Tasks, with no full-workspace collection reset for a record-local edit.
- Existing view-switch baseline must not regress beyond 100 ms p95.

Sources: [Plan and measure Windows app performance](https://learn.microsoft.com/en-us/windows/apps/develop/performance/planning-measuring-performance), [Keep the Windows UI thread responsive](https://learn.microsoft.com/en-us/windows/apps/develop/performance/keep-ui-thread-responsive), and [Improving app responsiveness](https://developer.apple.com/documentation/xcode/improving-app-responsiveness). Checked 2026-10-08.

## Ordered remediation

1. [#87](https://github.com/nullabletype/dot-orbit/issues/87) makes record-local post-write updates incremental so one committed edit does not read and reconstruct every view. It preserves row identity, selection, focus, announcements and derived values, addressing the largest measured complete path and allocation source.
2. [#88](https://github.com/nullabletype/dot-orbit/issues/88) introduces one asynchronous, serialized, revision-aware writer after #87 defines the bounded completion update. Hosted Windows already shows direct synchronous storage at 157.6 ms p95, so persistence must leave the UI thread even after the reload is made incremental. Editing remains responsive while Saving/Saved/Failed stays visible; stale completions are ignored and navigation, close and immediate actions asynchronously flush the latest valid revision.
3. Investigate the remaining storage trace without presupposing an order between [#89](https://github.com/nullabletype/dot-orbit/issues/89) and [#90](https://github.com/nullabletype/dot-orbit/issues/90). #89 tests safe unlocked-connection reuse if Windows evidence shows repeated connection setup is material. #90 replaces record-local full-snapshot result reads with targeted reads and makes archive-index maintenance change-aware and atomic; it can proceed independently if those operations remain material.

Each issue runs the same committed harness on Windows 2025 and Ubuntu 24.04 as well as macOS and records evidence against its exact commit. After the fixes establish stable distributions, the final issue calibrates a cross-platform regression gate. No traces, screenshots, logs or test results may be uploaded as GitHub artifacts.

#87 and #88 are both evidence-led, in that order: moving persistence off-thread alone would leave the full UI reload blocking, while incremental UI updates alone would leave the Windows storage call blocking. Connection reuse and targeted storage/index work remain separate hypotheses until residual traces after those two changes justify their risk.
