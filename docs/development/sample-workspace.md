# Sample workspace

The sample-workspace tool creates a local encrypted dot-orbit workspace containing representative synthetic data for implementation, visual review, and screenshots. It uses the production encrypted store and domain operations, so it generates the current schema without duplicating persistence details in SQL or committing database bytes.

From the repository root, run:

```sh
dotnet run --project tools/DotOrbit.SampleWorkspace/DotOrbit.SampleWorkspace.csproj
```

The default output is `artifacts/sample-workspace/workspace.orb`, which is ignored by Git. Unlock it in dot-orbit with the public development phrase:

```text
dot-orbit sample only
```

This phrase protects no confidential data and must never be reused for a real workspace. The tool does not print it in runtime diagnostics.

Once generated, a desktop build launched from this repository automatically uses the repository-local sample without moving or replacing the normal application workspace:

```sh
dotnet run --project src/DotOrbit.Desktop/DotOrbit.Desktop.csproj
```

Use local repository launches for implementation and visual testing. They keep the selected sample path when a workflow returns to the unlock screen. Normal packaged launches continue to use the platform application-data workspace. Pass `-- --default-workspace` only when you intentionally need a repository build to open the normal application workspace.

Use a different output file or a fixed date when repeatable date labels and screenshots matter:

```sh
dotnet run --project tools/DotOrbit.SampleWorkspace/DotOrbit.SampleWorkspace.csproj -- \
  --output local-data/sample-workspace/workspace.orb \
  --anchor-date 2030-04-05
```

To generate the workspace at the location the desktop application opens, use:

```sh
dotnet run --project tools/DotOrbit.SampleWorkspace/DotOrbit.SampleWorkspace.csproj -- \
  --default-workspace
```

This mode also refuses to overwrite either an existing workspace or its recovery-state companion. If you intend to replace an earlier disposable development workspace, move both files somewhere safe yourself before running the command. The generator never moves or deletes them.

Without `--anchor-date`, dates are relative to the current local calendar date. The tool refuses to overwrite an existing file; choose a new path or deliberately remove a disposable generated workspace yourself. It never replaces the application's default workspace.

Generation and validation happen in an isolated sibling directory. The finished encrypted database is published only after it reopens successfully; temporary database and recovery-state files are removed. If a database or recovery-state companion already exists at the destination, the tool leaves it unchanged and stops.

## Scenario coverage

The current scenario contains:

- 6 Categories, including a long name and an unused Category;
- 7 Participants, including unreferenced, single-Task, and multi-Task labels;
- 6 Projects covering empty, not-started, in-progress, complete, overdue, undated, and long-title states;
- 33 attached and standalone Tasks with inherited and overridden Categories;
- 21 incomplete and 12 completed Tasks, with completion dates spanning today through more than 30 days ago;
- 6 archived Tasks spanning Today, Yesterday, two days ago, older weekly groups, and 30 days ago, with both attached and standalone examples;
- 1 archived in-progress Project whose individually archived Task remains archived and whose former Today Task has no Today membership;
- 8 Today Tasks across Planned and In progress;
- 11 active incomplete Tasks in the overdue-through-seven-days Upcoming window;
- no date, overdue, today, tomorrow, seven-day, and just-outside-the-window examples;
- plain, multiline Markdown, safe and unsafe link text, raw HTML text, long content, and Unicode;
- interleaved shared order and independent Project-local order.

Identifiers and semantic relationships are deterministic for a fixed anchor date. The encrypted database bytes are intentionally not promised to be identical because the encryption layer may use fresh cryptographic material.

## Evolving the scenario

Add a new state only through its released production domain API. Task and Project Archive examples use their production commands; Bin, Category identity, and Project identity examples must wait for their implementation slices. Do not fabricate future rows through direct SQL. Keep the scenario readable, synthetic, invariant-valid, and broad enough to exercise the changed projection without turning it into a migration fixture or a replacement for focused tests.
