// node tools/checks/parse.mjs [files...]: .json files are valid JSON, .yml and .yaml files valid YAML. No arguments: every tracked file.
// Node has no YAML parser, so YAML goes through PyYAML (on GitHub's Ubuntu runners; `pip install pyyaml` elsewhere).
import { spawnSync } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { done, files } from './lib.mjs';

const problems = [];
const all = files();
for (const f of all.filter((f) => f.endsWith('.json'))) {
  try { JSON.parse(readFileSync(f, 'utf8').replace(/^\uFEFF/, '')); } catch (e) { problems.push(`${f}: ${e.message}`); }
}
const yaml = all.filter((f) => /\.ya?ml$/.test(f));
if (yaml.length) {
  const py = 'import sys, yaml\nfor f in sys.argv[1:]:\n  try: list(yaml.safe_load_all(open(f, encoding="utf-8")))\n  except Exception as e: print(f + ": " + " ".join(str(e).split()))';
  const run = spawnSync(process.platform === 'win32' ? 'python' : 'python3', ['-c', py, ...yaml], { encoding: 'utf8' });
  if (run.status !== 0) problems.push(`YAML not checked, python with PyYAML is missing: ${(run.stderr || run.error?.message || '').trim().split('\n').pop()}`);
  else problems.push(...run.stdout.split('\n').filter(Boolean));
}
done('parse', problems);
