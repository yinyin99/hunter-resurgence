// node tools/checks/commit-title.mjs [--pr] "<title>": a Conventional Commits title, at most 72 characters (65 with --pr: GitHub adds " (#n)" to a squash commit).
// Only the first line is read, so a commit message file's content works. A merge or revert commit git wrote is fine as a commit title, not as a pull request's.
import { done } from './lib.mjs';

const args = process.argv.slice(2);
const pr = args.includes('--pr');
const title = (args.find((a) => a !== '--pr') ?? '').split('\n')[0].trimEnd();
const max = pr ? 65 : 72;
const problems = [];
if (!/^(feat|fix|docs|test|ci|build|chore|perf|refactor|revert|style)(\([\w./-]+\))?!?: \S/.test(title) && (pr || !/^(Merge |Revert ")/.test(title)))
  problems.push(`"${title}" is not "type(scope)?: subject" (types: feat fix docs test ci build chore perf refactor revert style)`);
if (title.length > max) problems.push(`the title is ${title.length} characters, at most ${max}`);
done('commit-title', problems);
