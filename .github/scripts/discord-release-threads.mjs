// After a release is published: resolve the Discord forum threads of what it ships (docs/deploy.md, "Discord threads at release").
// The release is the commits from the previous stable release to TAG; each commit's merged pull requests and the issues they close hold the thread links.
// Env: DISCORD_BOT_TOKEN, DISCORD_GUILD_ID (both missing: a notice and exit 0), GH_TOKEN, GITHUB_REPOSITORY, TAG (vX.Y.Z), FROM (optional: the range starts at this tag instead).
// Needs Node 20+ (fetch), no packages.
import { setTimeout as sleep } from 'node:timers/promises';

const { DISCORD_BOT_TOKEN: token, DISCORD_GUILD_ID: guild, GH_TOKEN: ghToken, GITHUB_REPOSITORY: repo, TAG: tag, FROM: fromTag } = process.env;
const DISCORD = 'https://discord.com/api/v10';
const GITHUB = 'https://api.github.com';
const THREAD = 11, FORUM = 15;
const TRUSTED = ['OWNER', 'MEMBER', 'COLLABORATOR'];

// forum name -> the tag of a resolved thread, and the word of the reply
const FORUMS = { 'bug-reports': { tag: 'Resolved', verb: 'Fixed' }, ideas: { tag: 'Done', verb: 'Done' } };
const STATUS = { 'bug-reports': ['Open', 'In progress', 'Resolved', "Won't fix"], ideas: ['Idea', 'Planned', 'Done', 'Not planned'] };

if (!token || !guild) {
  console.log('::notice::No DISCORD_BOT_TOKEN or DISCORD_GUILD_ID: no thread updated');
  process.exit(0);
}
if (!/^v\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$/.test(tag ?? '')) {
  console.log('::error::TAG is not vMAJOR.MINOR.PATCH[-suffix]');
  process.exit(1);
}
if (tag.includes('-')) {
  console.log('::notice::A prerelease: no thread updated'); // the launcher updates itself from stable releases only
  process.exit(0);
}

// no thread or issue id is logged: the log of a public repository is public
const scrub = (s) => s.replace(/\d{15,}/g, '<id>');

async function github(path, query) {
  const res = await fetch(path.startsWith('http') ? path : GITHUB + path, {
    method: query ? 'POST' : 'GET',
    headers: { Authorization: `Bearer ${ghToken}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28', 'User-Agent': 'discord-release-threads' },
    body: query && JSON.stringify(query),
  });
  if (!res.ok) throw new Error(`GitHub ${path.split('?')[0]}: ${res.status} ${(await res.text()).slice(0, 300)}`);
  return res.json();
}

async function discord(method, path, body) {
  for (let attempt = 0; ; attempt++) {
    const res = await fetch(DISCORD + path, {
      method,
      headers: { Authorization: `Bot ${token}`, 'Content-Type': 'application/json', 'User-Agent': `DiscordBot (https://github.com/${repo}, 1)` },
      body: body && JSON.stringify(body),
    });
    if (res.status === 429 && attempt < 5) {
      const { retry_after = 1 } = await res.json().catch(() => ({}));
      await sleep(Math.ceil(retry_after * 1000));
      continue;
    }
    if (!res.ok) throw new Error(`Discord ${method} ${scrub(path)}: ${res.status} ${(await res.text()).slice(0, 300)}`);
    return res.status === 204 ? null : res.json();
  }
}

// the stable release before TAG, newest first (drafts and prereleases are not what a player had)
async function previousTag() {
  const stable = (await github(`/repos/${repo}/releases?per_page=100`)).filter((r) => !r.draft && !r.prerelease);
  const i = stable.findIndex((r) => r.tag_name === tag);
  if (i < 0) throw new Error(`no published release ${tag}`);
  return stable[i + 1]?.tag_name;
}

async function commitsOf(base) {
  const shas = [];
  for (let page = 1, total = 1; shas.length < total; page++) {
    const { commits, total_commits } = await github(`/repos/${repo}/compare/${encodeURIComponent(base)}...${encodeURIComponent(tag)}?per_page=100&page=${page}`);
    if (!commits.length) break;
    shas.push(...commits.map((c) => c.sha));
    total = total_commits;
  }
  return shas;
}

// the bodies that may hold a thread link: merged pull requests' (the maintainer merged them), and their closed issues' when a maintainer opened them
async function bodies() {
  const [owner, name] = repo.split('/');
  const main = (await github(`/repos/${repo}`)).default_branch;
  const base = fromTag || (await previousTag());
  if (!base) {
    console.log('::notice::No earlier release: nothing to compare');
    return [];
  }
  const prs = new Map();
  for (const sha of await commitsOf(base)) {
    for (const pr of await github(`/repos/${repo}/commits/${sha}/pulls`)) if (pr.merged_at && pr.base.ref === main) prs.set(pr.number, pr);
  }
  const out = [];
  let issues = 0;
  for (const pr of prs.values()) {
    out.push(pr.body ?? '');
    const q = 'query($o:String!,$n:String!,$p:Int!){repository(owner:$o,name:$n){pullRequest(number:$p){closingIssuesReferences(first:50){nodes{body authorAssociation repository{nameWithOwner}}}}}}';
    const { data, errors } = await github(`${GITHUB}/graphql`, { query: q, variables: { o: owner, n: name, p: pr.number } });
    if (errors) throw new Error(`GitHub GraphQL: ${JSON.stringify(errors).slice(0, 300)}`);
    for (const i of data.repository.pullRequest.closingIssuesReferences.nodes) {
      if (i.repository.nameWithOwner.toLowerCase() !== repo.toLowerCase() || !TRUSTED.includes(i.authorAssociation)) continue;
      issues++;
      out.push(i.body ?? '');
    }
  }
  console.log(`Release ${tag}: ${prs.size} merged pull request(s), ${issues} issue(s) they close from a maintainer`);
  return out;
}

async function resolve(id) {
  const thread = await discord('GET', `/channels/${id}`);
  if (thread.type !== THREAD || thread.guild_id !== guild) return console.log('Skipped a link: not a thread of the server');
  const forum = await discord('GET', `/channels/${thread.parent_id}`);
  if (forum.type !== FORUM || !Object.hasOwn(FORUMS, forum.name)) return console.log('Skipped a link: not a thread of bug-reports or ideas');

  const { tag: done, verb } = FORUMS[forum.name];
  const target = forum.available_tags.find((t) => t.name === done);
  if (!target) throw new Error(`the forum ${forum.name} has no tag "${done}"`);
  if ((thread.applied_tags ?? []).includes(target.id)) return console.log(`Skipped a thread of ${forum.name}: already ${done}`);

  const status = forum.available_tags.filter((t) => STATUS[forum.name].includes(t.name)).map((t) => t.id);
  const applied_tags = [...(thread.applied_tags ?? []).filter((t) => !status.includes(t)), target.id];
  await discord('POST', `/channels/${id}/messages`, {
    content: `✅ ${verb} in ${tag}: https://github.com/${repo}/releases/tag/${tag}. The launcher updates itself.`,
    allowed_mentions: { parse: [] },
  });
  await discord('PATCH', `/channels/${id}`, { applied_tags, archived: true }); // not locked: a reply from the reporter unarchives it
  console.log(`Resolved a thread of ${forum.name}: ${done}`);
}

// a link is https://discord.com/channels/<guild id>/<thread id>, maybe with a message id after
const links = new Set();
try {
  for (const body of await bodies()) {
    for (const [, g, id] of body.matchAll(/https:\/\/discord\.com\/channels\/(\d+)\/(\d+)/g)) {
      if (g === guild) links.add(id);
      else console.log('Skipped a link: another server');
    }
  }
} catch (e) {
  console.log(`::error::${e.message}`);
  process.exit(1);
}
if (!links.size) console.log('No Discord thread link in the release: nothing to do');

for (const id of links) {
  try {
    await resolve(id);
  } catch (e) {
    console.log(`::error::${e.message}`);
    process.exitCode = 1;
  }
}
