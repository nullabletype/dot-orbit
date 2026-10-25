# Portable encrypted recovery fixture

`portable-current-v13.dotorbit-recovery` is a fixed synthetic schema-13 encrypted workspace used to prove that identical historical recovery bytes can be validated, restored, and migrated to the current schema by every supported runtime package job.

- Generated on 2026-10-06 from commit `786c2319a52f360422887fd434d7768e91c5e0ea` with the production sample-workspace generator on macOS arm64.
- Anchor date: `2030-04-05`.
- Public test-only passphrase: `dot-orbit sample only`.
- SHA-256: `6141f60c4fa5f8bd23b2336b6b217adc72e227966b8626c7e994496c2f4c1852`.

The fixture intentionally remains at schema 13 so it exercises the supported historical upgrade path. Do not regenerate it merely because the current schema advances. If the compatibility baseline is deliberately changed, generate its replacement with the production sample-workspace generator from a commit where the target schema is current, then update the filename, hash, tests, and workflow references together. Never use real workspace data or edit the database directly.
