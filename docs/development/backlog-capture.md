# Standalone Task capture and shared Backlog ordering

Issue #8 extends the persisted work slice with standalone Tasks and the first editable Backlog. A standalone Task has no Project membership or Project position and must carry an explicit Category. The Backlog inspector keeps creation transient until Create succeeds; Cancel leaves no Task behind. Standalone Tasks never appear beneath Projects.

Backlog quick add pairs a required Category selector with a title field. Enter or Tab on a non-empty title creates one title-only standalone Task and returns focus to a fresh field. Escape clears unsubmitted text and empty Tab follows normal focus navigation. The selected Category is retained for sequential entries in the current Backlog visit, then cleared when the user leaves that view. It is never persisted as a hidden default.

Attached and standalone incomplete Tasks use the same persisted order. Every new Task enters at the top. Each Backlog row provides a visible drag handle that opens an accessible action menu for Move up, Move down, Move to top, and Move to bottom. Pointer drag and all four menu actions call the same transactional storage operation. A successful menu move returns focus to that Task's reorder trigger and announces the Task's new one-based position, total count, and Backlog scope through a polite live region. Selecting anywhere on the remaining row surface opens that Task in the inspector.

Schema version 4 makes Task Project membership and Project position nullable together, requires an explicit Category when membership is absent, and stores a dense non-negative shared position. Migration from released schema 3 preserves attached membership, Project-local positions, effective Category behaviour, and relative shared order. Migration remains protected by the existing validated encrypted recovery-point path.

This slice does not add automatic priority, Today lanes, Category inheritance changes, multiline batch creation, completion, or archive behaviour. It adds no dependency.

## Verification map

| Behaviour | Automated evidence |
| --- | --- |
| Transient standalone draft, required Category, and Project exclusion | `ProjectCaptureViewModelTests.StandaloneTaskDraftRequiresExplicitCategoryAndNeverAppearsUnderAProject` |
| Session-only Backlog quick-add Category | `ProjectCaptureViewModelTests.BacklogQuickAddRetainsCategoryOnlyUntilTheEntrySessionEnds` |
| Enter, non-empty Tab, Escape, empty Tab, retained focus, and explicit Category | `ProjectCaptureWindowTests.BacklogRapidEntryRequiresCategoryRetainsItAndImplementsTheKeyboardFlow` |
| All four accessible move actions and Backlog announcement | `ProjectCaptureViewModelTests.AllAccessibleMoveCommandsUseTheSharedOrderAndAnnouncePositionAndScope` |
| Focus preservation and pointer drag through the real Avalonia surface | `ProjectCaptureWindowTests.BacklogAccessibleMovePreservesFocusAnnouncesPositionAndPointerDragUsesTheSameOrder` |
| Full-row selection and hover, balanced list padding, accessible Category relationship, centred reorder affordance, final-row edge, and unclipped inspector metadata | `ProjectCaptureWindowTests.BacklogRowSurfaceSelectsTask`, `BacklogHoverHighlightsTheWholeSelectableRowSurface`, `BacklogListPanelUsesEqualOuterPadding`, `BacklogRowsExposeEffectiveCategoryAndRelationshipAsAccessibleText`, `BacklogReorderHandleUsesCentredDotGrid`, `LastBacklogRowHasNoTrailingDivider`, and `TaskInspectorMetadataLabelFitsItsColumn` |
| Standalone persistence, top insertion, atomic move rollback, and restart | `WorkspaceWorkTests.StandaloneCreationAndAtomicSharedReorderPersistAcrossRestart` |
| Released schema-3 migration | `WorkspaceMigrationTests.OpenUpgradesReleasedSchemaThreeTasksWithoutChangingMembershipOrRelativeOrder` |

Automated headless checks establish bindings, focus, accessible names, live-region data, and pointer behaviour. Final visual judgement and assistive-technology acceptance remain separate manual work.
