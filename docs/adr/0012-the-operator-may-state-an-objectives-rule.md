# The operator may state an objective's rule

Status: accepted

Spec: [#835](https://github.com/yinyin99/hunter-resurgence/issues/835)

Amends rule 1 of [ADR 0007](0007-the-mission-model.md). **Titles, texts and rewards still come only from the operator's recorded catalogue**, and the server still has no missions of its own. What an objective asks of an event, its rule, is the server's reading of its English title, except where the operator's file `mission_rules.json`, beside the recorded catalogue, states the rule of that one objective. The file is the operator's data, like the recording, and is not in the repository.

- An entry is keyed by mission id and objective id and is written in the words `coverage --rules` prints, so the rule that is checked is the rule that is read ([docs/server.md](../server.md#missions), "Overridden rules"). It replaces the reader's rule of that objective whole.
- The file is for single objectives the reader gets wrong or cannot read: a typo, one place, one named animal, a weapon the title names loosely. Wordings that many titles share stay the reader's to learn.
- Loading fails loud: an entry for an objective the catalogue does not have, a word that is no part of a rule, or a name no item, species, coat or reserve of the catalogue has, stops the start with the entry's key. Nothing in the file is skipped.
- An overridden rule is judged like any other: by the hunt's events, at the start's re-check of harvests already made, by `admin missions-rollback`, `missions-audit` and `missions-stuck`. One judgement, whatever the rule's source.

## Considered options

- **Teach the reader every single case.** Rejected for the tail: each costs reader code and tests for one objective, and a wrong reading needs a new server version to fix.
- **Rules in our own format.** Rejected: a second language beside the printed one would drift from it; with the printed words an operator copies a line of `coverage --rules`, edits it and sees the result in the next print.
- **Ship the entries in the repository.** Rejected: they name the operator's catalogue, and every other catalogue (the tests', a fork's) would fail the start on them.
- **Skip a bad entry with a warning.** Rejected: a skipped entry silently leaves an objective unmet or met by the wrong event.

## Consequences

- A changed or new entry is judged at the next start: the re-check completes objectives from the harvests and clues already made, and `admin missions-rollback` reopens a finish the new rule no longer meets.
- A new kind of condition in the reader needs its words in the file's parser too, so the file can state it.
