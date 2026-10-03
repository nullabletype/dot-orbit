export function help() {
  return `Usage: orbit-report [--output <path>]

Options:
  --output <path>  Write the report (default: ./orbit-report.json)`;
}

if (process.argv.includes("--help")) console.log(help());
