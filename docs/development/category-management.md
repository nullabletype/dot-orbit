# Category management and Task context changes

Issue #10 turns Categories into an ordered working view. Categories appear in their persisted manual order. Each Category shows two separate sections: Projects retain the global Projects order, and standalone Tasks retain the shared Backlog order. The view adds no Category-local Project or Task ordering scope.

Categories is an inclusive view of active work rather than another incomplete-work queue. Completing an active standalone Task does not remove it from its Category; Backlog is the view that filters to incomplete Tasks. Binned work remains excluded, and Archive has its own projection.

Category names are trimmed, required, and unique case-insensitively. New Categories append to the Category order; rename retains position. An unreferenced Category can be deleted directly, but the final Category cannot be removed. Deleting a referenced Category requires a distinct replacement. Project Categories, standalone Task Categories, and attached Task overrides are reassigned with the delete in one encrypted SQLite transaction. A failed replacement leaves every reference and Category intact.

Category creation and editing use the same contextual inspector draft lifecycle as Projects and Tasks. The Categories canvas contains selection and ordering actions, not inline text fields or a separate editing convention. Category-contained Projects and standalone Tasks reuse the shared list-row recipes and retain a visible gutter between titles and their progress or date metadata. Every implemented canvas scroller and the inspector reserve an internal right gutter so vertical scrollbars do not cover content at the minimum supported window size.

Task context changes are immediate actions, separate from inspector drafts. Detaching an attached Task materialises its current effective Category, removes its Project position, and compacts the source Project order. Attaching to a Project with the same Category switches to inheritance automatically. If the effective Task Category and destination Project Category differ, the application requires an explicit choice to preserve the Task Category as an override or adopt the Project Category. Moving between Projects uses the same rule, compacts the source order, and appends to the destination order. Every context change retains the Task's shared Backlog position.

The Categories reorder handle supports pointer drag and the keyboard-operable Move up, Move down, Move to top, and Move to bottom menu. A successful accessible move restores focus and announces the Category's new position and scope. Category-contained Project and standalone Task rows expose selection and Category relationship metadata without offering a false Category-local reorder action.

Schema version 5 already represents ordered Categories, nullable Project membership, inherited Categories, and explicit overrides, so this slice does not change the schema or add a dependency.

## Acceptance evidence

| Behaviour | Automated evidence |
| --- | --- |
| Category creation, rename, case-insensitive uniqueness, manual order, unreferenced deletion, and final-Category refusal | `WorkspaceWorkTests.CategoriesAreCreatedRenamedAndReorderedWithCaseInsensitiveUniqueNames` |
| Referenced replacement across Projects, standalone Tasks, and overrides, including failure rollback | `WorkspaceWorkTests.ReferencedCategoryDeletionReassignsEveryReferenceAtomicallyAndRollsBackOnFailure` |
| Detach, same-Category inheritance, explicit preserve/adopt choice, cross-Project movement, both ordering scopes, restart persistence, and move rollback | `WorkspaceWorkTests.AttachDetachAndCrossProjectMovePreserveSharedOrderAndMaintainDenseProjectOrders`, `CrossProjectMoveFailureRollsBackMembershipAndBothOrders` |
| Category projection and management workflow | `ProjectCaptureViewModelTests.CategoryProjectionPreservesGlobalOrdersAndManagementUsesAccessibleCategoryScope` |
| Completed active standalone Tasks remain in their Category | `ProjectCaptureViewModelTests.CategoriesKeepCompletedActiveStandaloneTasks` |
| Task-context decision workflow | `ProjectCaptureViewModelTests.TaskContextActionsDetachMatchAutomaticallyAndRequireAnExplicitMismatchChoice` |
| Grouped Categories surface, accessible relationship metadata, reorder menu, restored focus, and announcement | `ProjectCaptureWindowTests.CategoriesViewGroupsExistingOrdersAndOffersAccessibleCategoryReorder` |
| Shared list-row presentation and Category create/edit through the contextual inspector | `ProjectCaptureWindowTests.CategoriesUseSharedListRowsAndContextualInspectorEditing` |
| Category Project progress counts share a right edge; inspector dates keep fixed geometry and reject invalid manual drafts with field feedback | `ProjectCaptureWindowTests.CategoriesUseSharedListRowsAndContextualInspectorEditing`, `ProjectCaptureWindowTests.DateEditorKeepsFixedWidthAndRejectsInvalidDraftWithFieldFeedback` |
| Projects, Backlog, Categories, Completed, and inspector scrollbar gutters at minimum window size | `ProjectCaptureWindowTests.CanvasAndInspectorScrollbarsReserveAGutterAtMinimumWindowSize` |
| Inspector title containment, bottom action clearance, compact Category handle/title spacing, 12px date-picker typography, and Backlog-aligned expanded Project Tasks with consistent date metadata | `ProjectCaptureWindowTests.InspectorTitleTextStaysInsideItsFocusBorderAcrossWorkContexts`, `InspectorActionsClearTheViewportBottomAtMaximumScroll`, `CategoriesUseSharedListRowsAndContextualInspectorEditing`, `DateEditorRetainsManualEntryAndNativeCalendarKeyboardOpening`, `ProjectHeaderHoverFillsOnlyTheHeaderAndKeepsItsTitleTransparent`, `ExpandedProjectTaskUsesBacklogControlAndTextAlignmentWithProjectDateTypography` |
| Untouched existing and blank-create inspectors remain clean across date-picker focus, all inspector controls, and Project selection; stale bound-editor writes during inspector load or invalid-date discard cannot create a false draft | `ProjectCaptureWindowTests.LeavingAnUnchangedDatedInspectorDoesNotRequestDraftResolution`, `LeavingAnUnchangedTaskTitleDoesNotRequestDraftResolution`, `ClickingAmongUnchangedInspectorsNeverRequestsDraftResolution`, `LeavingAnUntouchedCreateDraftDoesNotRequestDraftResolution`, `DiscardingInvalidDateCannotDirtyTheNextInspectorThroughAQueuedRestore`, `ProjectCaptureViewModelTests.LoadingDraftIgnoresReentrantWritesFromThePreviouslyBoundTitleEditor` |
| Replacement decision and focus | `ProjectCaptureWindowTests.ReferencedCategoryDeletionRequiresAReplacementAndMovesFocusIntoTheDecision` |
| Mismatch preserve/adopt decision and detach through the real Avalonia surface | `ProjectCaptureWindowTests.TaskContextMismatchOffersPreserveOrAdoptBeforeMovingAndDetachMaterialisesCategory` |

Rendered desktop review remains required before publication because this slice changes the Categories and inspector user interfaces.
