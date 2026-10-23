# Desktop UI style guide

Status: implemented reference for the desktop Workbench direction across all built-in colour themes.

This is the detailed authority for dot-orbit component anatomy, visual states, accessibility and Avalonia reuse. [`ui-specification.md`](ui-specification.md) remains the authority for product-level layout and interaction decisions; the historical HTML prototype remains exploratory evidence rather than a component specification.

## Principles

- Preserve a dense, calm desktop Workbench: a persistent navigation rail, primary canvas and contextual inspector.
- The theme accent communicates interaction: selection, focus, insertion and checked interactive controls. The exact hue may vary by theme. Success, attention, archived and error tokens communicate their named state, with a non-colour cue alongside them.
- Never communicate meaning with colour or an icon alone. Pair colour with text, shape, state, an accessible name, or a visible structural change.
- Use semantic resources and classes. A screen must not recreate hover, focus, checked, disabled, error or reorder rules locally.
- Keep the first release desktop-first. Dark is the initial theme; Settings offers the dot-orbit Dark and Light themes plus the named Monokai Classic, Dracula Classic, Alucard Classic, Catppuccin Mocha, Catppuccin Latte, Gruvbox Dark, and Gruvbox Light themes without changing the layout or semantic hierarchy. Mobile and responsive redesign remain separate product decisions.

## Tokens

The implemented tokens live in `src/DotOrbit.Desktop/Styles/Tokens.axaml`. Use the semantic name rather than copying its current value.

| Family | Tokens | Rule |
| --- | --- | --- |
| Colour | `BackgroundBrush`, `SidebarBrush`, `PanelBrush`, `PanelRaisedBrush`, `PanelInputBrush` | Theme-specific canvas and layered surfaces. Raised is an interaction state, not decoration. |
| Lines | `LineBrush`, `LineSoftBrush`, `ControlHoverBorderBrush` | Strong lines bound panels and controls; soft lines separate peer rows. |
| Text | `TextBrush`, `MutedTextBrush`, `QuietTextBrush` | Main copy, supporting copy and metadata respectively. Do not hide necessary information through low contrast. |
| Interaction | `AccentBrush`, `AccentHoverBrush`, `AccentPressedBrush`, `AccentSurfaceBrush`, `AccentContentBrush` | Theme-specific accent family for action, selection, insertion, focus and checked interactive state. |
| State | `SuccessBrush`, `AmberBrush`, `ArchivedBrush`, `ErrorBrush` | Positive/completed, attention/in-progress, archived and validation/error. Always add a non-colour cue. |
| Work identity | `CategoryOrchidBrush`, `CategoryVioletBrush`, `CategoryIndigoBrush`, `CategoryOceanBrush`, `CategoryTealBrush`, `CategoryLimeBrush`, `CategoryTangerineBrush`, `CategoryRoseBrush`, `CategoryCobaltBrush`, `CategoryCyanBrush`, `CategoryEmeraldBrush`, `CategoryGoldBrush`, `CategoryAmberBrush`, `CategoryCoralBrush`, `CategoryMagentaBrush`, `CategorySlateBrush` | Category and Project identification only. Persist the corresponding stable lowercase key; never reuse these brushes as interaction or workflow state. |
| Type | `AppFont`, `TypeSizeEyebrow`, `TypeSizeBody`, `TypeSizeTitle` | Inter; 12px labels/metadata, 14px body, 20px inspector titles. Headings may step above these roles locally. |
| Spacing | `PanelInset`, `NavigationContentInset`, plus 4/6/8/10/12/20/24/32/44px increments | Use 10px list-panel content insets; row lists put horizontal insets inside each row so hover reaches the panel edges. Keep a 7px gutter between the navigation indicator and content. Add spacing between groups, not after a final group. |
| Scrolling | `ScrollContentInset`, `InspectorScrollContentInset` | Reserve a right gutter inside every canvas and inspector scroller so an overlay scrollbar remains separate from content at the 1120×600 minimum window size. Both also reserve bottom clearance after their final content. |
| Sizing | `ControlHitSize`, `ControlGlyphSize`, `WorkTypeIconSlotWidth`, `WorkTypeIconGlyphSize`, `WorkTypeIconOpticalOffset` | Compact row actions retain a 30px hit target; completion uses one centred 22px visual square; work titles use a compact 14px inline slot for a 13px glyph with a shared 1px optical baseline correction. Inline Task identity segments are the deliberate exception: each owns its complete visible segment inside the required 16px joined pill so row density and title-column boundaries do not change. |
| Radius | `PanelRadius`, `ControlRadius`, `CompactControlRadius` | 10px panels, 8px fields/actions and 6px compact controls. Rows remain visually quieter than panels. |
| Border and focus | `ControlBorder`, `FocusBorder` | 1px normal borders and a visible 2px keyboard-focus treatment. Focus must remain external to the completion glyph surface. |
| Density | 48px attached rows; 54px Backlog/Completed rows | Retain compact desktop density while preserving the control hit targets above. |

## Shared recipes

The implemented recipes live in `src/DotOrbit.Desktop/Styles/ComponentRecipes.axaml`, with the remaining global navigation, action, title, status and inspector recipes composed by `App.axaml`. `MainWindow.axaml` consumes them on Projects, attached Tasks, Backlog and Completed. New screens must reuse the same semantic classes before adding a new recipe.

### Panels and empty states

- `list-panel` supplies the shared surface, border, radius and equal 10px content inset. Add `row-list` to panels containing interactive rows: it removes panel padding and clips row fills to the rounded panel geometry, including its corners. Rows own their horizontal content padding; non-row headings and controls retain their own insets. Nested groups may add spacing between groups only.
- `attention-panel` supplies the shared bounded surface for warnings and consequential confirmations. It uses the amber semantic token, but its text must carry the meaning without relying on colour.
- `empty-state` supplies the bounded transparent surface. It needs a plain-language heading, explanation and, when applicable, a keyboard-operable next action.
- A panel does not gain a raised background merely to create visual layers; reserve the raised surface for interaction or deliberate hierarchy.
- Canvas content uses `ScrollContentInset` and inspector content uses `InspectorScrollContentInset`; scrollbars must not cover text, fields, row actions or panel borders at the minimum window size.

### Rows and titles

- Every user-visible Task, Project, and Category identity places one shared work-type glyph immediately before its title on the same line. Tasks use the neutral outlined task card; Projects use their coloured stacked work cards; Categories use their coloured grid. The glyphs apply their type-specific optical size so their painted centre aligns with the visible title glyphs rather than the text line box. They remain non-interactive and stay out of the accessibility tree. The containing row supplies the meaningful accessible identity.
- `CategoryIdentityMarker` is the decorative, non-focusable coloured Category grid plus full Category name used in Category headings and choices. Project navigation, headings, metadata, and identity controls use the same stacked-work-cards glyph, scaled to the available slot; Project rows place that coloured glyph once beside the title rather than repeating a neutral copy on the secondary line. `CategoryProjectPill` is the shared raised compact Task-context recipe: coloured Category grid and name first, a one-pixel angled internal boundary centred between the segments, then coloured Project cards and name. Standalone Tasks omit the boundary and Project segment. On live Task surfaces each segment is a compact native button; Category precedes Project in focus order, and the angled join belongs to the Project target. Decorative uses remain non-hit-testable and outside the accessibility tree.
- Today, Upcoming, Backlog, expanded Project Tasks, standalone Tasks under Categories, Completed, Archive timeline and search, and Bin place `CategoryProjectPill` on the Task secondary line. Each live segment uses `Open Category <name>` or `Open Project <name>`, a stable `task-category-<id>` or `task-project-<id>` automation identifier, a visible pink keyboard-focus edge, and independent hover/press feedback. Segment activation must not invoke Task or row selection. Category activation selects Categories and scrolls and focuses `category-selection-<id>`; active Project activation does the same in Projects at `project-selection-<id>`. The complete target row aligns to the top of its viewport whenever the remaining extent permits, leaving its frame, inset heading, and content visible; near the end, scrolling clamps naturally. Archived Project activation selects Archive and targets its archived row because archived work is absent from Projects. The normal workspace save/discard/stay/retry boundary applies before changing the main view. A binned parent Project leaves the segment visible but framework-disabled, skipped during Tab navigation, and exposes restore-first help. The pill stays aligned with the Task glyph edge, uses the title button's lower inset as the visual 6px title gap, and retains 6px bottom clearance above the row divider. Archive and Bin Project rows keep the coloured Project glyph beside the title and collapse the secondary pill to Category only. The existing fork geometry sits in the Category segment only for an attached Task with an explicit override. Long names use character ellipsis within the title column; full Category and relationship text remains in the row accessible name.
- Identity secondary lines describe relationships rather than repeat the work type: attached Tasks start with their Project, standalone Tasks use `Standalone`, Projects show only the Category marker because their Task progress is already right-aligned, and Categories use `N Projects · N standalone Tasks`. The row accessible name explicitly distinguishes inherited Category, Category override, and standalone Category relationships. A Category override uses a branch glyph without an arrowhead plus the visible text `Category override`; it must not resemble navigation or Task movement.
- `interactive-row` owns a full-width hover surface, including while the pointer is over its completion control, handle, disclosure, title, metadata or date. Its 10px horizontal padding keeps content inset without insetting the hover background. When that hover surface represents a selectable work item, its non-control area is also the activation target; embedded controls keep their own actions.
- `row-title` remains transparent and borderless on hover and press. Keyboard focus retains the visible pink border.
- `separated-row` uses a soft bottom rule only between peers. Add `last` to the final row of each independent list or Completed or Archive group. There is no trailing divider or trailing group margin.
- Task rows use one column contract across views: optional reorder handle, completion toggle, icon plus identity and relationship context, a right-aligned date block, view-specific actions or position, then the Today star as the final trailing action when available. Missing controls remove their column without changing the relative order of the remaining elements. Compact text actions compose `view-action` with `task-row-action` so Archive and Restore remain explicit without enlarging the row. Completed rows use the same date block for captured completion text and omit reorder, position, and Today actions.
- Settings lists use the same `list-panel row-list`, `interactive-row`, `separated-row`, and `last` recipe as work lists. Global reusable-data actions belong there rather than in every contextual inspector; an active inline rename uses `inspector-field` plus the ordinary primary and secondary actions.
- Project and Category headers own hover independently of their expanded child regions and use the same disclosure and card geometry. Backlog and Completed put their horizontal content insets inside full-width rows, without a blank outer strip above or below the list. Archive and Bin retain zero left row padding so their identity glyphs sit within 8px of the visible panel edge, with 10px right content padding. Expanded Project and Category child rows also extend to their card edges; Project Task content retains its additional left indent. Completed and Archive use the same recent-day then calendar-week group headings, one list panel per group, and inter-group spacing.
- A Project header places its compact Archive action immediately after fixed progress and target/state columns. Target date is the first line; the second line contains exactly one current state when needed: amber `⚠ Overdue` or green `Completed <date>`. It omits the redundant status word: the completed/total count stays neutral when not started, amber while in progress and green when complete, while the accessible name retains the textual status. Archive Project rows keep title and Category on two vertically centred lines. Their status/task-count summary and target date occupy the trailing metadata column used by Task completion dates; Project and Task content are mutually exclusive there. Archive Project rows omit active reorder and disclosure controls, include the textual `Archived Project` state, and end with the explicit Restore action; neither colour nor placement carries the state alone.
- The Completed bulk-archive panel uses an ordinary labelled ComboBox for the 1–30 day threshold, a polite live affected-count sentence, and one explicit action. Its confirmation names the affected count and states that Projects are not changed.
- Expanded Project Tasks use the same leading columns, two-line text rhythm, and vertically centred drag and completion controls as Backlog Tasks. The nested block has a small additional left inset so its handle and title sit to the right of the parent Project equivalents. Adjacent Project drag and disclosure controls retain a visible 4px gap. Task due and current-state metadata occupies one fixed right-aligned trailing block and uses 12px type, including “No date”; the second line contains exactly one state, with archival taking precedence over completion and completion taking precedence over overdue: violet `Archived <date>`, green `Completed <date>`, or amber `⚠ Overdue`. Restore and Today use the compact adjacent fixed action lane rather than the far edge of a wide empty column. The metadata does not move into the title block on different views. Category Project progress counts likewise occupy a right-aligned trailing column rather than following titles of different lengths.

### Reordering

- `drag-handle` renders one centred six-dot grid inside a 30px target. Scope-specific classes, commands, flyouts, names and help text stay at the caller.
- Every reorderable scope also offers Move up, Move down, Move to top and Move to bottom through a keyboard-accessible action menu.
- `reorder-host` opens a full `reorder-placeholder` before or after its `reorder-target`. The placeholder uses `AccentSurfaceBrush`, an accent outline, and a readable `TextBrush` label identifying the dragged item on a trimmed first line and its destination position on a separate always-visible second line. Its height matches the source Task row, or just the heading for an expanded Project or Category. A `drag-source` retains its original footprint with reduced opacity. Only a valid same-scope target receives `drag-target`; normal row/card borders stay unchanged.
- The upper and lower halves select before and after the target. At the first and last boundaries the placeholder appears above the first row or below the complete last row/card. The entire opened gap retains its selected boundary until the pointer leaves it, so opening a gap cannot flip the destination beneath a stationary pointer. Preview never changes persisted order.
- A completed drop, invalid target, Escape, pointer-capture loss, window deactivation, viewport scroll or window resize clears the placeholder. Scrolling cancels the current drag; this slice does not add drag auto-scroll. The current order remains announced after an accessible move.

### Completion and disclosure

- `completion-toggle` is a 30px hit target containing one centred 22px rounded square. It is transparent when idle, grey on unchecked hover, pink when checked, and darker pink on checked hover. It shows one plain tick with no inner surface.
- Focus is visible outside the 22px square: pink while unchecked and high-contrast light while checked. The accessible name changes between “Complete …” and “Reopen …”; checked state is exposed by the toggle role.
- `today-toggle` is a 30px trailing Task-row action with one centred 18px star and no button fill. A grey outlined star means “Add … to Today”; a solid amber star means “Remove … from Today”. The shape, checked state, and dynamic accessible name carry the meaning without relying on colour. It never becomes a separate row-selection target.
- `disclosure` uses the same compact geometry and explicit expanded/collapsed accessible name. Checked, hover and focus remain readable together.

### Status, badges, fields and feedback

- Status markers combine a dot or other shape with text. Neutral means not started, amber means in progress or attention, and green means complete or positive.
- `Work.Message` action and validation feedback belongs in a content-sized, capped-width quiet capsule in the fixed top bar beside the brand. The neutral panel surface and border keep it subordinate to the work, while a small marker and explicit message preserve a non-colour cue. It is a polite live region, uses at most two lines with character ellipsis for long messages, and never reserves space in the main content area when empty. Ordinary completed feedback clears five seconds after the latest message. Queued persistence and committed-refresh failure remain visible until settled; only the latter adds the compact, keyboard-operable **Retry refresh** action with an explicit accessible name.
- `status-badge` is compact supplementary information, never the only announcement of important state.
- Inspector fields reuse `inspector-title` and `inspector-field`. The editable title keeps a modest horizontal inset so glyphs remain inside its focus border, without recreating the original pronounced indent. Date controls use the same field surface, use 12px input and calendar typography, and remain keyboard operable.
- Inspector duplication uses the ordinary neutral `view-action` recipe under an **ACTIONS** eyebrow. Project duplication uses the existing `decision-overlay`, `inspector-field`, primary confirmation, secondary cancellation, nearby validation text, initial title focus, Escape cancellation, and return focus; it does not introduce a bespoke modal or warning colour.
- Validation uses an error border plus nearby `validation-message` text that states the correction. Do not encode “invalid” only in red.

### Markdown preview

- `markdown-preview` is a full-width keyboard-operable edit surface with button semantics. It renders the sanitised model, never raw Markdown or remote image content, and uses the ordinary interaction colours and focus border. Native hyperlinks inside it own their pointer and keyboard activation and never also activate editing.
- Activating the preview switches that inspector section to its multiline source field. Moving focus away or clicking anywhere outside the editor returns to the rendered state without saving the inspector draft or stealing focus from the next control; **Copy rendered** remains a separate explicit action.
- Empty Markdown shows an instructional placeholder rather than an empty target. Approved `http`, `https`, and `mailto` links use a keyboard-focusable accent hyperlink; fragments and rejected destinations remain inert. Code uses monospace text, headings use font-derived line metrics so ascenders and descenders remain visible, and ordered, unordered, lower-alpha, and mixed lists retain visible indentation through at least four levels. Remote images appear only as explicit **Remote image not loaded** placeholders. Semantic block changes receive visible vertical space; consecutive items within one list remain compact. Repeated empty source lines follow normal Markdown collapsing rules; use an `&nbsp;`-only paragraph when an intentional extra spacer is needed.
- In the source editor, Tab and Shift+Tab indent or outdent the current line by one four-space Markdown level. Indenting a numeric or lower-alpha ordered item converts it to an unordered sub-point; unordered markers and plain text keep their type. Enter continues the resulting marker at the same indentation. Control+Tab and Control+Shift+Tab move focus out of the editor.

## Component-state matrix

“N/A” means that the state does not apply to that component; do not invent a cosmetic state solely to fill the matrix.

| Component | Idle | Hover / pressed | Focus | Checked / expanded | Disabled | Error |
| --- | --- | --- | --- | --- | --- | --- |
| Navigation item | Muted text, transparent | Panel hover | Pink visible border | Pink indicator, accent surface and text | Framework-disabled plus accessible disabled state | N/A |
| Panel | Panel surface and strong boundary | N/A | N/A | N/A | N/A | N/A |
| Interactive row | Transparent | Full-row raised surface | Child action owns focus | N/A | Actions expose disabled state | N/A |
| Row title | Transparent and borderless | Remains transparent on hover/press | Pink border | N/A | Framework-disabled | N/A |
| Secondary button | Transparent, strong border | Raised surface | Pink 2px border | N/A | Framework-disabled | N/A |
| Primary button | Pink surface | lighter pink / platform press feedback | contrasting 2px border | N/A | Framework-disabled | N/A |
| Completion toggle | 30px target, transparent 22px square | grey unchecked hover | external pink ring | pink square and plain tick; darker pink hover; light focus ring | Framework-disabled with name retained | N/A |
| Today toggle | 30px target with centred grey outlined star and no fill | stronger outline, still no fill | pink border | centred solid amber star with no background, plus checked state and “Remove …” name | Framework-disabled with name retained | N/A |
| Disclosure | Quiet glyph | raised surface and strong border | pink border | readable expanded glyph and accessible name | Framework-disabled | N/A |
| Reorder handle | centred six dots | raised surface and strong border | pink border | N/A | Framework-disabled | N/A |
| Insertion placeholder | Absent | labelled row-height gap with an accent outline only on valid target | N/A | cleared after drop/cancel/loss/deactivation | N/A | invalid targets remain absent |
| Status marker | neutral shape plus text | N/A | N/A | green completion or amber progress plus text | N/A | N/A |
| Category marker / pill | named coloured Category grid plus visible Category name; pill adds raised bounded surface | live Task segments receive local hover/press feedback without selecting the row; decorative uses remain inert | pink 2px inset edge, Category before Project | fork inside pill only for explicit Task override, with textual relationship on containing row | unavailable parent Project is disabled, skipped by Tab, and retains restore-first help | invalid persisted keys fail workspace validation |
| Transient status | quiet neutral capsule, small marker plus message | Retry refresh uses the secondary-button recipe only after a committed refresh failure | Retry action owns focus | ordinary feedback is visible for five seconds; pending and refresh-failed states persist until resolved | N/A | wording distinguishes mutation failure from committed-refresh failure; colour is supplementary |
| Badge | accent surface plus short text | N/A unless actionable | Action owns focus if actionable | Count/state in text | Muted if unavailable | N/A |
| Inspector field | input surface and strong border | framework hover | visible focus border | Selected value remains textual | Framework-disabled | error border plus correction text |
| Markdown preview | input surface and sanitised rendered content | raised surface and strong border | pink 2px border | activation swaps to the source editor | copy is disabled when empty | unsafe content never becomes an active element |
| Date control | input surface and visible value | framework hover | keyboard calendar opening and visible focus | selected date in text; native today highlight remains distinct from selection | Framework-disabled | error border plus correction text |

Inspector date controls retain a fixed 150px width for empty, valid, and invalid input. The shared Project and Task calendar keeps the native Avalonia template and today state: dated fields open on the selected date's month, while undated fields open on the current local month with today highlighted but no selected or textual value. The native highlight does not commit data. Unparseable manual input remains visible, cannot be automatically saved as an empty date, and is identified at the field with correction text and accessible help.
| Empty state | heading and explanation | Action follows its button recipe | Action owns focus | N/A | N/A | N/A |
| Validation feedback | Absent for valid input | N/A | Invalid field remains focusable | N/A | N/A | error border, explicit text and accessible relationship |

## Local reference surface

Launch the deterministic reference window from the repository root:

```sh
dotnet run --project src/DotOrbit.Desktop/DotOrbit.Desktop.csproj -- --style-guide
```

The window is not a production navigation destination, does not open a workspace and contains synthetic content only. It consumes the production tokens and recipes; it must not define preview-only hover or focus rules. Inspect it at 1180×760 and exercise hover, press, Tab focus, checked, expanded, disabled, insertion and validation examples. Then inspect the changed production surfaces. Automated interaction checks guard geometry and state wiring, but human rendered and assistive-technology judgement remain acceptance requirements.
