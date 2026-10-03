# Project capture

Issue #7 adds the first persisted work slice. Projects opens a list of expandable Projects and a New project action. New project begins as a transient inspector draft; its first valid automatic save creates it, while leaving a blank draft creates nothing. A Project requires a title and Category; its Markdown source and date-only target date are optional. Empty Projects display Not started, 0 of 0 Tasks, and no completion date. Status and completion date are derived, read-only information.

Each expanded Project has a Quick add task field. Enter or Tab on a non-empty title commits one attached title-only Task and returns focus to a fresh field. Empty Tab leaves the field; Escape clears unsubmitted text. Tasks inherit their Project Category, start at the top of shared Backlog order, and append to the Project's own Task order. Capture first flushes any pending valid Project or Task inspector changes.

Selecting a Project or Task opens its editable title, Markdown source, date and Category. Task Category offers an explicit Inherit choice as well as overrides. Valid text changes save after 600 milliseconds of inactivity and discrete changes save immediately. Navigating to another item or view, opening Recovery, or closing the window flushes pending changes first. Invalid or failed changes stay visible and block leaving until corrected, retried, or explicitly discarded. Dates use YYYY-MM-DD and are validated as calendar dates. Category management retains explicit Save and Cancel.

Task inspectors associate or remove Participants and can reveal a temporary New participant field. Equivalent labels reuse the existing Participant identity; global rename and delete actions live in the Settings Participants list. Recovery is launched from the Settings Recovery row.

This slice excludes completion actions, Today membership, rendered Markdown, participants, archive/bin controls, category management, and reorder controls. Schema version 3 adds Project and Task storage through the existing recoverable migration path; existing workspaces migrate before the shell opens. No new dependencies are introduced.

## Automated acceptance evidence

These are test names, not a claim about an untested commit. Run the repository verification gate against the exact commit before publication.

| Requirement | Test evidence |
| --- | --- |
| New Project automatic creation, row refresh, failed-autosave focus and retry/discard flow | `ProjectCaptureWindowTests.ProjectInspectorAutosavesCreationEditingAndFailedNavigationThroughBindings` |
| Transient Project creation and cancellation | `ProjectCaptureViewModelTests.CreationIsTransientAndCancelLeavesNoProject` |
| Required title and empty derived state | `ProjectCaptureViewModelTests.CreateRequiresTitleAndShowsEmptyDerivedSummary` |
| Enter/Tab capture, fresh focus, empty Tab exit and Escape clear | `ProjectCaptureWindowTests.RapidCaptureKeysCreateOnceRetainFocusAndAllowEmptyTabExit` |
| Capture after flushing pending inspector changes, attached defaults and ordering | `ProjectCaptureViewModelTests.QuickAddFlushesPendingProjectChangesAndCreatesInheritedTasksInIndependentOrders` |
| Automatic field persistence and retained invalid calendar date | `ProjectCaptureViewModelTests.ExistingProjectAutosavesValidChangesAndRetainsInvalidDateForCorrection` |
| Task category override and return to inheritance | `ProjectCaptureViewModelTests.TaskCategoryCanBeOverriddenThenReturnedToInheritance` |
| Project Category propagation and live navigation counts | `ProjectCaptureViewModelTests.ProjectCategoryChangesFlowToInheritedTasksAndLeaveOverridesStable` |
| Automatic navigation flush | `ProjectCaptureViewModelTests.NavigationFlushesValidProjectChangesWithoutPrompting` |
| Exact text persistence across arbitrary field boundaries | `ProjectCaptureViewModelTests.NavigationFlushesTextWhoseFieldBoundariesCouldOtherwiseCollide` |
| Failed save retains draft and destination | `ProjectCaptureViewModelTests.FailedSaveRetainsDraftPendingDestinationAndAllowsRetry` |
| Navigation selection remains consistent after failed automatic save | `ProjectCaptureWindowTests.FailedAutosaveNavigationRetainsCheckedViewUntilDecision` |
| Close guard and keyboard focus | `ProjectCaptureWindowTests.ClosingDirtyCreationRequiresDecisionAndStayPreservesDraft` |
| Failed capture retains text and focus | `ProjectCaptureWindowTests.FailedQuickAddKeepsEnteredTitleAndFocus` |
| Real encrypted restart persistence and independent ordering | `WorkspaceWorkTests.CreateAndEditRoundTripsMarkdownDatesInheritanceAndIndependentOrders` |
| Invalid titles and references commit nothing | `WorkspaceWorkTests.InvalidReferencesAndBlankTitlesNeverChangeStoredWork` |
| Released schema upgrade with recovery | `WorkspaceWorkTests.ReleasedSchemaTwoMigratesWithRecoverableCategoryAndPersistsNewWork` |
| Recovery scheduling and closed sessions | `WorkspaceWorkTests.WritesScheduleEncryptedRecoveryAndClosedSessionsRejectWork` |
| Safe storage failure without sensitive output | `WorkspaceWorkTests.DatabaseWriteFailuresExposeNoTaskOrSqlContentAndRetainStoredState` |

Headless interaction checks establish Avalonia keyboard and focus behaviour. Native rendering and assistive-technology checks remain distinct acceptance evidence; a headless pass does not establish either.
