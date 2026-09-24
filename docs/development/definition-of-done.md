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

The repository-wide build, test, formatting, and packaging commands must be added here when the first runnable Avalonia/.NET slice is created.
