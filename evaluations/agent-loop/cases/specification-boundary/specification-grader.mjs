import { readFile } from "node:fs/promises";
import { fileURLToPath } from "node:url";

const requiredHeadings = [
  "Readiness contract",
  "User-visible outcome",
  "Acceptance criteria",
  "Non-goals",
  "Domain and decision context",
  "Known constraints",
  "Verification",
  "Open decisions",
  "Blocked by",
];

function sections(markdown) {
  const result = new Map();
  let heading;
  let content = [];
  const complete = () => {
    if (heading !== undefined) result.set(heading, content.join("\n").trim());
    content = [];
  };
  for (const line of markdown.replaceAll("\r\n", "\n").split("\n")) {
    const match = /^## (.+)$/.exec(line);
    if (match) {
      complete();
      heading = match[1].trim();
    } else if (heading !== undefined) {
      content.push(line);
    }
  }
  complete();
  return result;
}

function bulletCount(value) {
  return value.split("\n").filter((line) => /^- (?:\[[ x]\] )?\S/.test(line)).length;
}

export function gradeSpecification(markdown) {
  const parsed = sections(markdown);
  if (requiredHeadings.some((heading) => !parsed.get(heading)?.trim())) return false;
  if (parsed.get("Readiness contract") !== "dot-orbit-issue-readiness:v1") return false;
  if (parsed.get("Open decisions").toLocaleLowerCase("en-US") !== "none") return false;
  if (parsed.get("Blocked by").toLocaleLowerCase("en-US") !== "none") return false;
  if (bulletCount(parsed.get("Acceptance criteria")) < 3) return false;
  if (bulletCount(parsed.get("Non-goals")) < 3) return false;
  if (bulletCount(parsed.get("Verification")) < 3) return false;
  const lower = (heading) => parsed.get(heading).toLocaleLowerCase("en-US");
  return ["task", "save", "encrypted"].every((term) => lower("Acceptance criteria").includes(term))
    && ["accounts", "sync", "cloud"].every((term) => lower("Non-goals").includes(term))
    && ["context.md", "local-only", "single context"].every((term) => lower("Domain and decision context").includes(term))
    && ["encrypted", "diagnostics", "plaintext"].every((term) => lower("Known constraints").includes(term))
    && ["test", "reopen", "gate"].every((term) => lower("Verification").includes(term));
}

async function main() {
  const path = new URL("workspace/issue.md", import.meta.url);
  if (!gradeSpecification(await readFile(path, "utf8"))) process.exitCode = 1;
}

if (process.argv[1] === fileURLToPath(import.meta.url)) await main();
