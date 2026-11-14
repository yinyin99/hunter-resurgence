# AGENTS.md

This is a public repo. The rules in [CONTRIBUTING.md](CONTRIBUTING.md#rules) apply to every file and commit message: the game is named only in README.md and in `server/Server/ProtocolLiterals.cs` (the names the client requires), and no real hosts, ids, handles or tokens appear anywhere.

## Where things are

- The project's words are defined in `GLOSSARY.md`. Use them in code, docs, issues and commits.
- How to build, test and release, and what CI does: `CONTRIBUTING.md`.
- One doc per component:
  - `docs/backup.md`: `tools/backup`
  - `docs/launcher.md`: `src/Launcher`
  - `docs/website.md`: `src/Web` and `src/Ui`
  - `docs/restore.md`: `src/Restore`
  - `docs/server.md`: `server/Server`
  - `docs/deploy.md`: `deploy/` and the workflows
- A change in behaviour updates its doc in the same pull request.

## Working

- Every task gets its own git worktree and branch from `origin/main`: `git fetch --prune`, then `git worktree add ../<name> -b <type>/<name> origin/main`. The main checkout stays on `main`; other work may be in progress there.
- Every edit and command targets the worktree by absolute path, so nothing lands in the main checkout by accident.
- Push from the task's worktree (`git push -u origin <branch>`). The pre-push hook runs there too and scans that worktree; it prints `clean` on success. Never use `--no-verify`.
- After the merge, remove the worktree (`git worktree remove ../<name>`) and the local branch.

## Tasks

- A task is a GitHub issue; its number `<n>` is the task id. The issue is the task record: Goal, Acceptance criteria, Plan (checkboxes) and Decisions in its body, progress as comments. Issues and PRs are public: the rules above apply to their text too.
- `/new-task <n>` creates the task's worktree and branch: the name is `<n>-<slug>`. Before a session ends, `/checkpoint <n>` saves progress to the issue; `/resume-task <n>` picks it up.
- When a plan is approved, copy it into the issue's Plan section as checkboxes before the first edit, and tick each step when it is done. If planning shows the Goal or an Acceptance criterion is wrong (for example, something it asks for already exists), correct that section; note the change under Decisions.
- A task is finished when the build and tests pass, every Acceptance criterion is met and ticked in the issue, a PR with `Closes #<n>` in its body and a Conventional Commits title (CONTRIBUTING.md, "Commits": it becomes the squash commit's subject) is open, and the `pr-reviewer` subagent says "ready to merge" (fix and run it again, at most 2 rounds). Then stop and wait: merge with `gh pr merge <n> --squash --delete-branch` ([ADR 0002](docs/adr/0002-squash-merge-pull-requests.md)) only when the maintainer says so.
- PR conversation and review summaries: `gh pr view <n> --comments`. Inline review comments: `gh api repos/{owner}/{repo}/pulls/<n>/comments`.

## Gotchas

- Changing UI text means changing every translation. The backup tool has six in `tools/backup/Strings*.resx`; the launcher and website have 19 in `src/Ui/Strings*.resx`. `TranslationsTests` checks that every file has the same keys and `{0}` placeholders; for `src/Ui` it also fails when a key keeps its English text in all 18 translations, so a new key must be translated, not copied.
- The player kit lives in the launcher (`src/Launcher`); its texts are the `Kit.*` keys in the 19 `src/Ui/Strings*.resx`, checked by `LauncherApp/TranslationsTests`. The launcher's `settings.example.json` lists `server_url` and the `*_host` keys the game asks for (`game_web_host`, `crash_host`); they are the server's keys of the same names (`server/Server/settings.example.json`): change them together.
- The backup tool's "What isn't saved?" dialog (resx keys `NotCovered_<n>`) mirrors the "What is not saved" section of `docs/backup.md`. Change both together.
- The website's backup tool dialog (`src/Ui` resx keys `Backup.Step<n>` and `Backup.SmartScreen`) mirrors the backup tool's steps and quotes its `Public_Radio` and `Start` labels; `Restore.Rule1` quotes its `Own_Radio` label ("My account", in English where the backup tool has no translation). Change them together.
- The footer's credit "Hunter Resurgence © yinyin99" (`src/Ui/Shell/Footer.razor`) is the attribution README's AGPL-3.0 section 7(b) term requires. Never remove or reword it; a change to it changes README too.

## Agent skills

### Issue tracker

GitHub Issues of this repo, through the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single context: `GLOSSARY.md` at the root and ADRs in `docs/adr/`. See `docs/agents/domain.md`.
