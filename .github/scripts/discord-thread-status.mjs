// On an issue closed or reopened: reply in the Discord forum thread linked in the issue's body and set its status tag (docs/deploy.md, "Discord thread status").
// Reads the event from GITHUB_EVENT_PATH; the issue's title and body are untrusted and never reach a shell. Needs Node 20+ (fetch), no packages.
// Env: DISCORD_BOT_TOKEN, DISCORD_GUILD_ID (both missing: a notice and exit 0), GITHUB_EVENT_PATH, GITHUB_REPOSITORY.
import { readFileSync } from 'node:fs';
import { setTimeout as sleep } from 'node:timers/promises';

const { DISCORD_BOT_TOKEN: token, DISCORD_GUILD_ID: guild, GITHUB_EVENT_PATH: eventPath, GITHUB_REPOSITORY: repo } = process.env;
const API = 'https://discord.com/api/v10';
const THREAD = 11, FORUM = 15;

// forum name -> outcome -> status tag name; the outcomes are `completed` (closed as completed), `closed` (any other reason) and `reopened`
const TAGS = {
  'bug-reports': { completed: 'Resolved', closed: "Won't fix", reopened: 'Open' },
  ideas: { completed: 'Done', closed: 'Not planned', reopened: 'Idea' },
};
const DONE = { 'bug-reports': 'Fixed', ideas: 'Done' };
const text = {
  completed: (forum, n, url) => `✅ ${DONE[forum]} in #${n} (${url}). It reaches you with the next launcher or server update.`,
  closed: (_, n, url) => `Closed as not planned: #${n} (${url}).`,
  reopened: (_, n, url) => `🔄 Reopened: we're looking at this again (#${n}, ${url}).`,
};

if (!token || !guild) {
  console.log('::notice::No DISCORD_BOT_TOKEN or DISCORD_GUILD_ID: no thread updated');
  process.exit(0);
}

const { action, issue } = JSON.parse(readFileSync(eventPath, 'utf8'));
const outcome = action === 'reopened' ? 'reopened' : issue.state_reason === 'completed' ? 'completed' : 'closed';
const url = `https://github.com/${repo}/issues/${issue.number}`;

// thread ids are not logged: the log of a public repository is public
async function api(method, path, body) {
  for (let attempt = 0; ; attempt++) {
    const res = await fetch(API + path, {
      method,
      headers: { Authorization: `Bot ${token}`, 'Content-Type': 'application/json', 'User-Agent': `DiscordBot (https://github.com/${repo}, 1)` },
      body: body && JSON.stringify(body),
    });
    if (res.status === 429 && attempt < 5) {
      const { retry_after = 1 } = await res.json().catch(() => ({}));
      await sleep(Math.ceil(retry_after * 1000));
      continue;
    }
    if (!res.ok) throw new Error(`${method} ${path.replace(/\d{15,}/g, '<id>')}: ${res.status} ${(await res.text()).slice(0, 300)}`);
    return res.status === 204 ? null : res.json();
  }
}

async function update(id) {
  const thread = await api('GET', `/channels/${id}`);
  if (thread.type !== THREAD || thread.guild_id !== guild) return console.log('Skipped a link: not a thread of the server');
  const forum = await api('GET', `/channels/${thread.parent_id}`);
  if (forum.type !== FORUM || !Object.hasOwn(TAGS, forum.name)) return console.log('Skipped a link: not a thread of bug-reports or ideas');

  const target = forum.available_tags.find((t) => t.name === TAGS[forum.name][outcome]);
  if (!target) throw new Error(`the forum ${forum.name} has no tag "${TAGS[forum.name][outcome]}"`);
  const status = new Set(Object.values(TAGS[forum.name]));
  const keep = (thread.applied_tags ?? []).filter((t) => !forum.available_tags.some((a) => a.id === t && status.has(a.name)));
  const applied_tags = [...keep, target.id];
  const post = () => api('POST', `/channels/${id}/messages`, { content: text[outcome](forum.name, issue.number, url), allowed_mentions: { parse: [] } });

  if (outcome === 'reopened') {
    await api('PATCH', `/channels/${id}`, { archived: false, applied_tags });
    await post();
  } else {
    await post();
    await api('PATCH', `/channels/${id}`, { applied_tags, archived: true }); // not locked: a reply from the reporter unarchives it
  }
  console.log(`Updated a thread of ${forum.name}: ${target.name}`);
}

// a link is https://discord.com/channels/<guild id>/<thread id>, maybe with a message id after
const links = new Set();
for (const [, g, id] of (issue.body ?? '').matchAll(/https:\/\/discord\.com\/channels\/(\d+)\/(\d+)/g)) {
  if (g === guild) links.add(id);
  else console.log('Skipped a link: another server');
}
if (!links.size) console.log('No Discord thread link in the issue: nothing to do');

for (const id of links) {
  try {
    await update(id);
  } catch (e) {
    console.log(`::error::${e.message}`);
    process.exitCode = 1;
  }
}
