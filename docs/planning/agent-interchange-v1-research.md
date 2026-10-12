# Agent interchange v1: JSON, schema, canonicalisation, and untrusted-content research

Research date: 2026-10-12

Issue: [#76 — Resolve the agent-interchange v1 contract and review boundary](https://github.com/nullabletype/dot-orbit/issues/76)

## Scope and terminology

This note covers the externally governed parts of the v1 contract: JSON parsing, JSON Schema dialect and references, offline validation, exact-proposal binding, number interoperability, resource limits, and untrusted JSON/Markdown metadata. It does not choose the product's snapshot contents, operation vocabulary, approval granularity, or numeric resource budgets.

In this note:

- **Standards requirement** means a requirement made by the cited specification, within that specification's scope.
- **dot-orbit recommendation** means a proposed v1 product or implementation rule. These recommendations are not requirements imposed by JSON, JSON Schema, CommonMark, or a digest standard.
- **Schema document** means a JSON Schema authored and shipped by dot-orbit. **Instance document** means a snapshot or proposal validated against one of those schemas.

## Conclusions

The externally supported direction for v1 is:

1. Use strict RFC 8259 JSON encoded as UTF-8, with no comments, trailing commas, byte-order mark, non-finite numbers, or duplicate decoded member names. Reject duplicate names before building an object model or running JSON Schema validation.
2. Use the released JSON Schema 2020-12 dialect. As of 2026-10-12, the JSON Schema project still identifies 2020-12 as its current released version, while the IETF JSON Schema work is an Internet-Draft and is not yet a published standard. Every shipped schema should declare `"$schema": "https://json-schema.org/draft/2020-12/schema"` and an immutable, versioned, absolute `$id`.
3. Package every schema and all transitively referenced meta-schema resources needed by the application. Resolve only exact, allowlisted identifiers from an in-memory/local registry. Never dereference a schema URI through HTTP, another network protocol, or the filesystem while importing an instance.
4. Reject unknown instance properties. Prefer fragment-only references into `$defs`; if schemas are split, resolve only exact absolute IDs that are present in the bundled registry.
5. Constrain all JSON numbers to exactly interoperable integers. Keep `schemaVersion` as a small JSON integer fixed by `const`. Represent potentially long-lived counters such as workspace revision as decimal strings, or formally cap them at `9_007_199_254_740_991`. Dates, identifiers, and positions should be strings or relative references, not unconstrained numbers.
6. Bind approval to the parsed, validated proposal and the exact preview/execution plan retained in memory; do not reacquire the original input at apply time. A SHA-256 digest of an RFC 8785/JCS representation is a reasonable stable proposal identity only if the v1 instance profile first enforces JCS's I-JSON, Unicode, and binary64 constraints. The digest is an integrity identifier, not authentication.
7. Enforce input byte and nesting limits while reading, before schema validation. Use schema `maxLength`, `maxItems`, and `maxProperties` for structural limits, then add application limits for total bytes, depth, total operations/references, validation work and errors, and preview size. The standards deliberately do not choose those numeric budgets.
8. Treat explanations and every imported title/description/label as untrusted display data. Derive effects only from typed operations, render Markdown through the existing sanitised renderer, fetch no remote content, activate no links automatically, and never copy imported values into logs, diagnostics, exception text, or evidence.

## 1. JSON syntax and duplicate member names

### What the standards say

[RFC 8259 section 4](https://www.rfc-editor.org/rfc/rfc8259.html#section-4) says object member names **SHOULD** be unique, but its grammar still admits repeated names. It warns that receiver behaviour is unpredictable: implementations variously keep the last member, reject the object, or expose every occurrence.

JSON Schema cannot repair this ambiguity. Its 2020-12 data model treats an object as an unordered set that maps one string to one instance, and explicitly leaves the behaviour of an input containing two properties with the same key undefined ([JSON Schema Core 2020-12, section 4.2.1](https://json-schema.org/draft/2020-12/json-schema-core.html#section-4.2.1)). Therefore a validator normally sees only whatever object model its parser produced; `properties`, `additionalProperties`, and `unevaluatedProperties` do not provide reliable duplicate detection.

Member-name equality must be assessed after JSON escape decoding. RFC 8259 notes that comparing the source spellings can incorrectly treat semantically equal strings such as an escaped reverse solidus and its `\u` representation as different ([RFC 8259 section 8.3](https://www.rfc-editor.org/rfc/rfc8259.html#section-8.3)).

JSON text exchanged between systems outside a closed ecosystem must use UTF-8. RFC 8259 prohibits senders from adding a byte-order mark to networked JSON and permits parsers to ignore one for interoperability ([RFC 8259 section 8.1](https://www.rfc-editor.org/rfc/rfc8259.html#section-8.1)). Its grammar does not include comments or trailing commas, and its number grammar excludes `NaN` and infinities ([RFC 8259 section 6](https://www.rfc-editor.org/rfc/rfc8259.html#section-6)).

### dot-orbit v1 recommendation

The ingress parser should enforce a single strict profile before deserialisation or schema validation:

- exactly one top-level JSON object;
- UTF-8 without a byte-order mark;
- no comments, trailing commas, or implementation-specific extensions;
- no unpaired Unicode surrogates;
- no duplicate decoded member name in any object, at any depth; and
- no non-finite or out-of-profile number.

Duplicate detection should be a token-stream check that maintains a set of decoded property names for every open object. It must fail the whole document on the second occurrence. It must not implement “first wins” or “last wins,” because either would let the preview and another consumer interpret different proposals.

This is intentionally stricter than RFC 8259's minimum parser requirement. It is also compatible with I-JSON, which requires no duplicate names and fully valid Unicode ([RFC 7493 sections 2.1 and 2.3](https://www.rfc-editor.org/rfc/rfc7493.html#section-2)).

Unknown properties are a separate concern from duplicate properties. Every instance object should close its vocabulary with `additionalProperties: false` when the object is defined directly, or `unevaluatedProperties: false` when composed schemas require evaluation across subschemas. Unknown operation types should fail through a closed discriminator (`const`/`enum` plus `oneOf`), not be preserved for best-effort execution.

## 2. JSON Schema dialect, identity, and compatibility

### Published status on 2026-10-12

As of 2026-10-12, the [JSON Schema specification page](https://json-schema.org/specification) identifies **2020-12** as the current released version and 2019-09 as the previous version. The 2020-12 Core and Validation texts were published on 2022-06-16; its default dialect meta-schema URI is `https://json-schema.org/draft/2020-12/schema` ([2020-12 Validation, section 5](https://json-schema.org/draft/2020-12/json-schema-validation.html#section-5)).

The IETF JSON Schema working group is active, but its current 2026 document remains an Internet-Draft. On 2026-10-12, `draft-ietf-jsonschema-json-schema-03` is dated 2026-08-26 and expires 2027-02-27; Internet-Drafts are work in progress and are not stable replacement specifications ([IETF draft status](https://datatracker.ietf.org/doc/draft-ietf-jsonschema-json-schema/)). The working group currently targets a Proposed Standard publication request in May 2027 ([IETF JSONSCHEMA charter and milestones](https://datatracker.ietf.org/wg/jsonschema/about/)).

### dot-orbit v1 recommendation

Use JSON Schema 2020-12 for v1. Do not base a shipped interchange contract on the 2026 IETF draft. Record the dialect explicitly in every schema document:

```json
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://<repository-controlled-origin>/schemas/agent-interchange/v1/proposal.schema.json"
}
```

The exact repository-controlled origin and publication path remain a product/repository decision. The important contract properties are:

- the root `$id` is an absolute URI with a scheme and no fragment;
- the path contains the immutable interchange version and document kind;
- the same `$id` is never reused for changed schema semantics or bytes;
- aliases such as `/latest/` are not used as canonical IDs; and
- local filenames and `file:` URIs are not canonical IDs because they are installation-specific.

These rules follow JSON Schema Core: `$schema` identifies the dialect; `$id` gives a schema resource its canonical URI; a root schema should use an absolute `$id`; and a URI is an identifier, not necessarily a downloadable locator ([Core sections 8.1.1 and 8.2.1](https://json-schema.org/draft/2020-12/json-schema-core.html#section-8.2.1)).

`$schema` belongs in dot-orbit's schema documents. It is not a substitute for the required integer `schemaVersion` in snapshot/proposal instances. For v1, each instance should contain a closed document discriminator and `"schemaVersion": 1`; the schema should require `type: "integer"` and `const: 1`. Unsupported versions should be rejected before domain validation. This also avoids inventing a v0 converter, as issue #76 requires.

JSON Schema's `format` keyword is annotation-only in the default dialect unless the Format-Assertion vocabulary is explicitly required and fully implemented. The specification recommends application semantic validation rather than assuming uniform `format` enforcement ([Validation 2020-12, section 7.2](https://json-schema.org/draft/2020-12/json-schema-validation.html#section-7.2)). Therefore identifiers and dates should have schema-level type/length/pattern constraints and then undergo normal application parsing and domain validation. A `format` annotation may supplement, but must not replace, those checks.

## 3. Immutable local resolution and prohibition of remote references

### What the standard permits

JSON Schema resolves `$ref` values as URI references against the current base URI. A network-looking `$id` or `$ref` does not require network access: Core says implementations should know their schemas ahead of time, can associate arbitrary URIs with supplied schemas, and should not assume that a network-addressable URI requires a network operation ([Core sections 8.2.3 and 9.1.2](https://json-schema.org/draft/2020-12/json-schema-core.html#section-9.1.2)).

Draft 2020-12 also defines compound schema documents. Referenced resources can be bundled without changing their canonical IDs or references, commonly under `$defs` ([Core section 9.3.1](https://json-schema.org/draft/2020-12/json-schema-core.html#section-9.3.1)).

The Core security considerations require validators to avoid infinite loops and excessive resource consumption, and warn that an attacker must not be able to replace an existing schema by reusing or imitating its `$id` ([Core section 13](https://json-schema.org/draft/2020-12/json-schema-core.html#section-13)).

### dot-orbit v1 recommendation

Validation must be offline and closed-world:

1. Check in and package the exact 2020-12 general meta-schema, every vocabulary meta-schema it transitively references, and every dot-orbit v1 schema resource needed at runtime.
2. Validate the dot-orbit schemas against that closed packaged registry during build/test, not by downloading resources during instance import.
3. Construct a read-only registry from exact canonical URI to packaged schema resource. Reject duplicate registry IDs at startup/test time.
4. Configure the validator with no HTTP client, no generic URL loader, and no filesystem fallback. A reference not found in the registry is a validation failure.
5. Prefer `#/$defs/...` references inside one schema resource. Where snapshot and proposal schemas share separately identified resources, use their exact absolute versioned IDs and package them together.
6. Do not accept schemas from proposal documents. An instance's `$schema`, `$id`, or `$ref`-looking property is ordinary data and should be rejected as unknown unless the v1 instance schema explicitly defines it.

This makes schema identifiers stable for external tooling while preserving local-only application behaviour. It also prevents server-side request forgery, local-file disclosure, hangs on unavailable networks, and post-release schema drift.

## 4. Binding approval to the exact proposal and preview

### Available approaches

| Approach | Strength | Cost or ambiguity |
| --- | --- | --- |
| Hash the exact accepted UTF-8 bytes | Binds the exact selected file and is simple. | Whitespace or property-order changes produce a new digest; it is not a semantic canonical identity. A byte-order mark policy must be fixed first. |
| RFC 8785 JSON Canonicalization Scheme (JCS), then hash | Produces stable UTF-8 bytes across whitespace and object-property order; designed to make JSON hashable. | JCS is an Informational RFC, not an IETF Standards Track RFC. It requires I-JSON, IEEE-754 binary64-compatible numbers, preservation of strings without Unicode normalisation, recursive UTF-16 property sorting, and rejection of invalid Unicode. Parsing before duplicate detection can already have lost information. |
| Re-serialise a dot-orbit validated model, then hash | Naturally excludes parser trivia and can match the application model. | It is an application-specific canonicalisation unless every field, omission/default rule, string rule, ordering rule, and serializer behaviour is versioned and published. Model evolution can silently change the digest. |
| Retain an immutable validated execution plan in memory | Directly binds the preview to what will be applied and avoids rereading a replaced file. | The approval is intentionally session-local and cannot be resumed after restart unless a persistent, versioned plan format is designed. |

[RFC 8785](https://www.rfc-editor.org/rfc/rfc8785.html) defines JCS as a canonical representation suitable for hashing. It requires no duplicate property names, Unicode strings, binary64-expressible numbers, no whitespace, deterministic recursive property sorting by UTF-16 code units, and final UTF-8 encoding (sections 3.1 and 3.2). It does **not** normalise Unicode strings; all participants must preserve the parsed string data as-is. Invalid lone surrogates are an error.

JCS is a good cross-tool proposal identity only when the interchange number profile is deliberately compatible with it. It must not be introduced by simply parsing arbitrary RFC 8259 numbers as `double`, because JCS examples demonstrate that accepted input numbers can be rounded during binary64 serialisation ([RFC 8785 section 3.2.2 and Appendix B](https://www.rfc-editor.org/rfc/rfc8785.html#appendix-B)).

SHA-256 is sufficient for an internal proposal integrity identifier. [RFC 9530](https://www.rfc-editor.org/rfc/rfc9530.html#section-5) lists `sha-256` as active and suitable for many adversarial integrity uses. A plain digest does not authenticate its source: an attacker who can replace both content and digest can recompute it, as RFC 9530's security considerations explain. Agent interchange v1 does not need to claim signatures, provenance, or trusted authorship.

### dot-orbit v1 recommendation

Approval should be session-bound rather than file-bound:

1. Capture the explicitly supplied proposal once under the byte/depth limits.
2. Reject duplicate names and the strict-profile violations before deserialisation.
3. Validate against the locally bundled schema and then against the current domain state.
4. Build one immutable, ordered execution plan and derive the complete preview solely from that plan.
5. Calculate `SHA-256(JCS(validated proposal))` as the proposal identity. This is safe if v1 follows the number restrictions in the next section.
6. Associate the pending approval with the proposal digest, source snapshot ID, source workspace revision, the immutable execution plan, and every explicit user choice made during preview.
7. On Apply, do not reacquire or reparse the original input. At the mutation transaction boundary, repeat the workspace revision/snapshot precondition and apply the retained plan atomically. If the session, plan, or revision changed, expire approval and require a fresh intake/preview.

The proposal digest is derived from sensitive content and remains internal to the pending review; it is not diagnostic metadata. If there is a requirement to persist an approval across application restart, v1 would need a separately versioned canonical execution-plan/preview format. None of the cited standards defines that product workflow.

## 5. JSON numbers and integers

### What the standards say

RFC 8259 permits implementations to limit number range and precision. It says binary64 support gives good interoperability, and integers in `[-(2^53)+1, (2^53)-1]` interoperate exactly ([RFC 8259 section 6](https://www.rfc-editor.org/rfc/rfc8259.html#section-6)). I-JSON repeats that values outside `-9_007_199_254_740_991` through `9_007_199_254_740_991` cannot be expected to arrive exactly and recommends strings when greater magnitude or precision is required ([RFC 7493 section 2.2](https://www.rfc-editor.org/rfc/rfc7493.html#section-2.2)).

JSON Schema's data model treats numbers as arbitrary-precision base-10 mathematical values and ignores lexical distinctions. In the Validation vocabulary, `type: "integer"` matches any number with a zero fractional part, so `1`, `1.0`, and `1e0` are not distinct for validation ([Core section 4.2.1](https://json-schema.org/draft/2020-12/json-schema-core.html#section-4.2.1), [Validation section 6.1.1](https://json-schema.org/draft/2020-12/json-schema-validation.html#section-6.1.1)). JSON Schema therefore cannot enforce an “integer token with no decimal point or exponent” lexical rule.

### dot-orbit v1 recommendation

- `schemaVersion` may remain a JSON integer because its allowed value is exactly `1`.
- If workspace revision is a monotonic counter with no guaranteed permanent binary64 bound, encode it as a canonical decimal string matching `^(0|[1-9][0-9]*)$` with an explicit maximum length. Do not use a JSON number merely because the current implementation uses a machine integer.
- Use string encodings for stable IDs, UUIDs, dates, and proposal-local references.
- Do not put persistence positions on the wire. The existing requirement for relative Project-Task reorder targets is also more robust than numeric positions.
- Avoid fractional numbers in v1. There is no current domain need for them, and excluding them removes rounding and canonicalisation disagreements.
- If any other integer is introduced, give it explicit `minimum` and `maximum` constraints inside the exact-integer range.

The contract should decide whether lexical `1.0` for `schemaVersion` is accepted and canonicalised to `1`, or rejected by the strict token pass. JSON Schema does not make that decision. Accept-and-canonicalise is simpler and retains mathematical JSON Schema semantics; reject is stricter but requires a documented non-schema lexical rule.

## 6. Resource limits

### What the standards provide

RFC 8259 explicitly permits limits on total JSON text size, nesting depth, number range/precision, and string length/content, but deliberately specifies no numeric ceilings ([RFC 8259 section 9](https://www.rfc-editor.org/rfc/rfc8259.html#section-9)).

JSON Schema 2020-12 can express per-value and per-container limits with `maxLength`, `maxItems`, `maxProperties`, numeric bounds, and related keywords ([Validation sections 6.2–6.5](https://json-schema.org/draft/2020-12/json-schema-validation.html#section-6)). It does not provide a general maximum input-byte count, aggregate node count, nesting-depth limit, validator time budget, maximum error count, or preview-rendering budget. Its security section requires validators to avoid infinite loops and excessive resource use but does not select limits for an application.

CommonMark 0.31.2, which the official site still lists as the latest release on 2026-10-12, treats any character sequence as a document. It permits implementations to cap balanced-parenthesis nesting in link destinations for performance, while requiring support for at least three levels ([CommonMark 0.31.2 sections 2.1 and 6.3](https://spec.commonmark.org/0.31.2/)). It supplies no general Markdown byte, line, nesting, or rendering budget.

### dot-orbit v1 recommendation

The final v1 contract should publish explicit, boundary-specific limits for snapshot and instruction output, proposal input, validation, previews, and later MCP reuse. Limits should be consistent where the boundaries have the same trust and compatibility needs, but proposal-only field limits must not make current domain-valid stored data impossible to represent in a snapshot. At minimum, decide and test:

| Boundary | Enforcement point | Why schema alone is insufficient |
| --- | --- | --- |
| Maximum UTF-8 bytes | Before allocating/parsing the full document | JSON Schema sees a parsed instance, not source bytes. |
| Maximum JSON nesting depth | Streaming parser | JSON Schema has no general depth keyword. |
| Maximum string length by field | Parser aggregate guard plus schema `maxLength` | `maxLength` is per instance string and does not cap total text retained. |
| Maximum object properties and array elements | Schema `maxProperties`/`maxItems`, plus aggregate guard | Per-container limits do not cap the total tree. |
| Maximum proposal operations | Proposal schema `maxItems` and application check | This is a product/preview budget, not a JSON limit. |
| Maximum proposal-local records/references and dependency edges | Application validation | Cross-operation graph work is outside JSON Schema's structural model. |
| Maximum validation failures collected | Validator/application configuration | Exhaustive error output can itself consume memory and expose input. |
| Maximum Markdown characters/blocks/nesting | Field schema plus sanitised renderer guard | CommonMark does not provide a global resource ceiling. |
| Maximum preview rows/text | Preview builder before UI materialisation | A bounded proposal can still expand into a larger effect preview. |

The numbers must be chosen from representative large sample workspaces and adversarial tests, then recorded as v1 contract values. Standards cannot justify a dot-orbit-specific “correct” maximum. Limits should fail the entire proposal before recovery backup or mutation, with fixed error codes and safe counts rather than imported content.

Avoid schema constructs whose work is disproportionate to the small contract: deeply recursive schemas, expensive regular expressions, and large `uniqueItems` arrays. Enforce semantic uniqueness of operation IDs and proposal-local references in one bounded application pass.

## 7. Security implications of untrusted JSON and Markdown metadata

### CommonMark is not a sanitizer

CommonMark 0.31.2 defines raw HTML blocks and inline HTML that are emitted without escaping; its examples include a `<script>` block ([sections 4.6 and 6.6](https://spec.commonmark.org/0.31.2/)). Its link syntax defines how to parse a destination but does not impose dot-orbit's safe URI-scheme or activation policy ([section 6.3](https://spec.commonmark.org/0.31.2/#links)). A CommonMark-conforming parse therefore does not make untrusted Markdown safe to render in HTML or a web-capable surface.

The existing [`SanitisedMarkdownRenderer`](../../src/DotOrbit.Markdown/SanitisedMarkdown.cs) already supplies the correct product boundary: it drops block and inline HTML, retains only `http`, `https`, `mailto`, and fragment links, and replaces images with non-fetching placeholders. `MARKDOWN-007` requires imported/agent-proposed Markdown to follow this same path. V1 should reuse that renderer for imported descriptions and explanations rather than introducing a proposal-preview renderer.

### dot-orbit v1 recommendation

- Derive all effects, counts, conflicts, and warnings only from typed validated operations. Explanations are never interpreted as commands, fallback fields, links to fetch, or Task content.
- Label explanations as untrusted agent-provided review metadata and visually separate them from the application's authoritative effect preview. The application preview, not the explanation, is the approval basis.
- Run every proposed Markdown field and any Markdown explanation through `SanitisedMarkdownRenderer`. Keep raw HTML disabled, keep the URI-scheme allowlist, never load images, and never activate links automatically.
- Render ordinary titles, Category names, Participant labels, identifiers, and validation locations as text, never as markup. Do not concatenate untrusted values into HTML, rich text, command lines, filesystem paths, or schema error templates.
- Do not include imported values, excerpts, or content-derived digests in logs, exception messages, telemetry, crash reports, CI output, screenshots, or evidence. Diagnostics should contain only safe metadata such as document kind, byte count, operation index, JSON Pointer to a known field, fixed error code, and schema version.
- Map parser/validator exceptions to safe application errors because third-party exception text may echo offending values. Cap the number and size of displayed validation errors.
- Preserve strings exactly for proposal identity. Apply existing domain normalisation only at the domain-validation boundary (for example, Participant uniqueness), and show the resulting effect or conflict explicitly. Do not silently Unicode-normalise the imported document before a JCS digest; RFC 8785 requires string preservation.
- Treat proposal-local references as opaque bounded identifiers. Never resolve them as URIs, files, JSON Pointers supplied by the proposal, or executable expressions.
- Keep all validation and preview work offline. Even an apparently harmless remote image, schema reference, or link preview can disclose that a proposal was opened and can expose network metadata.

JSON itself must be parsed as data, never with an `eval`-like facility; RFC 8259 calls evaluation an unacceptable security risk ([RFC 8259 section 12](https://www.rfc-editor.org/rfc/rfc8259.html#section-12)).

## 8. Decisions the standards cannot make for issue #76

The cited specifications do not determine:

- the exact schema `$id` authority or public publication location;
- the concrete byte, depth, string, operation, graph, error, and preview limits;
- snapshot contents, workspace-revision semantics, or atomic-read behaviour;
- proposal operation vocabulary, ordering, dependencies, or conflict policy;
- whole-proposal versus per-operation approval;
- what preview detail is sufficient for informed approval;
- whether approval survives navigation or application restart;
- whether a JCS digest is user-visible, persisted, or only an internal guard;
- Category/Participant creation and rename policy; or
- which archived/completed records may be referenced.

Issue #76 must record those as product/security decisions. The standards research only narrows the safe implementation envelope.

## Primary sources

- [RFC 8259 — The JavaScript Object Notation (JSON) Data Interchange Format, December 2017](https://www.rfc-editor.org/rfc/rfc8259.html)
- [RFC 7493 — The I-JSON Message Format, March 2015](https://www.rfc-editor.org/rfc/rfc7493.html)
- [JSON Schema Core, Draft 2020-12, published 2022-06-16](https://json-schema.org/draft/2020-12/json-schema-core.html)
- [JSON Schema Validation, Draft 2020-12, published 2022-06-16](https://json-schema.org/draft/2020-12/json-schema-validation.html)
- [JSON Schema project specification status](https://json-schema.org/specification)
- [IETF JSONSCHEMA working group status](https://datatracker.ietf.org/wg/jsonschema/about/)
- [RFC 8785 — JSON Canonicalization Scheme, June 2020](https://www.rfc-editor.org/rfc/rfc8785.html)
- [RFC 9530 — Digest Fields, February 2024](https://www.rfc-editor.org/rfc/rfc9530.html)
- [CommonMark specification 0.31.2, published 2024-01-28](https://spec.commonmark.org/0.31.2/)
