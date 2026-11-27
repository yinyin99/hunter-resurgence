# Pictures

The website and the launcher draw every picture of the original screens in a *slot*: `<AssetSlot Name=… Width=… Height=…>` (`src/Ui/Components/AssetSlot.razor`), the map of `MapPlaceholder` (`src/Ui/Components/MapPlaceholder.razor`) and the page background `page-bg` (`src/Ui/Layouts/ShellLayout.razor`, website and launcher). The repository holds no pictures: a slot is a labelled box unless the operator supplies its file; without `page-bg` the page is plain black.

## How it works

- The website reads the folder named by `IMAGES_DIR`. On the VPS that is `SERVER_DIR/images`, mounted read-only at `/images` in `web` (`deploy/compose.yml`, [deploy.md](deploy.md#the-community-server)).
- One file per slot, named `<key>.<png|jpg|jpeg|gif|webp|webm>`. The key is the slot's name in lower case, with every run of characters outside `a-z` and `0-9` made one `-` and none at either end (`AssetImages.Key`): the slot `species-Alpine Ibex` is the file `species-alpine-ibex.png`. A `.webm` plays in the slot as a muted looping video, as the original landing page's background did. SVG is not served: opened directly from the site, an SVG can run script.
- The files are served at `/images/<file>`, cached `immutable`; the page asks for `/images/<file>?v=<time of the file>`, so a changed file gets a new URL.
- `/images.json` lists the pictures for the launcher: `{"<file>": "<version>"}`, where the version is the file's `?v=`. It is read with the folder, once at start; `{}` without `IMAGES_DIR`.
- The website reads the folder once at start: restart `web` after changing files (`docker compose restart web`).
- A slot without a file shows its labelled box, and so does every slot without `IMAGES_DIR`: the public repository, `dotnet run`, CI. The CI smoke test checks both cases, `/images.json` and `/images-missing.json`.
- The map is drawn from `map-<NN>` (the reserve, two digits) on the launch and join maps, and `map-bg` on the score sheet's map; it zooms and pans with the markers. Each marker is the slot `marker-<kind>` (`kind` is `lodge`, `tent`, `campsite`, `tower`, `interest`, `bait`, `feeder`, `treestand`, `tripod`, `deployabletower`, `groundblind`, `climbing` or `caller`), 24×24, and the selected or hovered one `marker-<kind>-active` when that file exists; a kind without a file is drawn as its icon and listed in `/images-missing.json` like any slot. The server gives each start's place as a percent of the `map-<NN>` picture ([server.md](server.md#the-launch-map)), so a picture and the server's square for that reserve must be the same cut.
- The launcher renders `src/Ui` itself, so it asks the website. At each start it reads `/images.json` from `server_url` of its `settings.json` ([launcher.md](launcher.md#settingsjson)) and waits 5 s at most; its slots then load `/images/<file>?v=…` from there, and WebView2 caches them until the version changes. Without `server_url`, or on no answer or an error, the launcher shows the labelled boxes.

## Missing pictures

`GET /images-missing.json` lists every slot the website and the launcher can draw that has no file in `IMAGES_DIR`, without visiting a page, with counts. The file to add is `<key>.<png|jpg|jpeg|gif|webp|webm>`.

```
curl -s https://<site>/images-missing.json
```

```json
{
  "total": 478, "present": 1,
  "areas": { "decoration": { "total": 25, "present": 1 }, "reserves and maps": { "total": 45, "present": 0 } },
  "missing": { "decoration": [ { "key": "logo-rating-2", "size": "62x82", "page": "Footer" } ] },
  "seenWithoutFile": { "ach-badge-early-bird": "/profile" }
}
```

- `total` is the number of slots the manifest expects (`src/Ui/AssetSlots.cs`), `present` how many of them have a file, `areas` the same per area: `avatars`, `bank`, `competitions`, `decoration`, `dogs`, `help`, `missions`, `news`, `profile`, `reserves and maps`, `rewards`, `score sheet`, `species`, `store`. `missing` has the rest by area, sorted by key, with the slot's main `size` (`WxH`, empty when the page sets it) and the `page` or component that draws it; an area with nothing missing is left out.
- The manifest holds the fixed names of the code, and expands each name built from data with the website's own services: store items and bundles (`item-<id>`, `bundle-<id>`; with a community server `store-item-<id>`, `store-bundle-<id>`: the catalogue the store shows, so the server must answer, and the store is for signed-in players: fetch `/images-missing.json` signed in to get these slots), species (`species-<name>`, the leaderboards' species), reserves (`reserve-<NN>`, `map-<NN>`, from the reserve list: 01 to 12), the marker kinds (`marker-<kind>` and `marker-<kind>-active`, which is optional: without it the selected marker keeps its picture), avatars (`avatar-<id>`, the account's choices), tutorial videos (`video-<id>`), competitions (`competition-hero-<id>`) and the feed's own posts, the bank's packs and memberships, the day missions' pictures and the fixed sets (`rank-<score, animals, weapons, collectables>`, `ach-<animals, weapons, exploration, day-mission>`, `paw-badge-<easy, medium, hard>`, `competition-<board, weight>`, `logo-rating-<1-4>`). The launcher's pages are in it (`reserve-<NN>`, `map-<NN>`, the markers, the avatars). The pictures of the mock alone (the bank, the day missions' people, `profile-banner`, the competitions' pictures, `recruit-hero`, the `bait` and `feeder` markers) are left out on a site with a community server (`HostMode.Live`): the pages that draw them (`/competitions`, `/community-league`, `/invite-friends`) go to the feed there, and the server sends no bait or feeder.
- The reply is public and cached: `total`, `present`, `areas` and `missing` are computed from the manifest and the folder once an hour and kept in memory, so they can be up to an hour old (the folder is read once at start and the catalogue changes rarely); a restart of `web` refreshes them at once, and it is also what makes a new file leave the list (`docker compose restart web`). A reply computed while the store was empty (the server not answering) is kept for 10 seconds only; one computed while the store answered and another list did not has no slots of that list until the hour is over or `web` restarts. `seenWithoutFile` is built for each request from the runtime list. The reply carries `Cache-Control: public, max-age=300`, so Cloudflare keeps it for 5 minutes. The catalogue is the store's own cache (5 minutes).
- **Listed when drawn**: names that come from a player's profile, which the website cannot list without that player (`ach-icon-<icon>`, `ach-badge-<title>`, `skill-<name>`, `statistics-<define>`, `trophy-<image>`, `weapon-<name>`, `ammo-<name>`, `collectable-<name>`, `photo-<id>`, `profile-bg-<name>`, `dog-skill-<skill>-<level>`, `dog-trophy-ribbon-<n>`, `antler-diagram-<species>`, `antler-guide-<species>`, `rank-<id>`, and the live site's `avatar-<id>` of the game's list, which the server gives only to a signed-in player). The live site's `avatar-<id>` files also serve the game, which reads them from the same folder and falls back to `avatar-1000000` ([server.md](server.md#avatars)); the live list cannot name a missing one, so check the avatar ids by hand. They are in the manifest as patterns without a source, so a page that draws one without a file puts it in `seenWithoutFile`, as `{"<key>": "<page>"}` sorted by key. So does any slot the manifest does not know at all (the test below should keep that empty). A key keeps the first page it was seen on; on a signed-in page only the first segment of the path is kept (`/messages`, `/bank`), because the list is public. It is in memory, cleared by a restart of `web`, and holds 10,000 keys at most; the open pages, tabs and dialogs fill it.
- `reserve-<NN>-<event>` (the event badge of a reserve) is asked for only while the community server's event runs and its file exists ([launcher.md](launcher.md#seasonal-events)), so it is in neither list.

### A new slot or pattern

`AssetSlotsTests` reads every `<AssetSlot Name=…>` of `src/Ui/**/*.razor`, the map's own names (`map-`, `marker-`, `reserve-`, `page-bg`) and fails for a literal name, or the literal start of a name built from data (`"ach-" + x`, `$"video-{id}"`), that `AssetSlots` does not have. A name held in a variable (`Name="@slot"`) is not read: its pattern is added by hand. In `src/Ui/AssetSlots.cs`:

- a fixed name: one `L(key, area, size, page)` line in `Literals`;
- a name built from data: one `Pattern(prefix, area, size, page, keys)` in `Patterns`, `prefix` the literal start as the `.razor` file writes it, `keys` the function that returns the names from the website's services (add the service to `AssetSources` and to the `/images-missing.json` handler in `src/Web/Program.cs`). Without `keys` the pattern is listed when drawn.

## Inventory

112 `<AssetSlot>` in 53 `.razor` files, 3 `<MapPlaceholder>` (`LaunchMap`, `JoinMapModal`, `SheetTab`) and `page-bg` in `ShellLayout`. Recount:

```
grep -rho '<AssetSlot' src/Ui --include=*.razor | wc -l
grep -rln '<AssetSlot' src/Ui --include=*.razor | wc -l
grep -rn '<MapPlaceholder' src/Ui --include=*.razor
```

With the values the mock services give, the slots have 736 distinct keys, and the 12 `marker-<kind>` of the launch map (the mock without tents) on top; a pattern's names come from the data of the page (`item-<id>`: one picture per store item, scaled to each slot that shows it). "Have it" says whether the operator's files hold the original picture of that place: `yes`, `no` with the reason, or the count for a pattern. Reasons: *player content* (uploaded by players: never used), *no original picture* (the original showed none there), *not in the operator's files*. The column is kept by hand; `/images-missing.json` gives the current state of the folder.

| Name or pattern | Size (W×H) | Where used | Have it |
|---|---|---|---|
| `logo-large` | 654×164, 300×56, 300×50, 240×60 | BrowserNotice, SignupCard, PublicBar, Eula | yes |
| `logo-bar` | 173×44 | StatusBar | yes |
| `logo-hero` | 485×120 | Feed | yes |
| `logo-landing` | 1000×250 | AutoLogin | yes |
| `logo-splash` | 483×65 | Splash | yes |
| `logo-rating-<1-4>` | 62×82 | Footer | 4 of 4 |
| `bg-hero` | 980×550 | Feed | yes: `bg-hero.webm`, the original landing page's background video (`landing_page_video.webm`, played muted and looping as there) |
| `browser-bg` | 1280×760 | BrowserNotice | yes |
| `page-bg` | behind the page, top centre, once, black below | ShellLayout (website and launcher) | yes: the original's `main_page_background2.jpg` (`body:not(.client)` in its stylesheet) |
| `frame-leather` | 1280×760 | SignupLayout | yes |
| `signup-bg-<1-5>` | 1200×674 | SignupCard (signup pages) | 5 of 5 |
| `gift-frame` | 561×288 | Signup Page2 to Page5 | yes |
| `gift-shotgun`, `gift-licenses`, `gift-membership` | 467×99, 476×193, 441×149 | Signup Page2, Page3, Page4 | yes: the original's signup pages 2, 3 and 4 showed `gift_shotgun`, `gift_licenses` and `gift_gun_membership`; their sizes are the slots' |
| `paper-notice` | set by the page | Paper | yes |
| `icon-store` | 46×46 | MenuBar | yes |
| `avatar-default` | 24 to 160 square | every place that draws a player: MapPlaceholder, RankRow, JoinMapModal, Dashboard, Leaderboards, Messages, Servers, StatusBar, FriendsTab, FriendsDropdown, HunterProfile, Settings, Welcome (the live site: a player with no avatar the pages can draw, an address that is no avatar of the game's list) | yes |
| `avatar-<NN>`, `avatar-locked` | 34 to 171 | Settings, StatusBar, Welcome, HunterProfile, Signup Page1 (the mock, without a community server: the live site draws `avatar-<id>`) | 55 of 55 |
| `avatar-<id>` | 24 to 171 square | everywhere a player is drawn, with the live site (the game's avatars: the Settings choices, the sign-up carousel, and a player's avatar by the id in its picture's file name, `…/256/<id>.png`, [website.md](website.md#avatars)); the id is one of the server's avatar list (`Avatar/getAvatars`) | 55 files, all of the game's list, which the operator uploads ([#453](https://github.com/yinyin99/hunter-resurgence/issues/453)): `avatar-1000000` to `avatar-1000017`, `avatar-1001000` to `avatar-1001017`, `avatar-1002000` to `avatar-1002017` and `avatar-1000024` (the one the store sells); an id without its file shows the labelled box |
| `profile-banner` | 940×200 | HunterProfile (the mock) | no: player content (the player's latest kill) |
| `profile-bg-<name>` | 940×200 | HunterProfile (the live site: the header picture the player chose, the restored profile's `background` without its extension, `profile-bg-exploration_3` for `exploration_3.png`; no slot without one) | 19 files: `exploration_1` to `6`, `species_1` to `6`, `weapons_1` to `6` and a holiday banner |
| `polaroid-back`, `polaroid-front` | 239×288 | HunterProfile (the avatar is drawn between them, turned -5°, as the original's `#profile-image-container`; a missing `polaroid-front` is not drawn, so its box does not cover the avatar) | yes |
| `photo-<id>` | 210×118, 900×506 | GalleryTab, VideoModal | 0 of 30: player content |
| `video-<id>` | 900×506, 185×138 | VideoModal, Help (tutorial rows) | 16 of 19: `v1`, `v2`–`v8` and `v12`–`v19` are matched to the original help page's videos by section and position (`v1` and `v12`–`v19` also by title; the mock names another author for `v12`–`v19`; for `v2`–`v8` the original lists the author only, and the section sizes and the repeated author match the mock's) and show the video site's `0.jpg` thumbnail, as the original page did. `v9`–`v11` (the deployables section's last three) stay empty: those videos are deleted, so their thumbnails are gone |
| `video` | 900×506 | VideoModal (Feed) | no: the original played the trailer in a video-site embed; no picture |
| `news-default` | 805×380, 175×95 | FeedCarousel (`NewsPicture`) | no: the slide picture of a news post that has none of its own |
| `competition-hero-170039`, `competition-hero-170031`, `competition-hero-170026` | 805×380, 175×95 | FeedCarousel (`NewsPicture`) | yes: the pictures of the project's first three posts, which the server's `news` rows name as `slot:<name>` (`NewsSeed.cs`), cropped to fill the slide. A post may name any slot (`admin news edit <id> --picture-slot <name>`), or have a file of its own (`--picture`, [server.md](server.md#news)) |
| `help-banner`, `help-hero` | 940×250 | Help | yes: the visible crop of the original's tutorial cover |
| `help-dm-<interface, levels, cycle, achievements, rewards, resume-buy, resume-use>` | 484 to 940 wide | DayMissionHelp | 7 of 7 |
| `reward-<day>` | 60×60 | DayMissionHelp | 11 of 11 |
| `paw-badge-<easy, medium, hard>` | 100×111 | DailyMissions | 2 of 3: the original's `mission_icon_easy_02` and `mission_icon_medium_02` for 1 and 2 stars; no hard icon was saved |
| `mission-giver-<alias>` | 150×150 | DailyMissions | 8 of 8: the giver of the day's mission, by the alias the server gives with its `_` written `-` (`mission-giver-vera-herzschlag`, `-buck-williams`, `-angelica-rodriguez`, `-harley-lovegrove`, `-clinton-douglas`, `-igsivalitaq`, `-laura-francese`, `-taxidermist-max`): the original's portrait of the character, shown at 150×150. A mission without a giver draws none; a giver the manifest does not list is reported when the page draws it |
| `mission-species-<species>-<m, f>` | 250×360 | DailyMissions | 102 of 102 (`mission-species-rusa-deer-m`: the species define in lower case, its `_` written `-`): the original's `missions/species/males/daily_missions_<species>.jpg` and `females/…` of the 51 species with a card (`<species>` the species define in lower case). The page draws the one of the mission's species by the mission's gender (the server gives both: `species`, `gender` 0 male or 1 female), as the original did (female when the mission's gender is 1, else male); a mission without a species draws none. The mock's only mission is the male Rusa Deer |
| `competition-<icon>` | 45×45 | CompetitionTable (the mock; not on the live site) | 2 of 2 |
| `competition-hero-<id>` | 980×340 | CompetitionView (the mock; not on the live site) | 160 of 171 (one picture per species id, as the game's species table). 11 competitions for Axis Deer, Fallow Deer and Wood Grouse (species 54, 55, 57) stay empty: the original had no picture for those species |
| `item-<id>` | 150×110, 270×197, 30×30, 260×70, 46×34 | Store, ItemDetail, BundleDetail (the mock, without `SERVER_URL`; 260×70 for the items an item page lists as fitting it, 46×34 for the head of its ammunition column) | 47 of 47, stand-ins: the mock items are fictional ("Blind C"), so each has a real picture of the same kind (hat, scope, tent, …), not of one particular item. Their ids are the mock's, and 40 of them are ids of the original's catalogue, so the live site never looks for them |
| `bundle-<id>` | 150×110, 190×207 | Store, BundleDetail (the mock) | 2 of 2 |
| `store-item-<id>` | 150×110, 270×197, 230×50, 256×187, 30×30, 260×70, 46×34 | Store, ItemDetail, BundleDetail, the weapons of SkillsTab and WeaponTable, the dogs of DogsTab (the live site: the server's items, with the original's ids; 260×70 for the items an item page lists as fitting it, 46×34 for the head of its ammunition column; 230×50 in the skills, 150×30 and 36×36 in the statistics; the dogs use it on the mock too, with the dog item's own id: 256 wide as the original's, drawn whole) | 1,003 of 1,003: the item's `image` in the original's item table, `items/256/<image>.png` as the original store showed it |
| `store-bundle-<id>` | 150×110, 190×207 | Store, BundleDetail (the live site) | 129 of 129: as the original store, the bundle's own `image`, else its first item's |
| `em-pack` | 100×80 | BankModal (redeemed code) | yes |
| `em-pack-<ems>` | 100×80 | BankModal | 5 of 5 |
| `membership-<starter, special, basic>` | 100×70 | BankModal | 1 of 3: `starter` is the Wayfarer card (the original's one starter bundle, 3 months). `special`: the original had three bundle cards (Deer Hunter, Duck Hunter, Stealth Hunter) that the mock's two anonymous bundles cannot be matched to; `basic`: one card per length (3, 6, 12 months) under one key |
| `recruit-hero` | 940×310 | InviteFriends (the mock; not on the live site) | yes |
| `reserve-<NN>` | 70×70 | LaunchMap | 12 of 12: the launch map's reserve badges, the normal art (coloured row of the original's permit sprite, in reserve id order) |
| `reserve-<NN>-<event>` | 70×70 | LaunchMap | 5 of 5 for `halloween_2026` (`reserve-01-halloween-2026`, 02, 03, 09, 10: the game's reserves 0, 1, 2, 8 and 9 of the event): the event's badge, drawn instead of `reserve-<NN>` only while the community server's event runs for that reserve and the player plays seasonal events ([launcher.md](launcher.md#seasonal-events)). The event name goes through `AssetImages.Key` like any slot name, so `halloween_2026` is `halloween-2026`; a reserve or event without a file keeps the normal badge, and the slot is never asked for outside the event, so `/images-missing.json` does not list it |
| `map-<NN>` (MapPlaceholder) | the map | LaunchMap, JoinMapModal | 12 of 12: the original's one world map (zoom 5 tiles) cut to a square around each reserve's border (6% margin, a square near the world's edge slid inward), scaled to 1500 px at most; the same square is the facts file's `reserves[].map`, the server's marker positions are percent of it. The boxes of the join map (704×608) stretch it |
| `marker-<kind>` (MapPlaceholder) | 24×24 | LaunchMap, JoinMapModal (the reserve maps; the score sheet's map keeps icons) | 13 of 13: the original's map marker sprite, one 32×32 picture a kind (the feeder is the sprite's broadcast feeder, the bait the barrel, `deployabletower` the sprite's deployable hunting tower, `climbing` its climbing gear, `caller` its electronic caller). Lodge, campsite, tower and interest are the server's starts; tent, treestand, tripod, deployabletower, groundblind, climbing and caller are the player's own placed items, each kind its own picture (`GET /account/placed`, [server.md](server.md#the-launch-map)); bait and feeder are the mock's |
| `marker-<kind>-active` (MapPlaceholder) | 24×24 | LaunchMap (the selected or hovered marker) | 13 of 13: the sprite's second row, the red one. Optional: without it the selected marker keeps its picture; `/images-missing.json` lists it like any slot |
| `map-bg` (MapPlaceholder) | the map | SheetTab | no: the original score map was a map centred on the kill's position; no single picture fits |
| `species-<name>` | 135×135, 118×118 | Leaderboards | 59 of 59 in the mock; all 54 of the live site's. The six the Hall of Fame tiles and the species boards draw for Blacktail Deer, Mule Deer, Whitetail Deer, Roe-bit, Wendigo and Werewolf (`species-blacktail-deer`, `species-mule-deer`, `species-whitetail-deer`, `species-roe-bit`, `species-wendigo`, `species-werewolf`) are the original's `statistics/<species define in lower case>.png`, the picture its Hall of Fame tile drew; a non-typical deer board ("Mule Deer (Non-Typical)") draws the typical's picture |
| `rank-<animals, weapons, collectables>` | 114×114, 40×40 | RanksTab (the mock; the live site draws `rank-<id>` in the rows) | 3 of 3 (the overview rows show them at 40×40) |
| `rank-<id>` | 114×114 | RanksTab (the live site: a row of animals, weapons or collectables, `<id>` the rank's game id, the same ids as the game's rank table) | 115 files in the operator's folder |
| `rank-score` | 40×40 | RanksTab | yes: the hunter score icon of the ranks overview |
| `ribbon-<turkey, fox, bear, elk, moose>` | 250×400 | HunterProfile (under the avatar polaroid on every tab, as the original's `#profile-ribbon`: the server's `ribbon` of the hunter without its extension, by the share of the achievements earned: `ribbon-moose` for `moose.png`; no slot without one) | the original's five collector ribbons, a tall tag a species: moose 5 to 24 % of the achievement points, elk 25 to 49, bear 50 to 74, fox 75 to 89, turkey 90 to 100 (below 5 % none) |
| `ach-<animals, weapons, exploration, day-mission>` | 100×100, 40×40 | AchievementsTab | 3 of 4 (the overview rows show the first three at 40×40): the original's `cat0` (animals) is the stag of its icon sprite, `cat1` the bullets, `cat2` the compass; `ach-day-mission`: the sprite has no day-mission icon (its paw cell is used by none of the original's templates) |
| `ach-icon-<icon>` | 150×150 | AchievementsTab (the live site: a milestone row) | the achievement's own `icon_url` (174 files): the original showed it on its category pages |
| `ach-badge-<title>` | 90×90 | AchievementsTab | 13 of 14: animal badges by the game's achievement table, challenge badges by the challenge's description (`Quick Draw` is the original's Assassin); `1 Rifle A (Standard)` is a stand-in for a weapon badge; `Early Bird` is not in the original |
| `skill-<name>` | 80×90 | SkillsTab | 78 of 81: weapon skills by the weapon category in the name; `i1` (semi-automatic rifles), `k1` and `l1` (air rifles) stay empty, the original's only icon of that kind is already used by another skill or is of another category |
| `statistics-<define>` | 80×90 | SkillsTab (the live site: the species' skill pictures) | `statistics/<define in lower case>.png` as the original showed it, 54 of the 55 species shown (`sasquatch` has none); the live weapon skills and the statistics' weapons and ammo use `store-item-<id>` |
| `trophy-<name>` | 147×147 | TrophiesTab (the mock) | 9 of 26: the 9 `Competition Trophy` slots are stand-ins (a real competition trophy each); the 17 plain `Trophy <n>` slots stay empty, nothing says which trophy they are |
| `trophy-<image>` | 147×147 | TrophiesTab (the live site: a competition trophy, `<image>` the file name of its picture without the extension, `trophy-elk_gold` for `…/elk_gold.png`; a trophy with no picture is drawn as `trophy-<name>`) | 1320 files in the operator's folder; `halloween_2026_campaign_hematite`, `halloween_2026_comp_bronze` and `halloween_2026_comp_silver` stay empty: their pictures are not in the operator's files |
| `dog-skill-<skill>-<level>` | 40×40 | DogsTab (a breed skill of a dog: `<skill>` the skill's define without `DOG_SKILL_`, in lower case with `-` for `_`, `dog-skill-loyalty-stamina-1`; `<level>` its value, 1 and one more for each unlock reached) | the original's `dogskills/<define>_<value>.png`: 85 files in the operator's folder |
| `dog-trophy-ribbon-<n>` | 64×64 | DogsTab (one slot for the dog's `<n>` ribbons, as many as the unlocks reached; no slot without one) | no: the original's ribbon picture is not in the operator's files |
| `weapon-<name>`, `ammo-<name>` | 150×30, 36×36 | WeaponTable | 5 of 5, 5 of 5: the mock weapons are fictional (stand-in pictures of the same kind); each ammo is the one the game's item table lists as fitting that weapon's picture (.243, .30-06, .340 Weatherby, 7.62x54R, 12 GA birdshot) |
| `collectable-<name>` | 24×24 | CollectableTable | 1 of 1 |
| `sheet-logo` | 460×64 | SheetTab | yes |
| `sheet-medal` | 84×84 | SheetTab | no: none of the 1,241 saved score pages shows a medal |
| `sheet-sponsor` | 116×82 | SheetTab | yes |
| `antler-diagram-<species>`, `antler-guide-<species>` | 380×220, 560×460 | SheetTab (the guide of a whitetail, mule or blacktail deer is `antler-guide-<name>-typical` or `-non-typical`, the wendigo's `antler-guide-wendigo-male` or `-female`) | 1 of 1, 1 of 1 |

A player's own picture, imported from their backup, is no slot: the page draws the link the server gives (`https://<site>/photos/<account id>/avatar.jpg`) as an image, so it is never in this folder ([server.md](server.md#avatars)).
