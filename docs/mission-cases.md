# Mission cases

Every mission objective is a title in English; the server turns it into a rule ([server.md](server.md#missions)). A **case** says, from the title's wording alone, what must finish the objective and what must not. `MissionCaseTests` plays every case through the server's real mission engine and the real rules, and fails when the server disagrees with the wording.

Write the cases from the title, **never from the rule the server prints** (`coverage --rules`): the point is to catch the rules that read the title wrong.

## Where things are

- **The real cases are not in this repository.** They hold the missions' real wording and the catalogue's names, which are the operator's data ([CONTRIBUTING.md](../CONTRIBUTING.md#rules)), so they live in the operator's private repository, one file per block of about 50 missions, `missions-<first>-<last>.json` (the mission ids from `<first>` to `<last>`), so two authors never edit the same file. An objective is in one file only and every objective of the catalogue has cases (the coverage test fails otherwise, for the real catalogue as for the sample's). The files hold ids, a hash of each title and the scenarios, never the titles.
- **The runner, the format and a sample are here**: `server/Server.Tests/MissionCaseTests.cs`, and in `server/Server.Tests/mission-cases/` an invented catalogue (`catalogue.json`, missions, species, items and places over the test fixture's), its overrides (`mission_rules.json`) and its cases (`cases/`), which exercise every condition and objective kind so that CI proves the runner.
- **The real run** takes three paths from the environment: `MISSION_CASES`, the folder of the real case files; `MISSION_CATALOGUE`, the catalogue the server loads (the `Application/application` reply of the operator's recording, as JSON); `MISSION_RULES`, the operator's `mission_rules.json`, which replaces the rules of single objectives as on the server ([server.md](server.md#missions), "Overridden rules"). Use the live server's file: a case judges the rule the players get. Without the first two the real cases are skipped with a message saying so; without `MISSION_RULES` the reader's rules are judged alone.

Run one file's real cases (the summary of known disagreements and doubts is in the output):

```
MISSION_CASES=<cases folder> MISSION_CATALOGUE=<app.json> MISSION_RULES=<mission_rules.json> dotnet test server/Server.Tests -c Release --filter-display-name "*missions-0130-0179.json*" --output Detailed
```

All of them, with the sample and the coverage counts: `--filter-class Resurgence.Server.Tests.MissionCaseTests`. A file of 50 missions plays in a few seconds (about 5 ms a case, after 3 s to load the catalogue).

## The format

A file is a JSON list (`//` comments and trailing commas allowed) of one entry per objective. Two entries of the sample:

```json
[
  {"mission": 10, "objective": 0, "hash": "48c640d2",
   "pass": [{"harvest": "Test Elk", "gender": "female", "weapon": "Test Magnum Revolver", "ammo": "Test Magnum Round", "sight": "Test Handgun Scope"}],
   "also": [
    {"why": "the Test Revolver: the family of the Test Magnum Revolver (the catalogue's description says they share one skill)", "set": {"weapon": "Test Revolver"}}
   ],
   "fail": [
    {"why": "no scope", "set": {"sight": null}},
    {"why": "a bow, outside the revolver family", "set": {"weapon": "Test Recurve Bow"}}
   ]},
  {"mission": 20, "objective": 1, "hash": "9dda3142",
   "pass": [{"done": 0}, {"harvest": "Test Deer", "distance": 19, "pose": "standing"}],
   "fail": [
    {"why": "crouching", "set": {"pose": "crouching"}},
    {"why": "two shots", "set": {"hits": ["left lung+right lung", "left lung+right lung"]}},
    {"why": "20 m", "set": {"distance": 20}},
    {"why": "before the first", "steps": [{"harvest": "Test Deer", "distance": 19, "pose": "standing"}, {"harvest": "Test Deer", "weapon": "Test Recurve Bow", "distance": 41}]}
   ]}
]
```

(The titles: "Harvest a Test Elk cow with a scoped Test Magnum Revolver." and, after "First, harvest an unspooked male Test Deer with any Recurve Bow at over 40 meters.", "Then, harvest a male Test Deer with one shot while standing at under 20 meters.")

| Key | |
|---|---|
| `mission`, `objective` | the ids |
| `hash` | the first 8 hex digits of the SHA-256 of the title: when the title changes, the case fails until it is read again. Write `""` first: the failure prints the title and its hash |
| `pass` | the steps of one scenario that finishes the objective |
| `also` | other scenarios that finish it too (another weapon of the class the title names, the gender the title leaves open) |
| `fail` | the near misses: each differs from `pass` in **one** thing |
| `doubt` | the wording is ambiguous: say why, and write no cases (`pass` may stay out); the maintainer decides |
| `placed` | `true`: the server places the named animals (`spawn_named_animals` on) for every case of the objective; also a key of a variant, for that case alone (below) |
| `passKnown` | the pass case is a known disagreement (below) |

A variant (`also`, `fail`) has `why` (a short reason) and either `set`, the keys of one step of `pass` replaced (`null` removes one; `step` is the index of that step in `pass` as written, default the last), or `steps`, all its steps; `placed` runs it alone with the server's placement of named animals (below). Prefer `set`: it changes one thing by construction.

**Known disagreements.** A case where the server disagrees with the wording today gets `known` (`passKnown` for the pass): the reason and, once filed, the issue number. The test reports it instead of failing; when the server agrees again the test fails until the mark is removed. Never mark a case known without saying why the server is wrong: a `known` mark is for a real disagreement only (the sample keeps one, mission 27 objective 1, to prove the path). A mark that is no text, and an objective written twice in one file, are problem lines of the file, not a crash.

### Steps

Each case plays on a fresh account whose only active mission is the case's (its dependencies are not needed). Unless the first step is a `hunt`, a hunt starts in the mission's first reserve. Every step is one second after the one before.

| Step | Keys |
|---|---|
| `{"hunt": "<reserve>"}` | a new hunt in that reserve; `lodge`: the lodge or place it starts at (the catalogue's location name) |
| `{"wait": <minutes>}` | time passes |
| `{"done": <objective>}` | the `pass` steps of that objective of the same mission, here; it must still be done at the end (a single-hunt mission forgets it at the next hunt) |
| `{"enter": "<place>"}`, `{"leave": "<place>"}` | the player enters or leaves a place; `at`: the player's `[x, z]` |
| `{"harvest": "<species>"}` | a kill: the spawn, the hits and the harvest |
| `{"spot": "<species>"}` | an animal seen; `gender`, `coat`, `stand`, `place`, `animal` |
| `{"clue": "<species>"}` | a clue; `kind`: `footprints` (default), `droppings`, `call`, `blood`, `bedding`, `other` (rooting, water bubbles); `gender`, `coat`, `place`, `animal`; `at`: where it is found, `[x, z]` in metres |

A harvest's keys, and what they are when left out:

| Key | Default | |
|---|---|---|
| `gender` | `male` | `male` or `female` |
| `weapon`, `ammo`, `sight` | no weapon, the species' first permitted ammunition, no sight | the catalogue's item names; `Name (Variant)` for one skin |
| `distance` | 50 | metres of the killing shot, to the millimetre (`9.144`: the engine compares the hit's centimetres, 914.4, as they are) |
| `hits` | `"left lung+right lung"` | the organs of one shot joined by `+`, or a list with one entry per shot (the last kills): `heart`, `right lung`, `left lung`, `liver`, `brain`, `intestines`, `spine`, `shoulder blade`, `neck bones`, `skull`, `hip bone`. An entry may be an object that changes that hit alone (below) |
| `state` | `standing` | the animal at every hit: `standing`, `walking`, `running`, `trotting`, `flying` (spooked), `flying unspooked`, `swimming`, `charging`, `burrow`, or the game's number |
| `pose` | `standing` | the player: `standing`, `crouching`, `prone` |
| `stand` | `no stand` | `hunting tower`, `tree stand`, `ground blind`, `tripod`, `waterfowl blind`, `tripod rifle rest` |
| `value` | 100 | harvest value in % |
| `weight` | 90 | kilograms; with a `score` only for a species scored by its antlers, horns, tail or skull (a species scored by its weight gets the weight of its score) |
| `score` | | the trophy score, as the server's own scoring scores the kill, for any species that has one (below) |
| `points` | | typical points of both antlers together, main beams included (the left antler has the odd one). Without a `score`, every point is 500 mm long and nothing else is measured, so the score follows from the points (a Boone and Crockett score of 39.37 per point); write a `score` instead to set it |
| `abnormal` | 0 | non-typical (abnormal) points, besides `points` (8 when not given); they never count as typical points |
| `nonTypical` | | `true`: a non-typical rack (the species' variant 100 up, e.g. 101 for species 1): the server never counts its typical points |
| `beard`, `spurs`, `tusks` | | millimetres; `spurs` and `tusks` are both sides, or `[left, right]` for two lengths (`"tusks": [102, 95]`: the shorter is 95) |
| `coat` | `common` | the game's coat name (`albino`, `blonde`, `piebald` ...), or `coat <id>` (a Halloween coat: `server/facts/facts.json`, `events.halloween_2026.coats.<species>.possessed`) |
| `place` | | the player was in that place at the killing shot (also for a spot or a clue) |
| `at` | `[0, 0]` | the animal's `[x, z]` in metres (a title's `(X: -12.614, Y: -6.282)` is `[-12614, -6282]`) |
| `named` | | `true`: the named animal. With the server's placement off (the default) the game marks it as special; with `placed`, it is the animal the server placed for the objective (the population of that species nearest `at`) |
| `population` | | the population of an animal the server placed, by its number |
| `animal` | a new animal | a label: steps with the same label are the same animal (in one hunt) |
| `shot` | a new shot | a shot number: harvests with the same one were killed by the same shot |

**One hit differs.** An entry of `hits` that is an object changes that hit alone: `organs` (default both lungs) and any of `weapon`, `ammo`, `sight`, `distance`, `state`, `stand`, `pose`, `shot`; what it does not give is the step's. The conditions of the killing shot (weapon, ammunition, sight, distance, state, stand, stance) read the last hit, `spooked` and every other animal state with them; `unspooked` (unaware when first shot) reads the first; `one shot`, `organs only` and `without hitting` read every hit.

```json
{"harvest": "Test Elk", "weapon": "Test Recurve Bow", "hits": [{"weapon": "Test Bolt Rifle", "ammo": "Test Rifle Round"}, {}]}
{"harvest": "Test Fox", "hits": [{"state": "running"}, {"state": "standing"}]}
```

The first: a rifle wounded the elk, the bow killed it. The second: the fox was running at the first hit (not unspooked) and standing at the killing one (not spooked).

**Scores.** `score` makes the kill score what it asks by the server's own scoring (`Scoring`), for every species that has a score:

- a species scored by its weight (foxes, birds, the turkey, a boar's weight and tusks): the harness sets the weight; a species scored by its skull: the skull's size;
- a species scored by its antlers, horns or tail: the harness sends a measured trophy (main beams, points, circumferences, spreads, palms, a tail and spurs, all of one middling trophy) scaled up or down until its score is the one asked. Its `points` and `abnormal` are kept; `beard`, `spurs` or `tusks` with such a `score` are refused.

The measures are whole millimetres, so a measured trophy's score climbs in steps of up to a few tenths: `"score": 150` is the reachable score nearest 150 (refused when it is more than 0.5 away). A case at a limit says on which side it must fall:

| `score` | the kill scores | for |
|---|---|---|
| `">=150"` | 150, else the least score above it | the pass of `150 or more` |
| `"<150"` | the greatest score under 150 | the near miss of `150 or more` |
| `"<=150"` | 150, else the greatest score under it | the near miss of `more than 150` |
| `">150"` | the least score above 150 | the pass of `more than 150` |

A doe, a cow or a hen has no antlers and no score in the game; the harness still measures a trophy when the step asks a `score`, so a near miss of the gender leaves `score` out (`"set": {"gender": "female", "score": null}`) unless the title's gender word already refuses her.

**Named animals placed or not.** By default the server runs as with `spawn_named_animals` off: a named animal is met by its species (and coat, gender) near the title's place, within 1000 m. `"placed": true` on the entry or on a variant runs that case on an engine with it on: the server places the animal, and only that animal (`"named": true`) meets the objective. The near miss "an ordinary animal at the named animal's place" is then `{"why": "…", "placed": true, "set": {}}`.

**Multiplayer credit** (a harvest skinned by another member of the session, credited to the shooter) is not expressible yet: it needs a second account hosting or joining the case account's game and both sending heartbeats while it runs (as `MpHarvestTests` does), which the harness does not do.

Names are the catalogue's English names (species without `(Typical)`; reserves; items; locations). A place is a catalogue location's name (a tower is `Tower 92`, looked up in the hunt's reserve first) or the name the game sends for it (capital letters, as the sample's `TEST_PASS`) when no location of the catalogue is that place.

## How to read a title

1. Find the kind: harvest (`harvest`, `kill`, `take`), spot, clue (`locate`, `identify`, `ID`, `find`), arrive (`arrive`, `visit`, `head to`, `walk through`), start (`start at`, `begin at`).
2. List every condition the words ask: the animal (species, gender word: buck, bull, doe, cow, hen ...), the coat, the weapon or its class, the ammunition, the sight, the shot (organs, one shot), the distance, the weight, the score, the trophy, the animal's state, the player's stance and stand, the reserve, the place, the time, the count, the order.
3. Write the plainest scenario that meets all of them: `pass`. Leave every other key at its default.
4. For each condition write the near misses of the checklist below, and an `also` where the title allows more than the pass shows.
5. If a word can be read two ways, write a `doubt` instead.

## Checklist: the near misses each condition needs

| Condition | Mandatory near misses |
|---|---|
| species | another species of the same reserve; `also` for each species a short name or a list covers (`a Test Wolf or a Test Fox`) |
| gender | the other gender; when the title gives none, `also` with the other gender |
| weapon | a weapon outside the named weapon's family (see below); when the title names a class, `also` with another member of the class; when it names a weapon, `also` with another member of its family |
| ammunition | another ammunition the weapon fires; `without <ammo>`: that ammunition. An ammunition condition is never relaxed: whatever member of the family fired, it still needs that ammunition |
| permitted | an ammunition the species does not permit |
| one shot | two hits |
| organs | the killing shot elsewhere; `only` or `without`: an earlier hit on the part it refuses |
| harvest value | 99 % |
| distance, weight, points, beard, spurs, tusks | the bound itself (it fails `over`/`under`/`more than`, passes `at least`/`at most`) and one step past it; `both` spurs or tusks: one of them under the bound (`[102, 95]`) |
| score | both sides of the limit with the bound forms: `">=N"` and `"<N"` for `N or more`, `">N"` and `"<=N"` for `more than N` |
| typical points | one point fewer; the missing point given as an abnormal one (`"points": 7, "abnormal": 1`); a non-typical rack (`"nonTypical": true`) |
| first shot, killing shot | when the title speaks of the killing shot (weapon, ammunition, state), a first hit that has it and a killing hit that has not, and the other way round (`hits` objects) |
| state, first state (spooked, unspooked) | another state (`running` for unspooked, `standing` for airborne) |
| not state | each refused state |
| pose, stand | another pose; another stand and `no stand` (`"stand": null`) |
| sight | `scoped`: no sight; `without a scope`: a sight; a named sight: another one |
| coat, not coat | another coat; the refused coat |
| reserve | the same in another reserve (an explicit `hunt` as step 0, changed with `"step": 0`) |
| place, tower | the neighbouring place or tower; at the tower but on the ground |
| named animal | the same animal far from the place; another coat; with `placed`, an ordinary animal of the species at the place |
| clue | another kind when the title names one; one clue fewer; a clue of another animal when it says the same animal |
| several animals, a time | one animal twice; just past the time (more than 10 s past it: the server gives that grace) |
| one shot for several | two shots |
| order (`Then`, `Finally`, `After ...`) | the objective before the one it follows, or that one skipped |
| same hunt, `another` | a `hunt` step between |
| arrive | another place; with coordinates, a point over 200 m away **under another name** (the name alone meets it) |
| start | another lodge; no lodge |
| spot | a harvest or a clue instead; another species |

Units: metres, kilograms, minutes, millimetres, percent. A title's inches and pounds are converted by you (3 in = 76.2 mm).

## Order, the same hunt, another animal

- An objective after others (`Then ...`): its pass starts with `{"done": <the one before>}`. The near miss plays its own step first, then the earlier objective's.
- `in the same hunt`: the near miss puts `{"hunt": "<reserve>"}` between the earlier objective's steps and its own.
- `another <animal>`: a new step is a new animal by default; the same animal again is `"animal": "a"` on both steps.
- Two identical objectives: the second's pass is `{"done": 0}` and one more event; its near miss is `{"done": 0}` alone.

## Weapon families

A title that names a weapon accepts its whole family: every finish (skin) of it, and the differently named members the game gives one weapon skill (a revolver and its Magnum when the catalogue's descriptions say they share one skill, both ways; the sawed-off and the semi-automatic shotgun of one maker; the twin of a compound bow). `admin weapon-families` prints the family table of the real catalogue. For such a title:

- write one `also` with another member of the family (a differently named one when the family has one, else another finish);
- take the `fail` weapon from **outside** the family (another family of the same class is the best near miss);
- a name that only lies inside a longer name (`Test Revolver`, `Test Magnum Revolver`) is no family: the title naming the shorter accepts the longer, the title naming the longer stays exact, and the shorter is a near miss of it unless the catalogue relates them (name, definitions' chain or a description that shares the skill).

An ammunition the title names, or `permitted`, is still needed whatever member fired.

## When to write `doubt`

When the wording itself has two readings and the pass or a near miss depends on which: `5 minutes after another` (within 5 minutes, or at least 5?), `the north tower` (which one?), a place no catalogue names (which name does the game send?), a time of day (no condition the server knows), a gender the title implies but does not say. A doubt goes to the maintainer, who decides each one; never guess.

## Traps met in the pilot

- **An event finishes one objective**: the first unfinished one of the mission it meets. A pass for a later objective must not meet an earlier unfinished one (a male animal at 100 % meets an earlier "a male ... at 100 %" before a later "... with a fur other than common"): change something the title leaves open, or do the earlier one first with `done`.
- **Single-hunt missions** forget their progress at every new hunt: a `hunt` step in the middle of the pass undoes the `done` before it.
- **The implicit hunt** is in the mission's first reserve: a near miss in another reserve needs an explicit `hunt` as step 0 of the pass.
- **Two species of one name** (the real catalogue has two): the harness takes the one the mission lists.
- **Places**: the catalogue lists lodges, towers and points of interest; the areas of many titles (`the ... area`, `north of ...`) are only known by the name the game sends there. Without it, write a `doubt`.
- **Named animals**: by default the harness runs with `spawn_named_animals` off, so the server meets a named animal by its species near the title's place (1000 m); add the near misses of the placed animal with `placed`.
- **Arrivals with coordinates** are met by the place's name too: a near miss far away must use another name.
- **Halloween animals** are a species with a Halloween coat: `coat <id>`; their permitted ammunition is the event's, not the species'.
- **Item names that end in a generic word** (`... Arrows`, `... Shells`): the reader names an ammunition by its name without those words, so an invented `Test Arrows` would be read in every title that says "test".

## What the harness cannot express yet

A harvest credited to a member of a multiplayer session (above); the time of day (no condition the server knows); a measured trophy's score together with a beard, spurs or tusks; a measured trophy's score to better than a few tenths (`>=N` and the other bound forms still give each side of a limit).
