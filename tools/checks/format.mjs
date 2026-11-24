// node tools/checks/format.mjs [files...]: the .cs files are formatted as `dotnet format whitespace` wants (.editorconfig). No arguments: every tracked .cs file (main is not clean yet, see CONTRIBUTING.md).
// Whole files are checked, not only the lines a change touched. --folder reads the files as text: no restore or build, about 2 s.
import { spawnSync } from 'node:child_process';
import { files } from './lib.mjs';

const cs = files().filter((f) => f.endsWith('.cs'));
if (!cs.length) process.exit(0);
const run = spawnSync('dotnet', ['format', 'whitespace', '.', '--folder', '--verify-no-changes', '--include', ...cs], { stdio: 'inherit' });
if (run.status !== 0) console.error(`format: fix with: dotnet format whitespace . --folder --include ${cs.join(' ')}`);
process.exit(run.status ?? 1);
