# Agent-interchange copy workflow

The implemented outbound version 1 workflow is manual and provider-neutral. dot-orbit prepares text for an external planning tool but does not select or call a provider, send data over the network, read existing clipboard content, or monitor the clipboard.

## Copy a current Planning snapshot

1. Unlock the workspace and open **Settings**.
2. Under **Agent interchange**, choose whether **Include agent instructions and schema with snapshot** is enabled. This device-local preference defaults enabled, shares the local `preferences.json` document with the theme preference, and never changes Workspace revision.
3. Activate **Review and copy…**. Any valid pending Project or Task inspector edit is saved first. Invalid or failed edits keep the current editing decision open and no snapshot is read until the edit is corrected, retried, or explicitly discarded.
4. Review the included record counts and relationships, excluded scope, reusable-catalogue disclosure, selected output form, and clipboard warning.
5. Activate **Copy Planning snapshot** to authorise that one clipboard write. **Do not copy**, window close, or navigation writes nothing.

The review retains one atomic revision-bearing snapshot. It does not silently refresh after opening. Every later copy starts a new review and requires a new confirmation.

The snapshot contains every reusable Category and Participant, active non-Bin Projects, and every non-archived/non-binned standalone or included-Project Task, including completed Tasks. It includes stable identifiers, relationships, Category inheritance or override, Participant associations, dates, completion, Today state, progress, and domain-significant order. Archived and Bin Project/Task content, recovery data, storage paths, export history, and persistence positions are excluded. A reusable Category or Participant label referenced only by excluded work is still present and may reveal hidden context.

When the preference is enabled, the clipboard receives one documented text payload containing provider-neutral role and constraints, the exact packaged Planning snapshot and Change proposal schemas, the operation vocabulary, output instructions, and the live snapshot. When disabled, it receives only valid pretty-printed Planning snapshot JSON; the combined document uses the same formatting for its snapshot portion. Snapshot identity remains the deterministic `sha256:` JCS/SHA-256 content identity; copy creates no random or persisted identifier.

## Copy reusable instructions without workspace content

Activate **Copy instructions** to copy the same provider-neutral role, constraints, exact packaged schemas, vocabulary, and output instructions without reading or including a live workspace snapshot. This path is suitable for preparing a reusable tool prompt, but dot-orbit still does not invoke or configure that tool.

## Limits and privacy

Each complete final output—snapshot JSON, reusable instructions/schemas, or their combined document—must be strict UTF-8 and no larger than 16 MiB. The check applies after composition. An over-limit output fails before clipboard access with fixed guidance; dot-orbit never truncates or omits stored values to fit.

Clipboard payloads are sensitive unencrypted plaintext. Payload content, labels, identifiers, digests, and clipboard values must not appear in logs, telemetry, exception messages, screenshots, test reports, CI output, or GitHub artifacts. Success, cancellation, unavailable clipboard, generation failure, and clipboard failure use fixed non-sensitive status text.

This workflow does not import or apply a Change proposal and does not replace the complete plaintext workspace export. Proposal typing/paste/file intake, Review, and explicit Apply are separate follow-up work under the same version 1 contract.
