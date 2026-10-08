# Performance review baseline

Status: issue #86 investigation, measured 2026-10-08.

## Scope and reported symptom

In a follow-up to issue #86, the user reported a 0.5–1.0 second delay on Windows after every write or content-change operation. The affected app version, Windows version, hardware and workspace size are not available, so that exact environment has not yet been reproduced. This review uses synthetic, non-sensitive workspaces to establish a repeatable local baseline and isolate the write path without changing domain behaviour, encryption, data integrity or accessibility.

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

At 1,000 Tasks, each empty Task-Bin and Project-Bin read independently cost 15.3 ms and 18.2 ms p95. A refresh performs both reads in addition to the workspace snapshot read.

A 100-interaction stress run at 1,000 Tasks reproduced the long tail:

- full reload: 141.6 ms median, 149.5 ms p95, 534.4 ms maximum and 16.0 MB median allocation per interaction;
- Task title save: 168.2 ms median, 193.4 ms p95, 204.4 ms maximum and 18.6 MB median allocation per interaction;
- 100 full reloads coincided with 224 generation-0 and 56 generation-1 collections;
- 100 title saves coincided with 223 generation-0 and 53 generation-1 collections.

A local 10-second macOS CPU sample was also taken during an earlier synthetic 100-interaction diagnostic run. It confirmed that the measured work and collections occur on the application/UI thread and reported a 1.9 GB physical footprint at the sampled point. Managed stack symbols were incomplete, so method attribution comes from the one-variable harness measurements and source inspection rather than guessed profiler frames. The raw sample stayed local and is not a repository or GitHub artifact.

## Confirmed contributors

### Full synchronous reload after every write

Project and Task saves call `ReloadAndKeepInspector`, and `Reload` synchronously reads the workspace, Task Bin and Project Bin before rebuilding or resynchronising every desktop projection. It clears Projects, archived Projects, Category groups, Today groups, Completed groups, Archive groups, Bin rows and participant/category choices, even when one Task title changed. The rebuild also contains repeated linear `Single`, `Contains` and per-Project Task scans.

This is the dominant measured macOS path: at 1,000 Tasks, the complete reload was 145.9 ms median while a direct storage update was 25.3 ms p95. The reload includes its storage reads as well as rebuilding desktop projections, so the measurement does not attribute all of that time to collection work. It allocated about 16.0 MB per interaction and accounted for most observed collections. Action and stable timings differed by less than 1 ms at the median before Avalonia application idle; presented-frame timing remains unmeasured.

### Repeated encrypted connection setup and full reads

The persistence coordinator opens and configures a new SQLite3MC connection for every read and write with pooling disabled. A Task update reads the full snapshot before and after its row update, every committed mutation rebuilds the complete archive search index from another full snapshot, and the desktop refresh then performs three separate reads/connections.

On the measured Mac, direct update was about 25 ms p95, while the three post-write reads contributed roughly 15–18 ms p95 each before the remaining reload work. The complete reload therefore dominates the direct update, but these measurements do not isolate encryption cost from query/materialisation cost. Repeated open/key-derivation remains a Windows hypothesis until the harness runs on the reporter's class of Windows environment or the pull-request matrix.

### Allocation and GC amplify the long tail

The full rebuild creates new grouping and wrapper objects, replaces collections and raises broad change notifications on every write. At 1,000 Tasks it allocates about 19 MB per title save. The 100-interaction run's frequent generation-0/1 collections correlate with refresh maxima of 0.64–0.85 seconds, making allocation pressure a leading contributor hypothesis rather than a demonstrated per-sample cause.

### View-only interactions are a control, not the reported defect

Warm Task selection and view switching stayed below 32 ms p95 at 1,000 Tasks. The observed regression is therefore tied to mutation plus refresh, not navigation state changes alone. Non-virtualised `ItemsControl` surfaces remain a scaling concern for startup and large visible lists, but they were not the main repeated write stall in this investigation.

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
2. Rerun the committed harness, including Windows, after #87. [#88](https://github.com/nullabletype/dot-orbit/issues/88) introduces one asynchronous, serialized, revision-aware writer only if synchronous persistence still blocks the UI materially. Editing remains responsive while Saving/Saved/Failed stays visible; stale completions are ignored and navigation, close and immediate actions asynchronously flush the latest valid revision.
3. Investigate the remaining storage trace without presupposing an order between [#89](https://github.com/nullabletype/dot-orbit/issues/89) and [#90](https://github.com/nullabletype/dot-orbit/issues/90). #89 tests safe unlocked-connection reuse if Windows evidence shows repeated connection setup is material. #90 replaces record-local full-snapshot result reads with targeted reads and makes archive-index maintenance change-aware and atomic; it can proceed independently if those operations remain material.

Each issue runs the same committed harness on Windows 2025 and Ubuntu 24.04 as well as macOS and records evidence against its exact commit. After the fixes establish stable distributions, the final issue calibrates a cross-platform regression gate. No traces, screenshots, logs or test results may be uploaded as GitHub artifacts.

Only #87 is unconditionally evidence-led by this review. Async saving is the likely second step, but it should be revalidated after the full reload is removed: moving the current monolithic path off-thread would retain its allocations and require revision, flush, cancellation and failure semantics. Connection reuse and targeted storage/index work remain separate hypotheses until residual cross-platform traces justify their risk.
