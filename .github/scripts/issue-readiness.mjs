import { pathToFileURL } from "node:url";

export const contractValue = "dot-orbit-issue-readiness:v1";
export const requiredHeadings = [
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

const managedLabels = ["blocked", "ready-for-agent"];
const manualLabels = ["needs-triage", "needs-info", "ready-for-human", "wontfix"];

function sections(body) {
  const result = new Map();
  let heading;
  let content = [];
  const complete = () => {
    if (heading === undefined) return;
    const values = result.get(heading) ?? [];
    values.push(content.join("\n").trim());
    result.set(heading, values);
    content = [];
  };

  for (const line of body.replaceAll("\r\n", "\n").split("\n")) {
    const match = /^(?:##|###) (.*)$/.exec(line);
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

function parseDependencies(content) {
  const trimmed = content.trim();
  if (trimmed.toLowerCase() === "none") return { dependencies: [] };

  const dependencies = new Set();
  for (const line of trimmed.split("\n")) {
    const match = /^-\s+#([0-9]+)\s*$/.exec(line.replace(/\r$/, ""));
    const number = match && Number(match[1]);
    if (!Number.isInteger(number) || number <= 0 || number > 2_147_483_647) {
      return { failureReason: "blocked-by-malformed" };
    }
    dependencies.add(number);
  }

  return dependencies.size === 0
    ? { failureReason: "blocked-by-malformed" }
    : { dependencies: [...dependencies].sort((left, right) => left - right) };
}

function parseDependenciesOnly(body) {
  const values = sections(body).get("Blocked by");
  return values?.length === 1
    ? parseDependencies(values[0])
    : { failureReason: "section-blocked-by-missing-or-duplicate" };
}

export function parseIssue(body) {
  const parsedSections = sections(body);
  for (const heading of requiredHeadings) {
    const values = parsedSections.get(heading);
    const slug = heading.toLowerCase().replaceAll(" ", "-");
    if (values?.length !== 1) {
      return { failureReason: `section-${slug}-missing-or-duplicate` };
    }
    if (!values[0].trim()) return { failureReason: `section-${slug}-blank` };
  }
  if (parsedSections.get("Readiness contract")[0].trim() !== contractValue) {
    return { failureReason: "readiness-contract-unsupported" };
  }
  if (parsedSections.get("Open decisions")[0].trim().toLowerCase() !== "none") {
    return { failureReason: "open-decisions-unresolved" };
  }
  return parseDependencies(parsedSections.get("Blocked by")[0]);
}

function labelPlan(issue, desiredLabel, reason) {
  const deltas = [];
  if (!issue.labels.has(desiredLabel)) deltas.push({ operation: "add", label: desiredLabel });
  for (const label of managedLabels) {
    if (label !== desiredLabel && issue.labels.has(label)) {
      deltas.push({ operation: "remove", label });
    }
  }
  return { issueNumber: issue.number, reason, deltas };
}

function cycleFrom(current, graph, path) {
  const repeatedAt = path.indexOf(current);
  if (repeatedAt >= 0) return [...path.slice(repeatedAt), current];

  path.push(current);
  for (const dependency of graph.get(current)?.dependencies ?? []) {
    const cycle = cycleFrom(dependency, graph, path);
    if (cycle) return cycle;
  }
  path.pop();
  return undefined;
}

export function planReadiness(issues) {
  const byNumber = new Map(issues.map((issue) => [issue.number, issue]));
  const graph = new Map(issues.map((issue) => [issue.number, parseDependenciesOnly(issue.body)]));
  const plans = [];

  for (const issue of [...issues].sort((left, right) => left.number - right.number)) {
    const managed = issue.isOpen
      && issue.labels.has("implementation-slice")
      && managedLabels.some((label) => issue.labels.has(label))
      && !manualLabels.some((label) => issue.labels.has(label));
    if (!managed) continue;

    const specification = parseIssue(issue.body);
    if (specification.failureReason) {
      plans.push(labelPlan(issue, "needs-triage", specification.failureReason));
      continue;
    }
    if (specification.dependencies.includes(issue.number)) {
      plans.push(labelPlan(issue, "needs-triage", "dependency-self-reference"));
      continue;
    }

    const missing = specification.dependencies.filter((number) => !byNumber.has(number));
    if (missing.length > 0) {
      plans.push(labelPlan(issue, "needs-triage", `dependency-not-found:${missing.join(",")}`));
      continue;
    }

    const cycle = cycleFrom(issue.number, graph, []);
    if (cycle) {
      plans.push(labelPlan(issue, "needs-triage", `dependency-cycle:${cycle.join(",")}`));
      continue;
    }

    const open = specification.dependencies.filter((number) => byNumber.get(number).isOpen);
    plans.push(open.length === 0
      ? labelPlan(issue, "ready-for-agent", "dependencies-closed")
      : labelPlan(issue, "blocked", `dependencies-open:${open.join(",")}`));
  }
  return plans;
}

function headers(token) {
  return {
    Accept: "application/vnd.github+json",
    Authorization: `Bearer ${token}`,
    "User-Agent": "dot-orbit-issue-readiness/1.0",
    "X-GitHub-Api-Version": "2022-11-28",
  };
}

async function checkedFetch(fetchImpl, url, options = {}) {
  const response = await fetchImpl(url, options);
  if (!response.ok) {
    throw new Error(`github-api:${response.status}:${options.method ?? "GET"}`);
  }
  return response;
}

function nextLink(response) {
  for (const part of (response.headers.get("link") ?? "").split(",")) {
    const match = /<([^>]+)>;\s*rel="next"/.exec(part.trim());
    if (match) return match[1];
  }
  return undefined;
}

function apiRoot(apiUrl) {
  return apiUrl.endsWith("/") ? apiUrl : `${apiUrl}/`;
}

export async function readIssues({ apiUrl, repository, token, fetchImpl = fetch }) {
  const issues = [];
  let url = new URL(`repos/${repository}/issues?state=all&per_page=100&page=1`, apiRoot(apiUrl));
  while (url) {
    const response = await checkedFetch(fetchImpl, url, { headers: headers(token) });
    const page = await response.json();
    issues.push(...page
      .filter((issue) => issue.pull_request === undefined)
      .map((issue) => ({
        number: issue.number,
        body: issue.body ?? "",
        labels: new Set(issue.labels.map((label) => label.name)),
        isOpen: issue.state === "open",
      })));
    const next = nextLink(response);
    url = next ? new URL(next, url) : undefined;
  }
  return issues;
}

export async function reconcile({ apiUrl, repository, token, dryRun, fetchImpl = fetch, log = console.log }) {
  const requestHeaders = { ...headers(token), "Content-Type": "application/json" };
  const issues = await readIssues({ apiUrl, repository, token, fetchImpl });
  for (const plan of planReadiness(issues)) {
    if (plan.deltas.length === 0) continue;
    const labels = plan.deltas.map((delta) => `${delta.operation === "add" ? "+" : "-"}${delta.label}`);
    log(`issue-readiness: issue=#${plan.issueNumber} reason=${plan.reason} labels=${labels.join(",")} dry-run=${dryRun}`);
    if (dryRun) continue;

    for (const delta of plan.deltas) {
      const base = new URL(`repos/${repository}/issues/${plan.issueNumber}/labels`, apiRoot(apiUrl));
      if (delta.operation === "add") {
        await checkedFetch(fetchImpl, base, {
          method: "POST",
          headers: requestHeaders,
          body: JSON.stringify({ labels: [delta.label] }),
        });
      } else {
        await checkedFetch(fetchImpl, `${base}/${encodeURIComponent(delta.label)}`, {
          method: "DELETE",
          headers: headers(token),
        });
      }
    }
  }
}

export async function main(args = process.argv.slice(2), environment = process.env) {
  const dryRun = args.length === 1 && args[0] === "--dry-run";
  if (args.length !== 0 && !dryRun) throw new Error("invalid-arguments");
  for (const name of ["GITHUB_REPOSITORY", "GITHUB_API_URL", "GITHUB_TOKEN"]) {
    if (!environment[name]?.trim()) throw new Error(`missing-environment:${name}`);
  }
  await reconcile({
    apiUrl: environment.GITHUB_API_URL,
    repository: environment.GITHUB_REPOSITORY,
    token: environment.GITHUB_TOKEN,
    dryRun,
  });
}

if (process.argv[1] && pathToFileURL(process.argv[1]).href === import.meta.url) {
  main().catch((error) => {
    console.error(`issue-readiness: result=failed reason=${error.message}`);
    process.exitCode = 1;
  });
}
