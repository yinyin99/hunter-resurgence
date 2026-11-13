# Pictures

The website and the launcher draw every picture of the original screens in a *slot*: `<AssetSlot Name=… Width=… Height=…>` (`src/Ui/Components/AssetSlot.razor`), the map of `MapPlaceholder` (`src/Ui/Components/MapPlaceholder.razor`) and the page background `page-bg` (`src/Ui/Layouts/ShellLayout.razor`, website and launcher). The repository holds no pictures: a slot is a labelled box unless the operator supplies its file; without `page-bg` the page is plain black.

## How it works

- The website reads the folder named by `IMAGES_DIR`. On the VPS that is `SERVER_DIR/images`, mounted read-only at `/images` in `web` (`deploy/compose.yml`, [deploy.md](deploy.md#the-community-server)).
- One file per slot, named `<key>.<png|jpg|jpeg|gif|webp|webm>`. The key is the slot's name in lower case, with every run of characters outside `a-z` and `0-9` made one `-` and none at either end (`AssetImages.Key`): the slot `species-Alpine Ibex` is the file `species-alpine-ibex.png`. A `.webm` plays in the slot as a muted looping video, as the original landing page's background did. SVG is not served: opened directly from the site, an SVG can run script.
- The files are served at `/images/<file>`, cached `immutable`; the page asks for `/images/<file>?v=<time of the file>`, so a changed file gets a new URL.
- `/images.json` lists the pictures for the launcher: `{"<file>": "<version>"}`, where the version is the file's `?v=`. It is read with the folder, once at start; `{}` without `IMAGES_DIR`.
- The website reads the folder once at start: restart `web` after changing files (`docker compose restart web`).
- A slot without a file shows its labelled box, and so does every slot without `IMAGES_DIR`: the public repository, `dotnet run`, CI. The CI smoke test checks both cases, `/images.json` and `/images-missing.json`.
- The map is drawn from `map-<NN>` (the reserve, two digits) on the launch and join maps, and `map-bg` on the score sheet's map; it zooms and pans with the markers. Each marker is the slot `marker-<kind>` (`kind` is `lodge`, `tent`, `campsite`, `tower`, `interest`, `bait`, `feeder`, `treestand` or `groundblind`), 24×24, and the selected or hovered one `marker-<kind>-active` when that file exists; a kind without a file is drawn as its icon and listed in `/images-missing.json` like any slot. The server gives each start's place as a percent of the `map-<NN>` picture ([server.md](server.md#the-launch-map)), so a picture and the server's square for that reserve must be the same cut.
- The launcher renders `src/Ui` itself, so it asks the website. At each start it reads `/images.json` from `server_url` of its `settings.json` ([launcher.md](launcher.md#settingsjson)) and waits 5 s at most; its slots then load `/images/<file>?v=…` from there, and WebView2 caches them until the version changes. Without `server_url`, or on no answer or an error, the launcher shows the labelled boxes.

## Missing pictures

`GET /images-missing.json` lists every slot the website drew as its labelled box since it started, as `{"<key>": "<page>"}` sorted by key. The file to add is `<key>.<png|jpg|jpeg|gif|webp|webm>` in `IMAGES_DIR`.

```
curl -s https://<site>/images-missing.json
```

- A slot is listed only once a page has drawn it: open the pages, and their tabs and dialogs, whose pictures are to be checked. A dialog's slot gives the page under it. Each key keeps the first page it was seen on.
- A key built from page data (`item-<id>` …) is listed as the concrete key (`item-12`).
- On a signed-in page only the first segment of the path is kept (`/messages`, `/bank`), because the list is public.
- The list is in memory: restarting `web`, which adding files needs too, clears it. It holds 10,000 keys at most.
- The launcher draws the same slots from the same folder, but its own pages (`/dashboard`, `/launch-map`, `/servers`, `/tutorials`: `reserve-<NN>`, `map-<NN>` …) are sent to `/feed` by the website, so their slots are not listed; the Inventory covers them. A Development run (`dotnet run --project src/Web`) on a copy of the folder lists them too after opening those pages with `?mode=launcher`.

## Inventory

113 `<AssetSlot>` in 53 `.razor` files, 3 `<MapPlaceholder>` (`LaunchMap`, `JoinMapModal`, `SheetTab`) and `page-bg` in `ShellLayout`. Recount:

```
grep -rho '<AssetSlot' src/Ui --include=*.razor | wc -l
grep -rln '<AssetSlot' src/Ui --include=*.razor | wc -l
grep -rn '<MapPlaceholder' src/Ui --include=*.razor
```

With the values the mock services give, the slots have 661 distinct keys, and the 8 `marker-<kind>` of the launch map (the mock without tents) on top; a pattern's names come from the data of the page (`item-<id>`: one picture per store item, scaled to each slot that shows it). "Have it" says whether the operator's files hold the original picture of that place: `yes`, `no` with the reason, or the count for a pattern. Reasons: *player content* (uploaded by players: never used), *no original picture* (the original showed none there), *not in the operator's files*. The column is kept by hand; `/images-missing.json` gives the current state of the folder.

| Name or pattern | Size (W×H) | Where used | Have it |
|---|---|---|---|
| `logo-large` | 654×164, 300×56, 300×50, 240×60 | BrowserNotice, SignupCard, PublicBar, Eula | yes |
| `logo-bar` | 173×44 | StatusBar | yes |
| `logo-hero` | 485×120 | Feed | yes |
| `logo-landing` | 1000×250 | AutoLogin | yes |
| `logo-splash` | 483×65 | Splash | yes |
| `logo-dev` | 220×72 | Footer | yes |
| `logo-rating-<1-4>` | 62×82 | Footer | 4 of 4 |
| `bg-hero` | 980×550 | Feed | yes: `bg-hero.webm`, the original landing page's background video (`landing_page_video.webm`, played muted and looping as there) |
| `browser-bg` | 1280×760 | BrowserNotice | yes |
| `page-bg` | behind the page, top centre, once, black below | ShellLayout (website and launcher) | yes: the original's `main_page_background2.jpg` (`body:not(.client)` in its stylesheet) |
| `frame-leather` | 1280×760 | SignupLayout | yes |
| `signup-bg-<1-5>` | 1200×674 | SignupCard (signup pages) | 5 of 5 |
| `gift-frame` | 561×288 | Signup Page2 to Page5 | yes |
| `gift-shotgun`, `gift-licenses`, `gift-membership` | 467×99, 476×193, 441×149 | Signup Page2, Page3, Page4 | yes: the original's signup pages 2, 3 and 4 showed `gift_shotgun`, `gift_licenses` and `gift_gun_membership`; their sizes are the slots' |
| `paper-notice` | set by the page | Paper | yes |
| `coin-ems`, `icon-store` | 46×46 | MenuBar | yes |
| `avatar-default` | 24 to 160 square | every place that draws a player: MapPlaceholder, RankRow, JoinMapModal, Dashboard, Leaderboards, Messages, Servers, StatusBar, FriendsTab, FriendsDropdown, HunterProfile, Settings, Welcome (the live site: a player with no avatar the pages can draw, an address that is no avatar of the game's list) | yes |
| `avatar-<NN>`, `avatar-locked` | 34 to 171 | Settings, StatusBar, Welcome, HunterProfile, Signup Page1 (the mock, without a community server: the live site draws `avatar-<id>`) | 55 of 55 |
| `avatar-<id>` | 24 to 171 square | everywhere a player is drawn, with the live site (the game's avatars: the Settings choices, the sign-up carousel, and a player's avatar by the id in its picture's file name, `…/256/<id>.png`, [website.md](website.md#avatars)); the id is one of the server's avatar list (`Avatar/getAvatars`) | 54 files, all of the game's list, which the operator uploads ([#453](https://github.com/yinyin99/hunter-resurgence/issues/453)): `avatar-1000000` to `avatar-1000017`, `avatar-1001000` to `avatar-1001017` and `avatar-1002000` to `avatar-1002017`; an id without its file shows the labelled box |
| `profile-banner` | 940×200 | HunterProfile (the mock) | no: player content (the player's latest kill) |
| `profile-bg-<name>` | 940×200 | HunterProfile (the live site: the header picture the player chose, the restored profile's `background` without its extension, `profile-bg-exploration_3` for `exploration_3.png`; no slot without one) | 19 files: `exploration_1` to `6`, `species_1` to `6`, `weapons_1` to `6` and a holiday banner |
| `polaroid-back`, `polaroid-front` | 239×288 | HunterProfile (the avatar is drawn between them, turned -5°, as the original's `#profile-image-container`; a missing `polaroid-front` is not drawn, so its box does not cover the avatar) | yes |
| `animals-<n>`, `weapons-<n>`, `exploration-<n>`, `events-<n>` (profile headers) | 940×200, 240×50 | HunterProfile, Settings | 20 of 20 |
| `photo-<id>` | 210×118, 900×506 | GalleryTab, VideoModal | 0 of 30: player content |
| `video-<id>` | 900×506, 185×138 | VideoModal, Help (tutorial rows) | 9 of 19: `v1` and `v12`–`v19` are matched to the original help page's videos by section, position and title (the mock names another author for `v12`–`v19`; the titles and order are the original's) and show the video site's `0.jpg` thumbnail, as the original page did. `v2`–`v11` stay empty: the original lists those videos by author only, and the mock's titles do not match the videos' own titles |
| `video` | 900×506 | VideoModal (Feed) | no: the original played the trailer in a video-site embed; no picture |
| `competition-hero-170031`, `competition-hero-170026` | 805×380, 175×95 | FeedCarousel | yes: the project's posts reuse two competition pictures (a deer each, `NewsPosts.cs`), cropped to fill the slide |
| `help-banner`, `help-hero` | 940×250 | Help | yes: the visible crop of the original's tutorial cover |
| `help-dm-<interface, levels, cycle, achievements, rewards, resume-buy, resume-use>` | 484 to 940 wide | DayMissionHelp | 7 of 7 |
| `reward-<day>` | 60×60 | DayMissionHelp | 11 of 11 |
| `paw-badge-<easy, medium, hard>` | 100×111 | DailyMissions | 2 of 3: the original's `mission_icon_easy_02` and `mission_icon_medium_02` for 1 and 2 stars; no hard icon was saved |
| `mission-giver-<day>` | 150×150 | DailyMissions (not on the live site) | 1 of 7: `-1` is Vera Herzschlag, named in the original's day 1 mission text. The mock's other missions have no named giver (the original gives Angelica Rodriguez the bear missions), so they stay empty |
| `mission-picture-<day>` | 250×360 | DailyMissions (not on the live site) | 1 of 7: the original showed the male or female picture of the mission's species by the mission's gender; the mock's missions 2–7 state none |
| `competition-<icon>` | 45×45 | CompetitionTable | 2 of 2 |
| `competition-hero-<id>` | 980×340 | CompetitionView | 160 of 171 (one picture per species id, as the game's species table). 11 competitions for Axis Deer, Fallow Deer and Wood Grouse (species 54, 55, 57) stay empty: the original had no picture for those species |
| `item-<id>` | 150×110, 270×197, 30×30 | Store, ItemDetail, BundleDetail (the mock, without `SERVER_URL`) | 47 of 47, stand-ins: the mock items are fictional ("Blind C"), so each has a real picture of the same kind (hat, scope, tent, …), not of one particular item. Their ids are the mock's, and 40 of them are ids of the original's catalogue, so the live site never looks for them |
| `bundle-<id>` | 150×110, 190×207 | Store, BundleDetail (the mock) | 2 of 2 |
| `store-item-<id>` | 150×110, 270×197, 230×50, 256×187, 30×30 | Store, ItemDetail, BundleDetail, the weapons of SkillsTab and WeaponTable, the dogs of DogsTab (the live site: the server's items, with the original's ids; 230×50 in the skills, 150×30 and 36×36 in the statistics; the dogs use it on the mock too, with the dog item's own id: 256 wide as the original's, drawn whole) | 1,003 of 1,003: the item's `image` in the original's item table, `items/256/<image>.png` as the original store showed it |
| `store-bundle-<id>` | 150×110, 190×207 | Store, BundleDetail (the live site) | 129 of 129: as the original store, the bundle's own `image`, else its first item's |
| `em-pack` | 100×80 | BankModal (redeemed code) | yes |
| `em-pack-<ems>` | 100×80 | BankModal | 5 of 5 |
| `membership-<starter, special, basic>` | 100×70 | BankModal | 1 of 3: `starter` is the Wayfarer card (the original's one starter bundle, 3 months). `special`: the original had three bundle cards (Deer Hunter, Duck Hunter, Stealth Hunter) that the mock's two anonymous bundles cannot be matched to; `basic`: one card per length (3, 6, 12 months) under one key |
| `recruit-hero` | 940×310 | InviteFriends | yes |
| `bait-barrel` | 80×100 | GameManagement | yes |
| `reserve-<NN>` | 70×70 | LaunchMap | 12 of 12: the launch map's reserve badges, the normal art (coloured row of the original's permit sprite, in reserve id order) |
| `reserve-<NN>-<event>` | 70×70 | LaunchMap | 5 of 5 for `halloween_2026` (`reserve-01-halloween-2026`, 02, 03, 09, 10: the game's reserves 0, 1, 2, 8 and 9 of the event): the event's badge, drawn instead of `reserve-<NN>` only while the community server's event runs for that reserve and the player plays seasonal events ([launcher.md](launcher.md#seasonal-events)). The event name goes through `AssetImages.Key` like any slot name, so `halloween_2026` is `halloween-2026`; a reserve or event without a file keeps the normal badge, and the slot is never asked for outside the event, so `/images-missing.json` does not list it |
| `map-<NN>` (MapPlaceholder) | the map | LaunchMap, JoinMapModal | 12 of 12: the original's one world map (zoom 5 tiles) cut to a square around each reserve's border (6% margin, a square near the world's edge slid inward), scaled to 1500 px at most; the same square is the facts file's `reserves[].map`, the server's marker positions are percent of it. The boxes of the join map (704×608) stretch it |
| `marker-<kind>` (MapPlaceholder) | 24×24 | LaunchMap, JoinMapModal (the reserve maps; the score sheet's map keeps icons) | 9 of 9: the original's map marker sprite, one 32×32 picture a kind (the feeder is the sprite's broadcast feeder, the bait the barrel). Only lodge, campsite, tower and interest are the server's starts; tent, bait, feeder, treestand and groundblind are the mock's |
| `marker-<kind>-active` (MapPlaceholder) | 24×24 | LaunchMap (the selected or hovered marker) | 9 of 9: the sprite's second row, the red one. Optional: without it the selected marker keeps its picture, and it is not listed in `/images-missing.json` |
| `map-bg` (MapPlaceholder) | the map | SheetTab | no: the original score map was a map centred on the kill's position; no single picture fits |
| `species-<name>` | 135×135, 118×118 | Leaderboards | 59 of 59 |
| `rank-<animals, weapons, collectables>` | 114×114, 40×40 | RanksTab (the mock; the live site draws `rank-<id>` in the rows) | 3 of 3 (the overview rows show them at 40×40) |
| `rank-<id>` | 114×114 | RanksTab (the live site: a row of animals, weapons or collectables, `<id>` the rank's game id, the same ids as the game's rank table) | 115 files in the operator's folder |
| `rank-score` | 40×40 | RanksTab | yes: the hunter score icon of the ranks overview |
| `ach-<animals, weapons, exploration, day-mission>` | 100×100, 40×40 | AchievementsTab | 3 of 4 (the overview rows show the first three at 40×40): the original's `cat0` (animals) is the stag of its icon sprite, `cat1` the bullets, `cat2` the compass; `ach-day-mission`: the sprite has no day-mission icon (its paw cell is used by none of the original's templates) |
| `ach-icon-<icon>` | 150×150 | AchievementsTab (the live site: a milestone row) | the achievement's own `icon_url` (174 files): the original showed it on its category pages |
| `ach-badge-<title>` | 90×90 | AchievementsTab | 13 of 14: animal badges by the game's achievement table, challenge badges by the challenge's description (`Quick Draw` is the original's Assassin); `1 Rifle A (Standard)` is a stand-in for a weapon badge; `Early Bird` is not in the original |
| `skill-<name>` | 80×90 | SkillsTab | 78 of 81: weapon skills by the weapon category in the name; `i1` (semi-automatic rifles), `k1` and `l1` (air rifles) stay empty, the original's only icon of that kind is already used by another skill or is of another category |
| `statistics-<define>` | 80×90 | SkillsTab (the live site: the species' skill pictures) | `statistics/<define in lower case>.png` as the original showed it, 54 of the 55 species shown (`sasquatch` has none); the live weapon skills and the statistics' weapons and ammo use `store-item-<id>` |
| `trophy-<name>` | 147×147 | TrophiesTab (the mock) | 9 of 26: the 9 `Competition Trophy` slots are stand-ins (a real competition trophy each); the 17 plain `Trophy <n>` slots stay empty, nothing says which trophy they are |
| `trophy-<image>` | 147×147 | TrophiesTab (the live site: a competition trophy, `<image>` the file name of its picture without the extension, `trophy-elk_gold` for `…/elk_gold.png`; a trophy with no picture is drawn as `trophy-<name>`) | 1320 files in the operator's folder |
| `dog-skill-<skill>-<level>` | 40×40 | DogsTab (a breed skill of a dog: `<skill>` the skill's define without `DOG_SKILL_`, in lower case with `-` for `_`, `dog-skill-loyalty-stamina-1`; `<level>` its value, 1 and one more for each unlock reached) | the original's `dogskills/<define>_<value>.png`: 85 files in the operator's folder |
| `dog-trophy-ribbon-<n>` | 64×64 | DogsTab (one slot for the dog's `<n>` ribbons, as many as the unlocks reached; no slot without one) | no: the original's ribbon picture is not in the operator's files |
| `weapon-<name>`, `ammo-<name>` | 150×30, 36×36 | WeaponTable | 5 of 5, 5 of 5: the mock weapons are fictional (stand-in pictures of the same kind); each ammo is the one the game's item table lists as fitting that weapon's picture (.243, .30-06, .340 Weatherby, 7.62x54R, 12 GA birdshot) |
| `collectable-<name>` | 24×24 | CollectableTable | 1 of 1 |
| `sheet-logo` | 460×64 | SheetTab | yes |
| `sheet-medal` | 84×84 | SheetTab | no: none of the 1,241 saved score pages shows a medal |
| `sheet-sponsor` | 116×82 | SheetTab | yes |
| `antler-diagram-<species>`, `antler-guide-<species>` | 380×220, 560×460 | SheetTab | 1 of 1, 1 of 1 |

A player's own picture, imported from their backup, is no slot: the page draws the link the server gives (`https://<site>/photos/<account id>/avatar.jpg`) as an image, so it is never in this folder ([server.md](server.md#avatars)).
