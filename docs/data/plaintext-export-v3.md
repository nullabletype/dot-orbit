# Plaintext workspace export v3

Status: implemented.

dot-orbit can explicitly write a human-readable UTF-8 JSON copy of the unlocked workspace. This export is unencrypted, manual, and independent of encrypted recovery. The user previews its scope, chooses the destination, and confirms the plaintext output before any content is written. Bin contents are not exported.

## Identification and compatibility

The root object starts with:

```json
{
  "format": "dot-orbit-plaintext-export",
  "schemaVersion": 3
}
```

`schemaVersion` versions this public JSON contract independently of the encrypted SQLite workspace schema. Consumers must reject unsupported format or schema-version values rather than guessing at their meaning. Version 3 adds the required stable `colourKey` to each Project. Version 2 remains documented for consumers of older exports.

## Document shape

The remaining root properties appear in this order:

- `categories`: every Category as `id`, `name`, persisted `position`, and required named `colourKey`.
- `participants`: every reusable Participant label as `id` and `label`, including unused labels.
- `projects`: `active` and `archived` arrays. Each Project has `id`, `title`, Markdown `description`, `categoryId`, nullable ISO `targetDate`, persisted `position`, required named `colourKey`, `isArchived`, nullable UTC `archiveInstant`, and nullable captured local `archiveDate`.
- `tasks`: `active` and `archived` arrays. Each Task has `id`, nullable `projectId`, `title`, Markdown `description`, nullable `explicitCategoryId`, nullable ISO `dueDate`, `sharedPosition`, nullable `projectPosition`, `isComplete`, nullable UTC `completionInstant`, nullable captured local `completionDate`, ordered `participantIds`, nullable `todayLane`, `isArchived`, nullable UTC `archiveInstant`, and nullable captured local `archiveDate`.

`todayLane` is `planned`, `inProgress`, or `null`. Today has no separate order: its lane projections retain the shared Task order. Category, Project, shared Task, Project-local Task, and Participant-association order can therefore be reconstructed from the exported positions and arrays.

Dates use `YYYY-MM-DD`. Instants use round-trip ISO 8601 UTC text. Optional values are present as JSON `null` rather than being omitted.

## Determinism and scope

The same logical snapshot produces the same bytes. Categories and Projects are ordered by position then identifier, Tasks by shared position then identifier, and Participant records by identifier. The order of a Task's `participantIds` remains its stored association order. No export timestamp or filesystem path is embedded.

The source snapshot contains active and archived records but excludes individual Task and aggregate Project Bin records. Export never calls Bin read APIs, does not run from automatic-recovery scheduling, and does not use or suggest the configured recovery directory.

The completed document is first written to a uniquely named sibling temporary file in the explicitly selected directory, then moved over the selected destination. A write failure therefore leaves any existing destination unchanged. When replacing a file, the candidate inherits that destination's Unix mode or Windows ACL before any content is written. A new export is created for the current user only (`0600` on Unix and a protected current-user ACL on Windows). The temporary file is removed on handled completion or failure; an operating-system or process interruption can leave that unencrypted temporary file beside the chosen destination for the user to remove.
