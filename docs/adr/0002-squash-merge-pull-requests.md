# Squash-merge pull requests

Status: accepted

Every pull request, a task's or Dependabot's, is merged with `gh pr merge <n> --squash --delete-branch` (`--admin` added only where [deploy.md](../deploy.md) says so) and becomes one commit on `main`. That commit's subject is the PR title plus GitHub's ` (#<n>)`; its body lists the messages of the branch's commits. Measured on 2026-11-03: the last 40 merge commits on `main` all have the subject "Merge pull request #N from <owner>/<branch>", so `git log --first-parent` shows no Conventional subject. Review fixes reach `main` as commits of their own: #183 has three "fix: review of #183" commits, and #176, #170 and #111 have similar ones; #162 brought in two merges of `origin/main`. Agents spend work keeping branch history clean (fixup and autosquash, "rebase and squash"), which squash makes unneeded. And every commit on `main` is then a state that CI built and tested, so `git bisect` and `git revert <sha>` work with no `--first-parent` and no `-m 1`.

## Considered options

- **Merge commits (the method until now).** Keeps every one-purpose commit, and `git blame` inside a big PR shows the exact commit: #110 had 21 commits and #105 had 16. Rejected: the subjects on `main` are GitHub's merge subjects, and review-fix and back-merge commits stay in the history. The single commits stay readable in the PR.
- **Rebase merge.** A linear history, but every branch commit, review fixes included, lands on `main` one by one, untested. Rejected.
- **Squash with the PR body or the commit list as the commit message.** Rejected: the body is Markdown written for review (checklists, `Closes #n`), and the commit list repeats what the PR already shows. The squash commit is its title only; ` (#NNN)` links to both.
- **Squash by convention, all three methods still allowed.** Rejected: one `--merge` by mistake brings the old history back. The setting and the ruleset make it impossible.

## Consequences

- The PR title is a Conventional Commits subject of at most 65 characters, so the subject is at most 72 with ` (#NNN)`. The PR has one purpose ([CONTRIBUTING.md](../../CONTRIBUTING.md), "Commits"). Dependabot writes its own titles (`build: Bump …`), and they are kept as they are, even when one is a few characters longer.
- Repository setting: merge commits and rebase merges are off; the squash title is `PR_TITLE` and the squash message is `BLANK`. The ruleset `main: reviewed changes` has `allowed_merge_methods: ["squash"]`.
- Dependabot pull requests squash too: the bump and the "build: regenerate lock files" commit become one `build: …` commit ([deploy.md](../deploy.md)).
- Release notes do not change: they come from the annotated tag's message (`release.yml`). `git log --oneline <last tag>..origin/main` now gives one line per PR to write it from.
- A branch built on another branch that has not been merged yet needs a rebase onto `main` after its base is squashed, because the base's commits are not on `main`.
- `git blame` and `git log` on `main` show the PR, not the single commit inside it. The single commits and their messages are in the PR on GitHub.
- Revisit with a new ADR if a PR needs its separate commits on `main` (for example, a large import that is bisected commit by commit).
