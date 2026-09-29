# Task completion and the Projects projection

Issue #9 completes the first Projects projection. A Task completion is one atomic immediate action that records the current instant and the local calendar date observed at that instant. Reopening clears both values, and a later completion replaces them. The captured date is persisted rather than recalculated, so moving the workspace between time zones cannot move historical completion between dates.

Project status, progress and completion date remain derived. Empty Projects are Not started and incomplete. A non-empty Project is In progress when only some Tasks are complete and Complete only when every Task is complete; its completion date is then the latest captured Task completion date. An active incomplete Project whose target date is before today is shown as Overdue in its existing manual position. This does not add a Projects navigation badge.

Attached and standalone Task rows use the same native toggle action with a dynamic Complete or Reopen accessible name and checked state. Completing the drafted Task from Backlog first uses the existing save, discard or stay decision because the row will disappear; after completion, focus moves to the next available completion toggle or the Backlog quick-add field. Completion and reorder actions elsewhere commit independently without saving or discarding inspector fields.

Completed supplies the minimum requirements-consistent reopen route: it groups Tasks by captured completion day for Today and the preceding two days, then by Monday-based calendar week. Groups follow captured calendar order newest first, independently of time-zone-dependent instant ordering, while Tasks inside each group use newest completion instant first. It shows each persisted completion date and provides the same toggle to reopen the Task. Reopening the drafted row first uses the save, discard or stay decision, then removes the reopened Task from Completed and returns it to Backlog. Bulk archive, history search and other archive behaviour remain owned by issue #15.

Projects and each Project's attached Tasks have separate transactional orders. Their reorder triggers support pointer drag within the matching scope and keyboard-operable menus for Move up, Move down, Move to top and Move to bottom, restore focus, and announce the resulting position and scope. Cross-Project Task drops are ignored. These orders are separate from the shared incomplete-Task order used by Backlog. Backlog reorder positions count only visible incomplete Tasks; completed Tasks retain their hidden shared-order slots.

Pointer reorder state is cancelled on every new press, pointer-capture loss, window deactivation and close. Cancellation clears the stored scope and identifiers and removes any insertion highlight, so a lost release cannot affect a later click.

Project target dates and Task due dates use a native calendar picker with retained `YYYY-MM-DD` keyboard entry and date-only validation. Rows show Today, Tomorrow, a calendar date, or No date, with a full unambiguous date exposed to assistive technology. The open window refreshes this presentation on activation and at a low-frequency UI-thread interval when the injected `TimeProvider` reports a new local date or time zone. Captured Task completion dates are read-only in Task rows and the inspector.

Project status keeps its text and adds a secondary colour treatment: neutral for Not started, amber for In progress and green for Complete. Status is never communicated by colour alone.

Schema version 5 adds the latest completion instant and captured local date as an all-or-nothing pair. Migration from schema 4 preserves work and ordering and marks existing Tasks incomplete through the validated encrypted recovery-point path. No new dependency is introduced.

## Verification map

| Behaviour | Automated evidence |
| --- | --- |
| Empty, partial and complete Project derivation; latest completion date; overdue boundary | `WorkCompletionDerivationTests.EmptyProjectIsNotStartedIncompleteAndHasNoCompletionDate`, `PartialAndCompleteProjectStatesUseLatestCapturedTaskDate` |
| Completion, reopen, recompletion, time-zone stability and rollback | `WorkspaceWorkTests.CompleteReopenAndRecompleteAtomicallyReplaceCapturedValuesAcrossTimeZones`, `CompletionFailureRollsBackBothCapturedFieldsWithoutExposingPrivateDatabaseDetails` |
| Incomplete-only Backlog reorder with interleaved completed slots | `WorkspaceWorkTests.SharedReorderMovesOnlyIncompleteTasksAndPreservesCompletedSlots`, `ProjectCaptureViewModelTests.BacklogReorderUsesVisiblePositionsWhenCompletedTasksAreInterleaved` |
| Independent Project and per-Project Task order persistence, rollback and pointer drag | `WorkspaceWorkTests.ProjectAndPerProjectTaskOrdersPersistIndependentlyAndRollBackOnFailure`, `ProjectCaptureWindowTests.ProjectAndAttachedTaskReorderMenusAreScopedKeyboardActionsAndRestoreFocus` |
| Schema-4 migration defaults, all order domains and schema-5 validation | `WorkspaceMigrationTests.OpenUpgradesReleasedSchemaFourWorkAsIncompleteWithoutChangingAnyOrder`, `WorkspaceWorkTests.InvalidPersistedCompletionPairIsRejectedOnOpenAndRead` |
| Derived desktop state, Backlog removal/reopen, dirty-draft guard and immediate-action independence | `ProjectCaptureViewModelTests.CompletionUpdatesDerivedProjectStateAndReopenRestoresBacklogWithoutChangingCapturedDate`, `CompletingDirtyBacklogTaskRequiresResolutionAndMovesFocusToNextRow`, `CompletionAndIndependentReordersPreserveDirtyInspectorDraft` |
| Completed day/week grouping, captured-calendar ordering, restart-visible data, guarded reopen and visible focus | `WorkspaceWorkTests.CompleteReopenAndRecompleteAtomicallyReplaceCapturedValuesAcrossTimeZones`, `ProjectCaptureViewModelTests.CompletedGroupsUseThreeDailyBucketsThenCalendarWeeks`, `CompletedGroupsUseCapturedCalendarOrderWhenUtcInstantsAreInverted`, `CompletedProjectionSurvivesReloadShowsCapturedDateAndReopensStandaloneTask`, `ReopeningDirtyTaskFromCompletedRequiresDraftResolution`, `ProjectCaptureWindowTests.CompletedProjectionShowsPersistedDateAndReopenMovesFocusToTheNavigationItem`, `ReopeningAttachedTaskFromCompletedFocusesTheNextVisibleCompletedToggle` |
| Relative, full and undated row dates, live local-date/time-zone refresh and empty-Project overdue semantics | `ProjectCaptureViewModelTests.RowsPresentRelativeFullAndUndatedDatesAndEmptyPastProjectAsOverdue`, `DatePresentationRefreshesAfterLocalMidnightAndTimeZoneChange` |
| Native toggle keyboard/state/name, captured date, disappearing-row focus and calendar interaction | `ProjectCaptureWindowTests.SharedCompletionToggleIsKeyboardOperableExposesStateAndShowsCapturedDate`, `CompletingDirtyBacklogRowRequiresDecisionThenFocusesRemainingCompletionControl`, `DateEditorRetainsManualEntryAndNativeCalendarKeyboardOpening` |
| Scoped reorder alternatives, announcements and focus restoration | `ProjectCaptureWindowTests.ProjectAndAttachedTaskReorderMenusAreScopedKeyboardActionsAndRestoreFocus` |
| Lost-release pointer cancellation | `ProjectCaptureWindowTests.NewPointerPressCancelsStaleProjectDragAndRemovesItsTargetHighlight` |
| Text-retaining status colours | `ProjectCaptureWindowTests.ProjectStatusRetainsTextAndUsesNeutralAmberAndGreenTreatments` |

Headless interaction checks establish bindings, keyboard paths, focus, names, checked state and live-region data. Final visual judgement and assistive-technology acceptance remain separate manual evidence.
