import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const requiredFiles = [".nojekyll", "CNAME", "index.html", "orbit-mark.svg", "shared.css"];
const localReferencePattern = /\b(?:href|src)="([^"]+)"/gu;

export function validateSite(siteRoot) {
  const root = path.resolve(siteRoot);
  const failures = [];

  for (const requiredFile of requiredFiles) {
    if (!fs.existsSync(path.join(root, requiredFile))) {
      failures.push(`missing required file: ${requiredFile}`);
    }
  }

  const cnamePath = path.join(root, "CNAME");
  if (fs.existsSync(cnamePath) && fs.readFileSync(cnamePath, "utf8").trim() !== "dot-orbit.app") {
    failures.push("CNAME must contain only dot-orbit.app");
  }

  const indexPath = path.join(root, "index.html");
  if (fs.existsSync(indexPath)) {
    const html = fs.readFileSync(indexPath, "utf8");
    if (html.includes("localhost") || html.includes("?preview=")) {
      failures.push("production HTML must not contain local preview URLs");
    }
    if (!html.includes('href="https://github.com/nullabletype/dot-orbit/releases"')) {
      failures.push("production HTML must include the GitHub Releases download link");
    }

    for (const match of html.matchAll(localReferencePattern)) {
      const reference = match[1];
      if (/^(?:https?:|mailto:|#)/u.test(reference)) continue;

      const pathname = reference.split(/[?#]/u, 1)[0];
      const target = pathname.startsWith("/")
        ? path.resolve(root, `.${pathname}`)
        : path.resolve(root, pathname);
      const relativeTarget = path.relative(root, target);
      if (relativeTarget.startsWith("..") || path.isAbsolute(relativeTarget)) {
        failures.push(`local reference escapes the site root: ${reference}`);
      } else if (!fs.existsSync(target)) {
        failures.push(`local reference does not exist: ${reference}`);
      }
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
