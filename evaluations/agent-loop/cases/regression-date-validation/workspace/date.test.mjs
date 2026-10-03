import assert from "node:assert/strict";
import test from "node:test";
import { parseDate } from "./date.mjs";

test("accepts a canonical persisted date", () => {
  assert.equal(parseDate("2026-10-03"), "2026-10-03");
});

test("rejects malformed input", () => {
  assert.equal(parseDate("03/10/2026"), undefined);
  assert.equal(parseDate(""), undefined);
});
