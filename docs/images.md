# Pictures

The website and the launcher draw every picture of the original screens in a *slot*: `<AssetSlot Name=… Width=… Height=…>` (`src/Ui/Components/AssetSlot.razor`), the map of `MapPlaceholder` (`src/Ui/Components/MapPlaceholder.razor`) and the website's page background `page-bg` (`src/Ui/Layouts/ShellLayout.razor`). The repository holds no pictures: a slot is a labelled box unless the operator supplies its file; without `page-bg` the page is plain black.

## How it works

- The website reads the folder named by `IMAGES_DIR`. On the VPS that is `SERVER_DIR/images`, mounted read-only at `/images` in `web` (`deploy/compose.yml`, [deploy.md](deploy.md#the-community-server)).
- One file per slot, named `<key>.<png|jpg|jpeg|gif|webp|webm>`. The key is the slot's name in lower case, with every run of characters outside `a-z` and `0-9` made one `-` and none at either end (`AssetImages.Key`): the slot `species-Alpine Ibex` is the file `species-alpine-ibex.png`. A `.webm` plays in the slot as a muted looping video, as the original landing page's background did. SVG is not served: opened directly from the site, an SVG can run script.
- The files are served at `/images/<file>`, cached `immutable`; the page asks for `/images/<file>?v=<time of the file>`, so a changed file gets a new URL.
- `/images.json` lists the pictures for the launcher: `{"<file>": "<version>"}`, where the version is the file's `?v=`. It is read with the folder, once at start; `{}` without `IMAGES_DIR`.
- The website reads the folder once at start: restart `web` after changing files (`docker compose restart web`).
- A slot without a file shows its labelled box, and so does every slot without `IMAGES_DIR`: the public repository, `dotnet run`, CI. The CI smoke test checks both cases, `/images.json` and `/images-missing.json`.
- The map is drawn from `map-<NN>` (the reserve, two digits) on the launch and join maps, and `map-bg` on the score sheet's map; it zooms and pans with the markers.
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

104 `<AssetSlot>` in 53 `.razor` files, 3 `<MapPlaceholder>` (`LaunchMap`, `JoinMapModal`, `SheetTab`) and `page-bg` in `ShellLayout`. Recount:

```
grep -rho '<AssetSlot' src/Ui --include=*.razor | wc -l
grep -rln '<AssetSlot' src/Ui --include=*.razor | wc -l
grep -rn '<MapPlaceholder' src/Ui --include=*.razor
```

With the values the mock services give, the slots have 657 distinct keys; a pattern's names come from the data of the page (`item-<id>`: one picture per store item, scaled to each slot that shows it). "Have it" says whether the operator's files hold the original picture of that place: `yes`, `no` with the reason, or the count for a pattern. Reasons: *player content* (uploaded by players: never used), *no original picture* (the original showed none there), *not in the operator's files*. The column is kept by hand; `/images-missing.json` gives the current state of the folder.

| Name or pattern | Size (W×H) | Where used | Have it |
|---|---|---|---|
| `logo-large` | 654×164, 300×56, 300×50, 240×60 | BrowserNotice, SignupCard, PublicBar, Eula | yes |
| `logo-bar` | 173×44 | StatusBar | yes |
| `logo-hero` | 485×120 | Feed | yes |
| `logo-landing` | 1000×250 | AutoLogin | yes |
| `logo-splash` | 483×65 | Splash | yes |
| `logo-dev` | 200×71 | Footer | yes |
| `logo-rating-<1-4>` | 62×82 | Footer | 4 of 4 |
| `bg-hero` | 980×550 | Feed | yes: `bg-hero.webm`, the original landing page's background video (`landing_page_video.webm`, played muted and looping as there) |
| `browser-bg` | 1280×760 | BrowserNotice | yes |
| `page-bg` | behind the page, top centre, once, black below | ShellLayout (website only, as the original: its launcher had none) | yes: the original's `main_page_background2.jpg` (`body:not(.client)` in its stylesheet) |
| `frame-leather` | 1280×760 | SignupLayout | yes |
| `signup-bg-<1-5>` | 1200×674 | SignupCard (signup pages) | 5 of 5 |
| `gift-frame` | 561×288 | Signup Page2 to Page5 | yes |
| `gift-shotgun`, `gift-licenses`, `gift-membership` | 467×99, 476×193, 441×149 | Signup Page2, Page3, Page4 | no: not in the operator's files |
| `paper-notice` | set by the page | Paper | yes |
| `coin-ems`, `icon-store` | 46×46 | MenuBar | yes |
| `avatar-default` | 24 to 160 square | MapPlaceholder, RankRow, JoinMapModal, Dashboard, Leaderboards, Messages, Servers, StatusBar, FriendsTab, FriendsDropdown, HunterProfile, Settings, Welcome | yes |
| `avatar-<NN>`, `avatar-locked` | 34 to 170 | Settings, StatusBar, Welcome, HunterProfile, Signup Page1 | 55 of 55 |
| `avatar-discord` | 18×18 | DiscordWidget | no: player content |
| `profile-banner` | 940×200 | HunterProfile | no: player content (the player's latest kill) |
| `animals-<n>`, `weapons-<n>`, `exploration-<n>`, `events-<n>` (profile headers) | 940×200, 240×50 | HunterProfile, Settings | 20 of 20 |
| `photo-<id>` | 210×118, 900×506 | GalleryTab, VideoModal | 0 of 30: player content |
| `video-<id>` | 900×506 | VideoModal (Help) | 0 of 19: not in the operator's files |
| `video` | 900×506 | VideoModal (Feed) | no: the original played the trailer in a video-site embed; no picture |
| `news-<target>` | 805×380, 175×95 | FeedCarousel | 6 of 6 |
| `splash-<image>` (live site: the file name of the slide's recorded picture, lower case, without its extension) | 805×380, 175×95 | FeedCarousel with `SERVER_URL` | 0: the original's slides, not mapped yet |
| `help-banner`, `help-hero` | 940×245, 940×250 | Help | yes |
| `help-dm-<interface, levels, cycle, achievements, rewards, resume-buy, resume-use>` | 484 to 940 wide | DayMissionHelp | 7 of 7 |
| `reward-<day>` | 60×60 | DayMissionHelp | 11 of 11 |
| `paw-badge-<easy, medium, hard>` | 80×80 | DailyMissions | 2 of 3: the original's `mission_icon_easy_02` and `mission_icon_medium_02` for 1 and 2 stars; no hard icon was saved |
| `mission-giver-<day>` | 150×150 | DailyMissions | 1 of 7: `-1` is Vera Herzschlag, named in the original's day 1 mission text. The mock's other missions have no named giver (the original gives Angelica Rodriguez the bear missions), so they stay empty |
| `mission-picture-<day>` | 250×250 | DailyMissions | 1 of 7: not in the operator's files |
| `competition-<icon>` | 45×45 | CompetitionTable | 2 of 2 |
| `competition-hero-<id>` | 980×340 | CompetitionView | 160 of 171 (one picture per species id, as the game's species table). 11 competitions for Axis Deer, Fallow Deer and Wood Grouse (species 54, 55, 57) stay empty: the original had no picture for those species |
| `item-<id>` | 150×110, 270×197, 30×30 | Store, ItemDetail, BundleDetail (the mock, without `SERVER_URL`) | 47 of 47, stand-ins: the mock items are fictional ("Blind C"), so each has a real picture of the same kind (hat, scope, tent, …), not of one particular item. Their ids are the mock's, and 40 of them are ids of the original's catalogue, so the live site never looks for them |
| `bundle-<id>` | 150×110, 190×207 | Store, BundleDetail (the mock) | 2 of 2 |
| `store-item-<id>` | 150×110, 270×197, 30×30 | Store, ItemDetail, BundleDetail (the live site: the server's items, with the original's ids) | 0 of 1,003: *not mapped yet*. A picture goes in only with evidence that it is that item's, as in the rules of the mirror task (#201) |
| `store-bundle-<id>` | 150×110, 190×207 | Store, BundleDetail (the live site) | 0 of 129: *not mapped yet* |
| `em-pack` | 100×80 | BankModal (redeemed code) | yes |
| `em-pack-<ems>` | 100×80 | BankModal | 5 of 5 |
| `membership-<starter, special, basic>` | 100×70 | BankModal | 1 of 3: `starter` is the Wayfarer card (the original's one starter bundle, 3 months). `special`: the original had three bundle cards (Deer Hunter, Duck Hunter, Stealth Hunter) that the mock's two anonymous bundles cannot be matched to; `basic`: one card per length (3, 6, 12 months) under one key |
| `recruit-hero` | 940×310 | InviteFriends | yes |
| `bait-barrel` | 80×100 | GameManagement | yes |
| `reserve-<NN>` | 70×70 | LaunchMap | 12 of 12: the launch map's reserve badges (coloured row of the original's permit sprite, in reserve id order) |
| `map-<NN>` (MapPlaceholder) | the map | LaunchMap, JoinMapModal | 12 of 12: the original's one world map (zoom 5 tiles) cut to a square around each reserve's border, scaled to 1500 px at most. The boxes of the join map (704×608) stretch it |
| `map-bg` (MapPlaceholder) | the map | SheetTab | no: the original score map was a map centred on the kill's position; no single picture fits |
| `species-<name>` | 135×135, 118×118 | Leaderboards | 59 of 59 |
| `rank-<animals, weapons, collectables>` | 110×110 | RanksTab | 3 of 3 |
| `ach-<animals, weapons, exploration, day-mission>` | 100×100 | AchievementsTab | 3 of 4: the original's `cat0` (animals) is the stag of its icon sprite, `cat1` the bullets, `cat2` the compass; `ach-day-mission`: the sprite has no day-mission icon (its paw cell is used by none of the original's templates) |
| `ach-badge-<title>` | 90×90 | AchievementsTab | 13 of 14: animal badges by the game's achievement table, challenge badges by the challenge's description (`Quick Draw` is the original's Assassin); `1 Rifle A (Standard)` is a stand-in for a weapon badge; `Early Bird` is not in the original |
| `skill-<name>` | 80×90 | SkillsTab | 78 of 81: weapon skills by the weapon category in the name; `i1` (semi-automatic rifles), `k1` and `l1` (air rifles) stay empty, the original's only icon of that kind is already used by another skill or is of another category |
| `trophy-<name>` | 150×150 | TrophiesTab | 9 of 26: the 9 `Competition Trophy` slots are stand-ins (a real competition trophy each); the 17 plain `Trophy <n>` slots stay empty, nothing says which trophy they are |
| `dog-<type>` | 120×120 | DogsTab | 2 of 2 |
| `weapon-<name>`, `ammo-<name>` | 150×30, 36×36 | WeaponTable | 5 of 5, 5 of 5: the mock weapons are fictional (stand-in pictures of the same kind); each ammo is the one the game's item table lists as fitting that weapon's picture (.243, .30-06, .340 Weatherby, 7.62x54R, 12 GA birdshot) |
| `collectable-<name>` | 24×24 | CollectableTable | 1 of 1 |
| `sheet-logo` | 460×64 | SheetTab | yes |
| `sheet-medal` | 84×84 | SheetTab | no: none of the 1,241 saved score pages shows a medal |
| `sheet-sponsor` | 116×82 | SheetTab | yes |
| `antler-diagram-<species>`, `antler-guide-<species>` | 380×220, 560×460 | SheetTab | 1 of 1, 1 of 1 |
