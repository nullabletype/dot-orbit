import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const approvedFiles = [
  ".nojekyll",
  "CNAME",
  "index.html",
  "orbit-mark.svg",
  "screenshots/ai-light-current.png",
  "screenshots/categories-light-current.png",
  "screenshots/projects-dark-current.png",
  "screenshots/settings-dark-current.png",
  "screenshots/today-dark-current.png",
  "shared.css",
];
const localReferencePattern = /\b(?:href|src)="([^"]+)"/gu;
const cssReferencePattern = /url\(\s*["']?([^"')]+)["']?\s*\)/gu;

function listFiles(root, directory = root) {
  const files = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const absolutePath = path.join(directory, entry.name);
    const relativePath = path.relative(root, absolutePath).split(path.sep).join("/");
    if (entry.isSymbolicLink()) throw new Error(`site bundle must not contain symbolic links: ${relativePath}`);
    if (entry.isDirectory()) files.push(...listFiles(root, absolutePath));
    else if (entry.isFile()) files.push(relativePath);
    else throw new Error(`site bundle contains an unsupported entry: ${relativePath}`);
  }
  return files.sort();
}

function localTarget(root, sourceFile, reference) {
  const pathname = reference.split(/[?#]/u, 1)[0];
  if (pathname === "") return path.join(root, sourceFile);
  if (pathname === "/") return path.join(root, "index.html");
  return pathname.startsWith("/")
    ? path.resolve(root, `.${pathname}`)
    : path.resolve(root, path.dirname(sourceFile), pathname);
}

function validateLocalReference(root, sourceFile, reference, failures) {
  if (/^(?:https?:|mailto:|tel:|data:)/u.test(reference)) return;

  const target = localTarget(root, sourceFile, reference);
  const relativeTarget = path.relative(root, target);
  if (relativeTarget.startsWith("..") || path.isAbsolute(relativeTarget)) {
    failures.push(`local reference escapes the site root: ${reference}`);
  } else if (!fs.existsSync(target)) {
    failures.push(`local reference does not exist: ${reference}`);
  }
}

export function validateSite(siteRoot) {
  const root = path.resolve(siteRoot);
  const failures = [];

  const actualFiles = fs.existsSync(root) ? listFiles(root) : [];
  for (const approvedFile of approvedFiles) {
    if (!actualFiles.includes(approvedFile)) failures.push(`missing required file: ${approvedFile}`);
  }
  for (const actualFile of actualFiles) {
    if (!approvedFiles.includes(actualFile)) failures.push(`unapproved file in site bundle: ${actualFile}`);
  }

  const cnamePath = path.join(root, "CNAME");
  if (fs.existsSync(cnamePath) && fs.readFileSync(cnamePath, "utf8").trim() !== "dot-orbit.app") {
    failures.push("CNAME must contain only dot-orbit.app");
  }

  for (const htmlFile of actualFiles.filter(file => file.endsWith(".html"))) {
    const html = fs.readFileSync(path.join(root, htmlFile), "utf8");
    if (!/<!doctype html>/iu.test(html)
        || !/<html\b/iu.test(html)
        || !/<head\b/iu.test(html)
        || !/<body\b/iu.test(html)
        || !/<\/html>/iu.test(html)) {
      failures.push(`production HTML has an incomplete document structure: ${htmlFile}`);
    }
    if (html.includes("localhost") || html.includes("?preview=")) {
      failures.push("production HTML must not contain local preview URLs");
    }
    if (htmlFile === "index.html"
        && !html.includes('href="https://github.com/nullabletype/dot-orbit/releases"')) {
      failures.push("production HTML must include the GitHub Releases download link");
    }
    if (htmlFile === "index.html" && html.includes("Pre-release builds are available")) {
      failures.push("production HTML must not describe all downloads as pre-release builds");
    }

    const ids = new Set([...html.matchAll(/\bid="([^"]+)"/gu)].map(match => match[1]));
    for (const match of html.matchAll(localReferencePattern)) {
      const reference = match[1];
      validateLocalReference(root, htmlFile, reference, failures);
      const fragment = reference.startsWith("#") ? reference.slice(1) : undefined;
      if (fragment && !ids.has(fragment)) {
        failures.push(`local fragment does not exist in ${htmlFile}: #${fragment}`);
      }
    }
  }

  for (const cssFile of actualFiles.filter(file => file.endsWith(".css"))) {
    const css = fs.readFileSync(path.join(root, cssFile), "utf8");
    for (const match of css.matchAll(cssReferencePattern)) {
      validateLocalReference(root, cssFile, match[1], failures);
    }
  }

  if (failures.length > 0) {
    throw new Error(failures.join("; "));
  }
}

async function run(arguments_) {
  if (arguments_.length > 1) {
    throw new Error("Usage: node .github/scripts/site-controls.mjs [site-root]");
  }
  const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "../..");
  validateSite(arguments_[0] ?? path.join(repositoryRoot, "site"));
  console.log("site-controls: result=passed");
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    await run(process.argv.slice(2));
  } catch (error) {
    console.error(`site-controls: result=failed reason=${error.message}`);
    process.exitCode = 1;
  }
}
