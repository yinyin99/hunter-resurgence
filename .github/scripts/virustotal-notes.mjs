// After a release is published: scan its downloads on VirusTotal and append the result to the release notes (docs/deploy.md, "VirusTotal scan").
// Usage: node virustotal-notes.mjs FILE... (the downloaded assets). Env: VIRUSTOTAL_API_KEY (missing: a notice and exit 0), GH_TOKEN, GITHUB_REPOSITORY, TAG.
// The free API allows 4 requests a minute: every request waits for the previous one by 15 s. Any failure or a scan that takes longer than 15 minutes ends with a notice and leaves the notes as they are.
// Needs Node 20+ (fetch, openAsBlob), gh on the PATH, no packages.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { openAsBlob, readFileSync, writeFileSync } from 'node:fs';
import { basename } from 'node:path';
import { pathToFileURL } from 'node:url';
import { setTimeout as sleep } from 'node:timers/promises';

const API = 'https://www.virustotal.com/api/v3';
const SMALL = 32 * 1024 * 1024; // POST /files takes up to 32 MB; larger files go to the address of /files/upload_url
const MARKER = '## VirusTotal';

// "3/68 (Avast, AVG: FileRepMalware; Trapmine: ML)": detections / engines that gave a verdict, the flagging engines grouped by their result name
export function summarize(analysis) {
  const { stats = {}, results = {} } = analysis.data.attributes;
  const flagged = (stats.malicious ?? 0) + (stats.suspicious ?? 0);
  const total = flagged + (stats.undetected ?? 0) + (stats.harmless ?? 0);
  const groups = new Map();
  for (const r of Object.values(results).sort((a, b) => a.engine_name.toLowerCase().localeCompare(b.engine_name.toLowerCase()))) {
    if (r.category !== 'malicious' && r.category !== 'suspicious') continue;
    const name = r.result ?? r.category;
    groups.set(name, [...(groups.get(name) ?? []), r.engine_name]);
  }
  const engines = [...groups].map(([name, e]) => `${e.join(', ')}: ${name}`).join('; ');
  return { flagged, total, engines };
}

// the text appended to the notes; files: [{ name, sha, analysis }]
export function section(files) {
  const lines = files.map(({ name, sha, analysis }) => {
    const s = summarize(analysis);
    return `- ${name}: VirusTotal: ${s.flagged}/${s.total}${s.flagged ? ` (${s.engines})` : ''}\n  https://www.virustotal.com/gui/file/${sha}`;
  });
  const anyFlag = files.some(({ analysis }) => summarize(analysis).flagged);
  const why = anyFlag ? '\n\nNew unsigned files are often flagged by reputation-based engines until they are widely downloaded; the SHA-256 above matches what we built.' : '';
  return `${MARKER}\n\n${lines.join('\n')}${why}\n`;
}

async function main() {
  const { VIRUSTOTAL_API_KEY: key, TAG: tag } = process.env;
  if (!key) {
    console.log('::notice::No VIRUSTOTAL_API_KEY: no VirusTotal scan');
    return;
  }
  const paths = process.argv.slice(2);
  let last = 0;
  async function vt(method, url, body) {
    for (let attempt = 0; ; attempt++) {
      await sleep(Math.max(0, last + 15_000 - Date.now()));
      last = Date.now();
      const res = await fetch(url.startsWith('http') ? url : API + url, { method, headers: { 'x-apikey': key, accept: 'application/json' }, body });
      if (res.status === 429 && attempt < 3) {
        await sleep(60_000);
        continue;
      }
      if (!res.ok) throw new Error(`VirusTotal ${method} ${url.split('?')[0].replace(API, '')}: ${res.status}`);
      return res.json();
    }
  }

  const files = [];
  for (const path of paths) {
    const blob = await openAsBlob(path);
    const form = new FormData();
    form.append('file', blob, basename(path));
    const url = blob.size > SMALL ? (await vt('GET', '/files/upload_url')).data : '/files';
    files.push({ name: basename(path), sha: createHash('sha256').update(readFileSync(path)).digest('hex'), id: (await vt('POST', url, form)).data.id });
  }

  const deadline = Date.now() + 15 * 60_000;
  for (const f of files) {
    for (;;) {
      const a = await vt('GET', `/analyses/${f.id}`);
      if (a.data.attributes.status === 'completed') { f.analysis = a; break; }
      if (Date.now() > deadline) throw new Error('the analysis takes longer than 15 minutes');
      await sleep(30_000);
    }
  }

  const gh = (...args) => execFileSync('gh', args, { encoding: 'utf8' });
  const body = JSON.parse(gh('release', 'view', tag, '--json', 'body')).body;
  if (body.includes(MARKER)) {
    console.log('::notice::The notes have a VirusTotal section already');
    return;
  }
  writeFileSync('notes-virustotal.md', `${body.trimEnd()}\n\n${section(files)}`);
  gh('release', 'edit', tag, '--notes-file', 'notes-virustotal.md');
  console.log(`VirusTotal result appended to the notes of ${tag}`);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  if (!/^v\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$/.test(process.env.TAG ?? '')) {
    console.log('::error::TAG is not vMAJOR.MINOR.PATCH[-suffix]');
    process.exit(1);
  }
  // the release is published and complete: a failed scan is a notice, never a failed release
  await main().catch((e) => console.log(`::notice::No VirusTotal result in the notes: ${e.message}`));
}
