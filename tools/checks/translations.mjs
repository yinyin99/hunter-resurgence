// node tools/checks/translations.mjs [files...]: for every Strings.resx a file names (or any of its Strings.<lang>.resx), each language file has exactly the keys of the base
// and the same {0} or {name} placeholders in each string, and no stray brace (a FormatException at run time). No arguments: every tracked .resx.
// Not checked here: a language file that does not exist (the test of src/Ui reads every language of Languages.All), or a key no file holds (ResxUsageTests).
import { readdirSync, readFileSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { done, files } from './lib.mjs';

const LANG = /\.[a-z]{2,3}(-[A-Za-z]{2,4})?\.resx$/;
const bases = new Set(files().filter((f) => f.endsWith('.resx')).map((f) => f.replace(LANG, '.resx')));

const read = (f) => {
  const names = [...readFileSync(f, 'utf8').matchAll(/<data\s+name="([^"]+)"[^>]*>\s*<value>([\s\S]*?)<\/value>/g)];
  return { map: new Map(names.map((m) => [m[1], m[2]])), dupes: names.length - new Set(names.map((m) => m[1])).size };
};
// "{{" and "}}" are literal braces; what is left of a {...} is the placeholder's name or index
const bare = (s) => s.replaceAll('{{', '').replaceAll('}}', '');
const holes = (s) => [...bare(s).matchAll(/\{([^{}]*)\}/g)].map((m) => m[1].split(/[:,]/)[0]).sort().join(',');
const stray = (s) => /[{}]/.test(bare(s).replace(/\{[^{}]*\}/g, ''));

const problems = [];
for (const base of bases) {
  const stem = basename(base, '.resx'), dir = dirname(base);
  const en = read(base);
  if (en.dupes) problems.push(`${base}: ${en.dupes} duplicate key(s)`);
  for (const name of readdirSync(dir).filter((n) => n.startsWith(stem + '.') && LANG.test(n)).sort()) {
    const f = join(dir, name).replaceAll('\\', '/'), tr = read(f);
    if (tr.dupes) problems.push(`${f}: ${tr.dupes} duplicate key(s)`);
    for (const k of en.map.keys()) if (!tr.map.has(k)) problems.push(`${f}: missing ${k}`);
    for (const [k, v] of tr.map) {
      if (!en.map.has(k)) problems.push(`${f}: extra ${k}`);
      else if (holes(v) !== holes(en.map.get(k))) problems.push(`${f}: ${k} has placeholders {${holes(v)}}, English has {${holes(en.map.get(k))}}`);
      else if (stray(v)) problems.push(`${f}: ${k} has a stray brace`);
    }
  }
}
done('translations', problems);
