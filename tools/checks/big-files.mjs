// node tools/checks/big-files.mjs [files...]: no file over 1 MB, nothing under TestResults/, bin/ or obj/. No arguments: every tracked file.
import { statSync } from 'node:fs';
import { done, files } from './lib.mjs';

const problems = [];
for (const f of files()) {
  if (/(^|\/)(TestResults|bin|obj)\//.test(f)) problems.push(`${f} is build output, not source`);
  else if (statSync(f).size > 1024 * 1024) problems.push(`${f} is ${(statSync(f).size / 1024 / 1024).toFixed(1)} MB, at most 1 MB`);
}
done('big-files', problems);
