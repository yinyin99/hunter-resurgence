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
