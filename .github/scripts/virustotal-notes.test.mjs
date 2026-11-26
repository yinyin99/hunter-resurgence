// node --test .github/scripts: the parsing of a VirusTotal analysis (the fields of GET /analyses/{id} that the script reads) and the text it appends to the notes.
import assert from 'node:assert/strict';
import { test } from 'node:test';
import { section, summarize } from './virustotal-notes.mjs';

const analysis = (stats, results) => ({ data: { attributes: { status: 'completed', stats, results } } });
const engine = (engine_name, category, result) => ({ [engine_name]: { engine_name, category, result } });

const flagged = analysis(
  { malicious: 3, suspicious: 0, undetected: 60, harmless: 5, timeout: 1, 'type-unsupported': 4 },
  { ...engine('Trapmine', 'malicious', 'ML'), ...engine('AVG', 'malicious', 'FileRepMalware'), ...engine('Avast', 'malicious', 'FileRepMalware'), ...engine('Other', 'undetected', null) },
);
const clean = analysis({ malicious: 0, suspicious: 0, undetected: 70, harmless: 0 }, engine('Other', 'undetected', null));

test('summarize counts the verdicts and groups the flagging engines by result', () => {
  assert.deepEqual(summarize(flagged), { flagged: 3, total: 68, engines: 'Avast, AVG: FileRepMalware; Trapmine: ML' });
  assert.deepEqual(summarize(clean), { flagged: 0, total: 70, engines: '' });
});

test('summarize counts suspicious as flagged and names a result-less engine by its category', () => {
  const s = summarize(analysis({ malicious: 0, suspicious: 1, undetected: 9, harmless: 0 }, engine('Solo', 'suspicious', null)));
  assert.deepEqual(s, { flagged: 1, total: 10, engines: 'Solo: suspicious' });
});

test('section: links and results, the reputation sentence only when an engine flagged a file', () => {
  const a = 'a'.repeat(64), b = 'b'.repeat(64);
  const both = section([{ name: 'Setup.exe', sha: a, analysis: flagged }, { name: 'backup.zip', sha: b, analysis: clean }]);
  assert.match(both, /^## VirusTotal\n/);
  assert.ok(both.includes(`- Setup.exe: VirusTotal: 3/68 (Avast, AVG: FileRepMalware; Trapmine: ML)\n  https://www.virustotal.com/gui/file/${a}`));
  assert.ok(both.includes(`- backup.zip: VirusTotal: 0/70\n  https://www.virustotal.com/gui/file/${b}`));
  assert.ok(both.includes('reputation-based engines'));
  assert.ok(!section([{ name: 'backup.zip', sha: b, analysis: clean }]).includes('reputation-based'));
});
