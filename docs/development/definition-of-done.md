# Definition of done

An issue is complete only when:

- every acceptance criterion has evidence;
- relevant unit and integration checks pass;
- domain invariants are covered at the appropriate boundary;
- affected UI has been exercised in the real desktop runtime;
- keyboard and assistive-technology behaviour has been considered;
- failure and empty states relevant to the slice are handled;
- documentation and ADRs match the implemented behaviour;
- no unrelated files are included;
- the tested commit is recorded; and
- residual risk or missing independent review is stated plainly.
- every direct and transitive dependency, SDK, runtime, action, runner image, and packaging tool used by the change is on a current supported release; checked versions, upstream evidence, and the check date are recorded.
- package versions are reproducibly pinned and lockfiles are current.
- deprecated, end-of-life, unmaintained, or affected-but-unpatched components block acceptance.
- any GitHub artifact upload contains only packaged Release-configuration application binaries for an explicitly supported runtime, plus optional PDB files.
- no Docker/OCI image, container filesystem, SDK, standalone runtime payload, dependency cache, source tree, intermediate output, test result, log, screenshot, or general evidence bundle is uploaded to GitHub artifact storage.

## Repository gate

Run the following from the repository root:

```sh
dotnet restore DotOrbit.slnx --locked-mode
dotnet format DotOrbit.slnx --no-restore --verify-no-changes
dotnet build DotOrbit.slnx --configuration Release --no-restore
dotnet test --solution DotOrbit.slnx --configuration Release --no-build
```

The first shell deliberately does not define a packaging command. Packaging, signing, installers, and release publication remain outside issue #1 and must be added with their own supported-runtime evidence before the first release.

## Desktop verification levels

- Every pull request runs the headless interaction tests and the native desktop smoke journey on the explicit macOS, Windows, and Linux runner matrix. The native journey starts the real application, checks the initial Today view, activates another primary destination by keyboard, checks the changed view and retained focus, and closes cleanly.
- Changes that affect layout, visual presentation, focus order, accessible names, roles, states, or announcements also require targeted manual visual and accessibility checks. The automated smoke journey is intentionally stable and does not replace those focused checks.
- Release milestones require the full supported-runtime verification and assistive-technology checks. The per-pull-request smoke gate is a baseline, not release evidence.
- Native smoke diagnostics contain only fixed operational phase, result, exit-code, view, and focus metadata. Routine validation uploads no evidence artifacts.
