import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

test("retry action and recovery state have non-colour accessibility semantics", async () => {
  const markup = await readFile(new URL("RecoveryPanel.axaml", import.meta.url), "utf8");
  assert.match(markup, /<Button[^>]*AutomationProperties\.Name="Retry recovery"/s);
  assert.match(markup, /<Button[^>]*IsTabStop="True"/s);
  assert.match(markup, /<TextBlock[^>]*Text="Recovery failed"/s);
  assert.match(markup, /AutomationProperties\.LiveSetting="Assertive"/);
});
