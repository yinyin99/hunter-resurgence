# Changing the mission reader

The reader (`Reader` in `server/Server/MissionConditions.cs`) turns an objective's English title into conditions, and a change can silently change what other titles mean. The recorded catalogue is not in the repo, so no test sees it: the maintainer's catalogue does.

A pull request that changes what the reader reads (a regex, a name table, a stop word) has in its body the diff of `coverage --rules` (docs/server.md, "Reading the rules") before and after the change, from the maintainer's catalogue, and the warnings that appeared or went. If you cannot run it against the catalogue, say so in the body and give the invented rows the tests use.

A new condition (a new `Check`, or new words for one in `Describe` of `server/Server/MissionRules.cs`) also gets its words in `TestIn` of `server/Server/MissionOverrides.cs`, so the operator's `mission_rules.json` can state it (docs/server.md, "Overridden rules"), and a title that has it in `MissionOverrideTests.TestEveryPrintedRuleReadsBackAsTheSameRule`, which reads every printed rule back.
