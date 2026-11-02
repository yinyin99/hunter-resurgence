# Pictures

The website and the launcher draw every picture of the original screens in a *slot*: `<AssetSlot Name=… Width=… Height=…>` (`src/Ui/Components/AssetSlot.razor`) and the map of `MapPlaceholder` (`src/Ui/Components/MapPlaceholder.razor`). The repository holds no pictures: a slot is a labelled box unless the operator supplies its file.

## How it works

- The website reads the folder named by `IMAGES_DIR`. On the VPS that is `SERVER_DIR/images`, mounted read-only at `/images` in `web` (`deploy/compose.yml`, [deploy.md](deploy.md#the-community-server)).
- One file per slot, named `<key>.<png|jpg|jpeg|gif|webp>`. The key is the slot's name in lower case, with every run of characters outside `a-z` and `0-9` made one `-` and none at either end (`AssetImages.Key`): the slot `species-Alpine Ibex` is the file `species-alpine-ibex.png`. SVG is not served: opened directly from the site, an SVG can run script.
- The files are served at `/images/<file>`, cached `immutable`; the page asks for `/images/<file>?v=<time of the file>`, so a changed file gets a new URL.
- The website reads the folder once at start: restart `web` after changing files (`docker compose restart web`).
- A slot without a file shows its labelled box, and so does every slot without `IMAGES_DIR`: the public repository, `dotnet run`, CI. The CI smoke test checks both cases.
- The map is drawn from `map-<NN>` (the reserve, two digits) on the launch and join maps, and `map-bg` on the score sheet's map; it zooms and pans with the markers.
- The launcher keeps the boxes: it renders `src/Ui` itself with no server address (issue #175).

## Inventory

104 `<AssetSlot>` in 53 `.razor` files, and 3 `<MapPlaceholder>` (`LaunchMap`, `JoinMapModal`, `SheetTab`). Recount:

```
grep -rho '<AssetSlot' src/Ui --include=*.razor | wc -l
grep -rln '<AssetSlot' src/Ui --include=*.razor | wc -l
grep -rn '<MapPlaceholder' src/Ui --include=*.razor
```

With the values the mock services give, the slots have 657 distinct keys; a pattern's names come from the data of the page (`item-<id>`: one picture per store item, scaled to each slot that shows it). "Have it" says whether the operator's files hold the original picture of that place: `yes`, `no` with the reason, or the count for a pattern. Reasons: *player content* (uploaded by players: never used), *no original picture* (the original showed none there), *not in the operator's files*.

| Name or pattern | Size (W×H) | Where used | Have it |
|---|---|---|---|
| `logo-large` | 654×164, 300×56, 300×50, 240×60 | BrowserNotice, SignupCard, PublicBar, Eula | yes |
| `logo-bar` | 173×44 | StatusBar | yes |
| `logo-hero` | 485×120 | Feed | yes |
| `logo-landing` | 1000×250 | AutoLogin | yes |
| `logo-splash` | 483×65 | Splash | yes |
| `logo-dev` | 200×71 | Footer | yes |
| `logo-rating-<1-4>` | 62×82 | Footer | 4 of 4 |
| `bg-hero` | 980×550 | Feed | no: no original picture (a video) |
| `browser-bg` | 1280×760 | BrowserNotice | yes |
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
| `video-<id>`, `video` | 900×506 | VideoModal (Help, Feed) | 0 of 19, `video` no: not in the operator's files / no original picture |
| `news-<target>` | 805×380, 175×95 | FeedCarousel | 6 of 6 |
| `help-banner`, `help-hero` | 940×245, 940×250 | Help | yes |
| `help-dm-<interface, levels, cycle, achievements, rewards, resume-buy, resume-use>` | 484 to 940 wide | DayMissionHelp | 7 of 7 |
| `reward-<day>` | 60×60 | DayMissionHelp | 11 of 11 |
| `paw-badge` | 80×80 | DailyMissions | yes |
| `mission-giver-<day>` | 150×150 | DailyMissions | 3 of 7: not in the operator's files |
| `mission-picture-<day>` | 250×250 | DailyMissions | 1 of 7: not in the operator's files |
| `competition-<icon>` | 45×45 | CompetitionTable | 2 of 2 |
| `competition-hero-<id>` | 980×340 | CompetitionView | 171 of 171 (one picture per species) |
| `item-<id>` | 150×110, 270×197, 30×30 | Store, ItemDetail, BundleDetail | 47 of 47 |
| `bundle-<id>` | 150×110, 190×207 | Store, BundleDetail | 2 of 2 |
| `em-pack` | 100×80 | BankModal (redeemed code) | yes |
| `em-pack-<ems>` | 100×80 | BankModal | 5 of 5 |
| `membership-<starter, special, basic>` | 100×70 | BankModal | 0 of 3: not in the operator's files (one sheet of all cards) |
| `recruit-hero` | 940×310 | InviteFriends | yes |
| `bait-barrel` | 80×100 | GameManagement | yes |
| `reserve-<NN>` | 70×70 | LaunchMap | 0 of 12: not in the operator's files |
| `map-<NN>` (MapPlaceholder) | the map | LaunchMap, JoinMapModal | 0 of 12: not in the operator's files |
| `map-bg` (MapPlaceholder) | the map | SheetTab | no: no original picture |
| `species-<name>` | 135×135, 118×118 | Leaderboards | 59 of 59 |
| `rank-<animals, weapons, collectables>` | 110×110 | RanksTab | 3 of 3 |
| `ach-<animals, weapons, exploration, day-mission>` | 100×100 | AchievementsTab | 3 of 4, `ach-day-mission`: no original picture |
| `ach-badge-<title>` | 90×90 | AchievementsTab | 13 of 14: not in the operator's files |
| `skill-<name>` | 80×90 | SkillsTab | 81 of 81 |
| `trophy-<name>` | 150×150 | TrophiesTab | 26 of 26 |
| `dog-<type>` | 120×120 | DogsTab | 2 of 2 |
| `weapon-<name>`, `ammo-<name>` | 150×30, 36×36 | WeaponTable | 5 of 5, 5 of 5 |
| `collectable-<name>` | 24×24 | CollectableTable | 1 of 1 |
| `sheet-logo` | 460×64 | SheetTab | yes |
| `sheet-medal` | 84×84 | SheetTab | no: no original picture |
| `sheet-sponsor` | 116×82 | SheetTab | yes |
| `antler-diagram-<species>`, `antler-guide-<species>` | 380×220, 560×460 | SheetTab | 1 of 1, 1 of 1 |
