# Desktop UI style guide

Status: implemented reference for the dark desktop Workbench direction.

This is the detailed authority for dot-orbit component anatomy, visual states, accessibility and Avalonia reuse. [`ui-specification.md`](ui-specification.md) remains the authority for product-level layout and interaction decisions; the historical HTML prototype remains exploratory evidence rather than a component specification.

## Principles

- Preserve a dense, calm desktop Workbench: a persistent navigation rail, primary canvas and contextual inspector.
- Pink communicates interaction: selection, focus, insertion and checked interactive controls. Green communicates completion or another positive outcome. Amber communicates attention or active progress. Error red communicates invalid input.
- Never communicate meaning with colour or an icon alone. Pair colour with text, shape, state, an accessible name, or a visible structural change.
- Use semantic resources and classes. A screen must not recreate hover, focus, checked, disabled, error or reorder rules locally.
- Keep the first release desktop-first and dark-only. Light theme, mobile and responsive redesign are separate product decisions.

## Tokens

The implemented tokens live in `src/DotOrbit.Desktop/Styles/Tokens.axaml`. Use the semantic name rather than copying its current value.

| Family | Tokens | Rule |
| --- | --- | --- |
| Colour | `BackgroundBrush`, `SidebarBrush`, `PanelBrush`, `PanelRaisedBrush`, `PanelInputBrush` | Near-black canvas with layered charcoal surfaces. Raised is an interaction state, not decoration. |
| Lines | `LineBrush`, `LineSoftBrush`, `ControlHoverBorderBrush` | Strong lines bound panels and controls; soft lines separate peer rows. |
| Text | `TextBrush`, `MutedTextBrush`, `QuietTextBrush` | Main copy, supporting copy and metadata respectively. Do not hide necessary information through low contrast. |
| Interaction | `AccentBrush`, `AccentHoverBrush`, `AccentPressedBrush`, `AccentSurfaceBrush`, `AccentContentBrush` | Pink family for action, selection, insertion, focus and checked interactive state. |
| State | `SuccessBrush`, `AmberBrush`, `ErrorBrush` | Positive/completed, attention/in-progress and validation/error. Always add a non-colour cue. |
| Type | `AppFont`, `TypeSizeEyebrow`, `TypeSizeBody`, `TypeSizeTitle` | Inter; 12px labels/metadata, 14px body, 20px inspector titles. Headings may step above these roles locally. |
| Spacing | `PanelInset`, `NavigationContentInset`, plus 4/6/8/10/12/20/24/32/44px increments | Use 10px equal list-panel insets and a 7px gutter between the navigation indicator and content. Add spacing between groups, not after a final group. |
| Scrolling | `ScrollContentInset`, `InspectorScrollContentInset` | Reserve a right gutter inside every canvas and inspector scroller so an overlay scrollbar remains separate from content at the 1120×600 minimum window size. Both also reserve bottom clearance after their final content. |
| Sizing | `ControlHitSize`, `ControlGlyphSize` | Compact actions retain a 30px hit target; completion uses one centred 22px visual square. |
| Radius | `PanelRadius`, `ControlRadius`, `CompactControlRadius` | 10px panels, 8px fields/actions and 6px compact controls. Rows remain visually quieter than panels. |
| Border and focus | `ControlBorder`, `FocusBorder` | 1px normal borders and a visible 2px keyboard-focus treatment. Focus must remain external to the completion glyph surface. |
| Density | 48px attached rows; 54px Backlog/Completed rows | Retain compact desktop density while preserving the control hit targets above. |

## Shared recipes

The implemented recipes live in `src/DotOrbit.Desktop/Styles/ComponentRecipes.axaml`, with the remaining global navigation, action, title, status and inspector recipes composed by `App.axaml`. `MainWindow.axaml` consumes them on Projects, attached Tasks, Backlog and Completed. New screens must reuse the same semantic classes before adding a new recipe.

### Panels and empty states

- `list-panel` supplies the shared surface, border, radius and equal 10px inset. Nested groups may add spacing between groups only.
- `empty-state` supplies the bounded transparent surface. It needs a plain-language heading, explanation and, when applicable, a keyboard-operable next action.
- A panel does not gain a raised background merely to create visual layers; reserve the raised surface for interaction or deliberate hierarchy.
- Canvas content uses `ScrollContentInset` and inspector content uses `InspectorScrollContentInset`; scrollbars must not cover text, fields, row actions or panel borders at the minimum window size.

### Rows and titles

- `interactive-row` owns full-row hover, including while the pointer is over its completion control, handle, disclosure, title, metadata or date. When that hover surface represents a selectable work item, its non-control area is also the activation target; embedded controls keep their own actions.
- `row-title` remains transparent and borderless on hover and press. Keyboard focus retains the visible pink border.
- `separated-row` uses a soft bottom rule only between peers. Add `last` to the final row of each independent list or Completed or Archive group. There is no trailing divider or trailing group margin.
- Task rows use one column contract across views: optional reorder handle, completion toggle, title plus Category, a right-aligned date block, view-specific actions or position, then the Today star as the final trailing action when available. Missing controls remove their column without changing the relative order of the remaining elements. Compact text actions compose `view-action` with `task-row-action` so Archive and Restore remain explicit without enlarging the row. Completed rows use the same date block for captured completion text and omit reorder, position, and Today actions.
- Settings lists use the same `list-panel`, `interactive-row`, `separated-row`, and `last` recipe as work lists. Global reusable-data actions belong there rather than in every contextual inspector; an active inline rename uses `inspector-field` plus the ordinary primary and secondary actions.
- Project and Category headers own hover independently of their expanded child regions and use the same disclosure and card geometry. Equal panel insets apply to Backlog, Completed, and Archive list panels. Completed and Archive use the same recent-day then calendar-week group headings and inter-group spacing.
- A Project header places its compact Archive action after target/status metadata. Archive Project rows omit active reorder and disclosure controls, include the textual `Archived Project` state, and end with the explicit Restore action; neither colour nor placement carries the state alone.
- The Completed bulk-archive panel uses an ordinary labelled ComboBox for the 1–30 day threshold, a polite live affected-count sentence, and one explicit action. Its confirmation names the affected count and states that Projects are not changed.
- Expanded Project Tasks use the same leading columns, two-line text rhythm, and vertically centred drag and completion controls as Backlog Tasks. The nested block has a small additional left inset so its handle and title sit to the right of the parent Project equivalents. Adjacent Project drag and disclosure controls retain a visible 4px gap. Task due and completion metadata occupies a right-aligned trailing block and uses 12px type, including “No date”; it does not move into the title block on different views. Category Project progress counts likewise occupy a right-aligned trailing column rather than following titles of different lengths.

### Reordering

- `drag-handle` renders one centred six-dot grid inside a 30px target. Scope-specific classes, commands, flyouts, names and help text stay at the caller.
- Every reorderable scope also offers Move up, Move down, Move to top and Move to bottom through a keyboard-accessible action menu.
- `reorder-target drag-target` renders the pink bottom insertion rule. Only a valid same-scope target may receive `drag-target`.
- A completed drop, invalid target, cancellation, pointer-capture loss or window deactivation clears every insertion rule. The current order remains announced after an accessible move.

### Completion and disclosure

- `completion-toggle` is a 30px hit target containing one centred 22px rounded square. It is transparent when idle, grey on unchecked hover, pink when checked, and darker pink on checked hover. It shows one plain tick with no inner surface.
- Focus is visible outside the 22px square: pink while unchecked and high-contrast light while checked. The accessible name changes between “Complete …” and “Reopen …”; checked state is exposed by the toggle role.
- `today-toggle` is a 30px trailing Task-row action with one centred 18px star and no button fill. A grey outlined star means “Add … to Today”; a solid amber star means “Remove … from Today”. The shape, checked state, and dynamic accessible name carry the meaning without relying on colour. It never becomes a separate row-selection target.
- `disclosure` uses the same compact geometry and explicit expanded/collapsed accessible name. Checked, hover and focus remain readable together.

### Status, badges, fields and feedback

- Status markers combine a dot or other shape with text. Neutral means not started, amber means in progress or attention, and green means complete or positive.
- Transient `Work.Message` action and validation feedback belongs in a content-sized, capped-width quiet capsule in the fixed top bar beside the brand. The neutral panel surface and border keep it subordinate to the work, while a small marker and explicit message preserve a non-colour cue. It is a non-interactive polite live region, uses at most two lines with character ellipsis for long messages, clears five seconds after the latest message, and never reserves space in the main content area when empty. Surface-specific feedback such as inspector autosave and Settings confirmations keeps its existing local presentation.
- `status-badge` is compact supplementary information, never the only announcement of important state.
- Inspector fields reuse `inspector-title` and `inspector-field`. The editable title keeps a modest horizontal inset so glyphs remain inside its focus border, without recreating the original pronounced indent. Date controls use the same field surface, use 12px input and calendar typography, and remain keyboard operable.
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
| Insertion rule | Absent | pink bottom rule only on valid target | N/A | cleared after drop/cancel/loss/deactivation | N/A | invalid targets remain absent |
| Status marker | neutral shape plus text | N/A | N/A | green completion or amber progress plus text | N/A | N/A |
| Transient status | quiet neutral capsule, small marker plus message | N/A | N/A | content-sized and visible for five seconds after the latest message | N/A | wording carries the failure or correction; colour is supplementary |
| Badge | accent surface plus short text | N/A unless actionable | Action owns focus if actionable | Count/state in text | Muted if unavailable | N/A |
| Inspector field | input surface and strong border | framework hover | visible focus border | Selected value remains textual | Framework-disabled | error border plus correction text |
| Markdown preview | input surface and sanitised rendered content | raised surface and strong border | pink 2px border | activation swaps to the source editor | copy is disabled when empty | unsafe content never becomes an active element |
| Date control | input surface and visible value | framework hover | keyboard calendar opening and visible focus | selected date in text | Framework-disabled | error border plus correction text |

Inspector date controls retain a fixed 150px width for empty, valid, and invalid input. Unparseable manual input remains visible, cannot be automatically saved as an empty date, and is identified at the field with correction text and accessible help.
| Empty state | heading and explanation | Action follows its button recipe | Action owns focus | N/A | N/A | N/A |
| Validation feedback | Absent for valid input | N/A | Invalid field remains focusable | N/A | N/A | error border, explicit text and accessible relationship |

## Local reference surface

Launch the deterministic reference window from the repository root:

```sh
dotnet run --project src/DotOrbit.Desktop/DotOrbit.Desktop.csproj -- --style-guide
```

The window is not a production navigation destination, does not open a workspace and contains synthetic content only. It consumes the production tokens and recipes; it must not define preview-only hover or focus rules. Inspect it at 1180×760 and exercise hover, press, Tab focus, checked, expanded, disabled, insertion and validation examples. Then inspect the changed production surfaces. Automated interaction checks guard geometry and state wiring, but human rendered and assistive-technology judgement remain acceptance requirements.
