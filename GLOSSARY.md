# Hunter Resurgence

A community replacement for the game's online services after the official servers closed: profile backups, a server, a launcher and a website.

## Servers

**Official servers**:
The game publisher's servers, which closed on 2026-11-11.
_Avoid_: live servers, old servers

**Local server**:
A server a player runs on their own PC or local network to play offline.
_Avoid_: offline server

**Community server**:
The shared server run by this project's maintainer; anyone can run the same code.
_Avoid_: restore server

**Operator**:
The person who runs the community server and checks backups and large claims by hand.

## Backup

**Backup tool**:
The Windows app that copied a profile from the official servers to the player's PC; it no longer works since they closed.
_Avoid_: the app, backup app

**Profile**:
The data of one game account: statistics, inventory, trophies, hunt history and more.
_Avoid_: account data

**Server playtime**:
The time a player hunted on this community server, from the hunts it recorded since the opening day; the profile's hunt time is this plus the restored playtime (the original's).
_Avoid_: playtime here

**Hunting statistics**:
Server-wide figures from the hunts, harvests and gm$ rewards: playtime, gm$ per hour and harvests per hour per reserve and per species; never per player for players, and a figure of fewer than 5 players is not shown.

**Backup**:
One copy of a profile at one moment, kept as a backup folder and a backup zip.

**Backup folder**:
The folder a backup writes, named after the account id; a later backup of the same profile adds the date and time.

**Backup zip**:
The zip of a backup folder; the file a player keeps private and later restores.
_Avoid_: zip (alone), archive

**Release zip**:
The download that holds the backup tool and its `settings.json`; not a backup zip.
_Avoid_: the zip

**My account**:
The mode that backs up the player's own profile through the running game.
_Avoid_: own, me

**Public profile**:
The mode that backs up any profile by handle or id, without the game.
_Avoid_: public account

**Group**:
A part of a profile the player ticks to save.
_Avoid_: section, category

**Claim key**:
A per-PC secret saved in every backup zip; a hint that the backup belongs to the player.

**Format**:
The version number of a backup zip's layout, separate from the backup tool's version.

## Restore

**Restore**:
Bringing a profile back on a server from a backup zip, or from the operator's archive by a claim.

**Claim**:
A signed-in player taking the archived profile of their old in-game name onto their account; one per account. It is restored at once below hunterscore 10,000, and waits for the operator at 10,000 or more or when another account claimed the same profile.

**Refused**:
Said of a whole backup zip that a restore does not read.

**Rejected**:
Said of a single entry a restore skips while reading the rest.

## Profile pages

**Profile page**:
The website's or the game's view of one profile on the community server: its header and its areas. Not the backup tool's Public profile mode.
_Avoid_: public profile

**Owner-only**:
Said of a profile area that only its player sees; every other area of a profile page is shown to anyone.

**Server hunt**:
A hunt played on the community server; its statistics are stored when it closes.

**Restored hunt**:
A hunt that came from a backup zip (the docs also say saved hunt); it is shown in the hunt history only and never counted again.

**Base statistics**:
The restored statistics of a profile, which play never changes; the statistics shown are these plus the closed server hunts.

## Launcher

**Launcher**:
This project's Windows app that signs in, picks a server and starts the game.
Translations keep the English word "launcher" (French: "le launcher", never "lanceur").
_Avoid_: new launcher

**Old launcher**:
The game's original launcher.

**Start page**:
The page the community server shows in the old launcher: sign in with Steam, then Play.

**Launch URL**:
The address the launcher gives the game to connect: the game web host and the player's session, access and refresh tokens.

**Game folder**:
The folder where the game is installed.
_Avoid_: install folder

**Game settings**:
The game's graphics and options files, which the launcher can edit.

**Settings copy**:
The copy of a game settings file the launcher makes before its first write.
_Avoid_: backup

**Player kit**:
The part of the launcher that sends the game to the community server: a one-time setup (Settings has Remove) and the forwarder.
_Avoid_: remote play tool, helper

**Forwarder**:
The part of the player kit that answers the game on the player's PC and relays each request unchanged to the community server.

**Website**:
This project's website: profiles, trophies, leaderboards and account pages.

**Hunterscore**:
A player's score over the rank rows of the facts file: per row its ethical kills or pickups, at most 1000, summed. Its display name, on the website, in the launcher and in player docs, is "HunterScore" (one word, capital H and capital S): a brand name like gm$ and em$, never translated. Code and internals docs write `hunterscore`.
_Avoid_: Hunter Score, hunter score (in player-facing text), trophy score

**Hunterscore board**:
The players of the community server in hunterscore order; a player's rank is their place on it.
_Avoid_: ladder, leaderboard (the website's boards of the other kinds)

**Rank row**:
One line of a profile's ranks tab: a species, a weapon or a kind of collectable, with the player's ethical kills or pickups of it. Not the player's rank on the hunterscore board.

## Hunting

**Reserve**:
One of the game's hunting areas, with its own map, species and lodges.

**Hunt**:
One outing on a reserve, from its start to its end; the game calls it an expedition.

**Lodge**:
A house on a reserve's map where a hunt can start and the player reaches their lodge inventory. The docs and the code also say lodge for a trophy lodge.

**Tent**:
A shelter the player places on a reserve; like a lodge, a hunt can start there and the lodge inventory is in reach.

**Spot**:
To study an animal through binoculars or a scope, which records it as spotted.

**Harvest**:
An animal the player killed and then confirmed at its body, which records it; the game also says confirmed kill.

**Score**:
The measure of a harvest by its species' method (from its antlers, horns, skull, weight and the like), shown on its score sheet; some harvests, such as a doe, have none.

**Score sheet**:
The page of a harvest's measurements and the score they add up to.

**Harvest value**:
A harvest's percentage from how quickly the animal died and how little the trophy was damaged; higher is better.

**Cross-species score**:
A harvest's score put on one scale for every species, so that species compare; the harvest pays it in gm$, rounded, when it is above 0. Shortened to CSS.

**Permitted ammunition**:
The ammunition the game allows for a species.

**Disqualified**:
Said of a harvest of which a hit used ammunition not permitted for its species: it has no score and pays nothing.

**Ethical kill**:
A harvest that is not disqualified; only these count in the rank rows.

**Collectable**:
An object on a reserve that the player finds and picks up, such as a shed antler.

**Pickup**:
One collectable picked up; pickups count in the rank rows of their kind.

**Trophy shot**:
The photo a player takes of their harvest at its body.

**Taxidermy**:
Turning a harvest into a trophy, full body or skull, for em$ or gm$ during its offer window, normally 72 hours after the harvest.

**Trophy**:
A taxidermized or skull-mounted harvest, or one restored from the original; the player can mount it in a trophy lodge.

**Trophy lodge**:
A building a player owns where they mount their trophies and weapons for other players to visit; the profile area "Lodges" lists them (the docs and the code often say lodge alone).

## Inventory and wallet

**Loadout**:
What the player carries on a hunt; a saved loadout is a named set the player can pick at launch.

**Lodge inventory**:
The items the player owns that are neither carried nor placed on a reserve; in reach at a lodge or a tent.

**Capacity**:
How much a loadout can hold; a backpack (the Increased Capacity item) adds to it.

**gm$**:
The game's currency earned by playing: harvests, missions and daily missions pay it, and the store sells for it.

**em$**:
The game's other currency, which the original sold for money; the store prices most items in it, and some missions pay it.

**Wallet**:
A player's em$ and gm$ balances and the history of their changes.

**Membership**:
The kind of account a player had on the original: a free guest, or a membership of some months the original sold; shown in a profile's header.

**License**:
The right to hunt a species or on a reserve, which the original sold; on the community server every player holds all of them unless the operator turns that off.

## Multiplayer and events

**Multiplayer game**:
A hunt one player hosts and others join from the launcher's games list. The games connect to each other; the community server creates the game and lists it.

**Punch-through server**:
The small UDP server that introduces a joiner to a host behind a home router, so no port needs forwarding.

**Seasonal event**:
A themed event the operator switches on for a window of time on some reserves, with its own creatures and missions. Each player can turn it off ("Play seasonal events").

**Event creature**:
A creature that lives only on the reserves of a seasonal event while it runs, such as the werewolf or the wendigo.

**Season**:
A leaderboard period of the community server: 0 is all time, 1 the harvests restored from the original (up to 2026-11-11), 2 and later the server's own, each started by the operator. A harvest belongs to the season its kill time falls in; an ended season has a Hall of Fame.
_Avoid_: seasonal event (a themed event of creatures and missions, not a leaderboard period)

## Missions

**Mission**:
A task a player activates and finishes in hunts for a reward; it has one or more objectives and is finished when all of them are.

**Mission pack**:
A group of missions about one species, weapon or theme; some missions need earlier ones of their pack first.
_Avoid_: mission group, chain

**Daily mission**:
The mission on a player's daily calendar, picked by the date and the difficulty of the player's streak day (a frozen calendar keeps the one it showed); finishing it continues that streak.
_Avoid_: daily challenge

**Giver**:
The character who gives a daily mission, shown with their portrait.

**Streak**:
The days in a row a player finished their daily mission; a missed day breaks it, and the next activation starts again at day 1.

**Difficulty**:
Easy, Medium or Hard: the level of a day's daily mission, set by the day of the streak; a harder one pays more.

**Objective**:
One line of a mission, such as "Harvest a Whitetail Deer buck with at least 8 typical points"; met by what the player does after the mission is active.

**Not understood**:
Said of an objective the community server can't judge yet: it can't be newly met, and an earlier finish of it stays.
_Avoid_: unsupported

**Overridden rule**:
The rule of one objective that the operator states in `mission_rules.json`, in the words `coverage --rules` prints, instead of the server's reading of its title; it is judged like any other rule.

**Named animal**:
An animal a mission objective names ("Old Mossback" at a place), which the game does not mark. With `spawn_named_animals` the community server places it with the hunt's start and only that animal meets the objective; without the setting, any animal of its species near the place does.

**Clue**:
What an animal leaves that the player identifies: footprints, droppings, a call, a blood trail, bedding, rooting or water bubbles. No title names the last two, so only an objective that follows one animal's trail counts them.

**Track**:
Footprints, as in "ID tracks from a Grey Wolf" or "ID 3 tracks from a Moose", and wherever a title names tracks beside another kind of clue ("tracks or droppings", "tracks, droppings or calls"). Following one animal's trail, two or more tracks to locate or find ("Locate 3 Elk tracks") or two or more from the same animal ("3 tracks from the same animal"), counts clues of any kind from that animal in one hunt.
_Avoid_: clue (when footprints are meant)

**Footprints**:
The clue of an animal's feet on the ground, walking or fleeing; titles also say footprint or footsteps.

**Typical points**:
The antler points of an animal's typical antler shape, both antlers added: 5 on each side is 10. Not the abnormal points.
_Avoid_: points (alone), tines

**Abnormal points**:
The antler points outside the typical shape, counted apart on the score sheet.

**Reopen**:
To set a finished objective back to not met because no harvest or clue recorded on the community server since the mission was activated meets it (for a single-hunt mission, only the one hunt that meets most of its finished objectives counts); the player keeps the reward already paid, and the mission pays again when finished again.
_Avoid_: revert, reset
