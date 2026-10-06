# Product brief

## Problem

Many personal task applications flatten projects into labels, make daily focus depend on dates, or impose a single ordering across every view. dot-orbit should let one person organise meaningful Projects and their Tasks while preserving a deliberately chosen Today list and several independent ways of looking at the same work.

## Intended user

One person managing their own work on a desktop. Collaboration, organisational administration, and assignee workflows are outside the initial scope.

## Product principles

- **Today first.** The application opens on the work deliberately selected for today.
- **Projects are real containers.** Tasks remain independently actionable without reducing Projects to tags.
- **Manual order is meaningful.** Drag order represents the user's current judgement; it is not disguised behind priority labels.
- **Views are projections, not duplicate records.** Today and Backlog show the same Tasks in a shared order.
- **Derived state stays derived.** Project progress, status, and completion date come from its Tasks.
- **Editing stays close to context.** Details are edited directly in the inspector rather than through modal forms.
- **Local and personal by default.** Local data and backups are encrypted. The application does not provide data synchronisation or depend on a cloud provider; a user may place encrypted backups in a folder managed by an external sync service.

## Initial scope

- Avalonia/.NET desktop application for macOS, Linux, and Windows, with user-selectable Dark and Light themes.
- Projects with nested Tasks, plus standalone Tasks for work that does not justify a Project.
- Categories with inheritance and Task overrides.
- Today, Upcoming, Backlog, Projects, Categories, Completed, and Archive views.
- Independent manual ordering scopes.
- Markdown descriptions with live rendering and rich-copy output.
- Optional Task participants without an assignee model.
- Searchable Archive.

## Explicit non-goals for the first useful release

- Teams, permissions, assignees, comments, or activity feeds.
- Real-time collaboration.
- Mobile-first layouts.
- AI task generation or automatic prioritisation.
- Recurring Tasks.
- Operating-system notifications and reminders.
- Calendar replacement, time tracking, or resource planning.
- Hosted service or account system.

## Planned extension

dot-orbit should later support tool-neutral, human-reviewed interchange for agent-assisted planning. A user can export current active work, ask an external tool to propose changes from material such as meeting notes, then import a structured change proposal for validation, preview, and explicit approval. Transcript ingestion, autonomous mutation, and provider-specific AI integration are not part of the first useful release.

## Success signal

The application is useful when the owner can capture work into Projects, arrange the Backlog, choose a focused Today subset, complete Tasks, and later find what was finished without maintaining the same information in several places.
