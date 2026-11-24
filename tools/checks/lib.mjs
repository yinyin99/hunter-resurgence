// Shared by the scripts here (CONTRIBUTING.md, "Checks"). Run from the repository root. Needs Node 20+, no packages.
import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';

// the file arguments that still exist (a deleted file has nothing to check); no arguments: every tracked file
export function files(args = process.argv.slice(2)) {
  if (!args.length) args = execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8', maxBuffer: 1 << 28 }).split('\0');
  return args.filter((f) => f && existsSync(f));
}

// one line per problem on stderr, exit 1 when there is any
export function done(name, problems) {
  for (const p of problems) console.error(`${name}: ${p}`);
  process.exit(problems.length ? 1 : 0);
}
