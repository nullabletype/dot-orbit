import assert from "node:assert/strict";
import test from "node:test";
import { moveItem } from "./order.mjs";

test("moves an item in either direction without mutating the input", () => {
  const values = ["a", "b", "c", "d"];
  assert.deepEqual(moveItem(values, 1, 3), ["a", "c", "d", "b"]);
  assert.deepEqual(moveItem(values, 3, 1), ["a", "d", "b", "c"]);
  assert.deepEqual(values, ["a", "b", "c", "d"]);
});

test("clamps destinations after removal", () => {
  assert.deepEqual(moveItem(["a", "b", "c"], 1, -20), ["b", "a", "c"]);
  assert.deepEqual(moveItem(["a", "b", "c"], 0, 20), ["b", "c", "a"]);
});

test("returns an unchanged copy for an invalid source", () => {
  const values = ["a", "b"];
  const result = moveItem(values, 9, 0);
  assert.deepEqual(result, values);
  assert.notEqual(result, values);
});
