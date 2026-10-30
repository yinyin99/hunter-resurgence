# AGENTS.md

This is a public repo. The rules in [CONTRIBUTING.md](CONTRIBUTING.md#rules) apply to every file and commit message: the game is named only in README.md, and no real hosts, ids, handles or tokens appear anywhere.

## Where things are

- The project's words are defined in `GLOSSARY.md`. Use them in code, docs, issues and commits.
- How to build, test and release, and what CI does: `CONTRIBUTING.md`.
- One doc per component:
  - `docs/backup.md`: `tools/backup`
  - `docs/launcher.md`: `src/Launcher`
  - `docs/website.md`: `src/Web` and `src/Ui`
  - `docs/restore.md`: `src/Restore`
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
- A task is finished when the build and tests pass, every Acceptance criterion is met and ticked in the issue, a PR with `Closes #<n>` in its body is open, and the `pr-reviewer` subagent says "ready to merge" (fix and run it again, at most 2 rounds). Then stop and wait: the maintainer reviews and merges (merge commit).
- PR conversation and review summaries: `gh pr view <n> --comments`. Inline review comments: `gh api repos/{owner}/{repo}/pulls/<n>/comments`.

## Gotchas

- Changing UI text means changing every translation. The backup tool has six in `tools/backup/Strings*.resx`; the launcher and website have 19 in `src/Ui/Strings*.resx`. `TranslationsTests` checks that every file has the same keys and `{0}` placeholders.
- The backup tool's "What isn't saved?" dialog (resx keys `NotCovered_<n>`) mirrors the "What is not saved" section of `docs/backup.md`. Change both together.

## Agent skills

### Issue tracker

GitHub Issues of this repo, through the `gh` CLI. See `docs/agents/issue-tracker.md`.

### Triage labels

The five default labels: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single context: `GLOSSARY.md` at the root and ADRs in `docs/adr/`. See `docs/agents/domain.md`.
