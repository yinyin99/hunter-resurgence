# Website

`src/Web/` is the community website. It is a Blazor Web App with interactive server rendering: pages are rendered on the server and kept alive over a SignalR connection, so it needs JavaScript. The pages and the theme live in `src/Ui/`, the same library the [launcher](launcher.md) uses; the website only adds the host, the language switch and the development switches.

## State

The screens follow the original website screen for screen (same theme, places, navigation and English texts; French uses the original French). Everything behind them is a **mock** until the community server exists: the services in `src/Services/Mocks/` return fixed, fictional data and the actions do nothing real. The pictures of the original are not included: each one is a neutral labelled box. See [launcher.md](launcher.md) for the service layer in `src/Services/Contracts/`.

The mock session starts signed in as a fictional hunter (`Hunter01`); sign out in the top bar to see the public site. Sign-in is the email, then a six-digit code (there is no password); the mock accepts any address with an `@` and any six digits.

## Backup tool

Until the official servers close, every website page (signed in or out) offers the backup tool twice. The work-in-progress banner has a "Save your profile before then: download the backup tool" link. A large orange button with the same text and a download icon (`src/Ui/Shell/BackupButton.razor`) sits at the top left: in the window margin left of the top bar's logo when the window is at least 1880px wide, otherwise just under the logo. It stays in place while the page scrolls. The plain pages of `BrowserLayout` (`/auto-login`, `/email/verify`, `/email/confirm`, `/email/change`, `/thank-you`, `/logout`) have only the banner link. The launcher shows the same button, always under the logo, but no banner. Both open a dialog (`src/Ui/Modals/BackupDialog.razor`) with README's three steps and the SmartScreen note, plus a "Download the backup tool" button. The steps follow README's "For players: the backup tool": change both together.

The dialog also opens by itself on a browser's first visit, unless another dialog is open. The `backup_tool` cookie (one year) remembers that it was shown.

The download button links `link_backup` from `settings.json`, meant to be `https://github.com/<owner>/<repo>/releases/latest/download/backup-win-x64.zip` (always the newest non-prerelease release zip). The `LINK_BACKUP` environment variable wins over it; the live site gets it from the repository variable `LINK_BACKUP` at every deploy ([deploy.md](deploy.md#deploys)). Without the key and the variable, neither the banner link, the orange button nor the dialog is shown.

From 2026-11-12 00:00 UTC all are hidden (`BackupDialog.Offered`).

## Build and run

```
dotnet build hunter-resurgence.slnx -c Release
dotnet run --project src/Web
```

The development profile listens on `http://localhost:5080`.

Deploy: the container image and the VPS setup are in [deploy.md](deploy.md).

`settings.json` in `src/Web/` is read for the game name and the external links (`game_name`, `game_full`, `link_*`); the keys are listed in [launcher.md](launcher.md#settingsjson). Without it the texts say "the game" and the links are left out. The real file is git-ignored.

The pages use native dialogs, popovers and CSS anchor positioning (dropdowns, menus, modals): use a current Chrome or Edge (the launcher's WebView2 runtime is one). Other browsers are not tested.

The JavaScript modules of `src/Ui/wwwroot` (`shell.js`) are imported by their plain path, `./_content/Resurgence.Ui/shell.js`. On the website, `<ImportMap />` in `src/Web/Components/App.razor` maps that path to the fingerprinted file (`shell.<hash>.js`, served `immutable`), whose URL changes with every build, so a browser never runs a `shell.js` from an older deploy. The plain URL is not enough: the app sends it with `no-cache`, but Cloudflare's Browser Cache TTL raises that to 4 hours. The CI docker smoke test checks that `/feed` names the fingerprinted file. The launcher needs no map: its BlazorWebView serves every file with `no-store`.

The paw favicon (`favicon.ico`, `favicon.svg`, which turns light in a dark theme, `favicon-32.png` and `apple-touch-icon.png`) lives in `src/Web/wwwroot/`, and `App.razor` links it through `@Assets[...]`, so its URLs are fingerprinted and served `immutable`, while the plain paths such as `/favicon.ico` answer too.

## Two views of one app

The website is signed-in or public:

- **Signed out**: the public top bar (`Already a hunter? Sign in`), the news with a play button, the store and its items, leaderboards, competitions, help, download, and hunter profiles and score sheets. Pages that need an account (missions, wallet, bank, messages, settings, game management, invite, welcome, community league) go back to the news.
- **Signed in**: the same menu as the launcher without the launcher-only items. The pages that only the launcher has (`/dashboard`, `/servers`, `/launch-map/...`, `/tutorials` and the sign-up screens) go back to the news.

Four plain pages are meant for the browser the old launcher opened: `/auto-login`, `/email/verify` (also `/email/confirm`, `/email/change`), `/thank-you` and `/logout`. They have no menu.

An unknown address answers with HTTP 404 and the "Page not found" page.

## Development switches

Only when the app runs in the Development environment (the default with `dotnet run`):

| Switch | Effect |
|---|---|
| `?mode=launcher` or `?mode=web` | renders the launcher view or the website view; remembered in a cookie. Screens built for the launcher can be looked at in a normal browser this way |
| `?mock=<state>` | puts a mock service in a state (cookie too); `?mock=none` clears it |
| `?lang=<code>` | picks the language: any of the 19 codes of `Languages.All` (`en`, `fr`, `de`, `pt-BR`, ...); this one works in every environment |
| `?alert=<text>` | opens the alert dialog with that text |
| `?modal=settings` or `?modal=initial` | opens the game settings dialog, or the first-start variant |

States: all those of [launcher.md](launcher.md#development-switches) (patch panel, account, store, wallet, bank, friends, messages, missions, news, profile, `session:out` for the public site, `session:signup`, `eula`, `start:hold`, `browser:hold`, `poll:fast` ...), plus these, which exist only on the website: `competitions:many` (45 entrants on a competition's leaderboard, three pages), `servers:none` (no games), `loadouts` (three saved loadouts), `tents` (two tents on the map) and `membership` (the signed-in hunter has a membership).

## Languages

The 19 languages and the resx files (`src/Ui/Strings.resx` and one `Strings.<code>.resx` each) are the launcher's: see [launcher.md](launcher.md#languages). The website picks the language from the browser's `Accept-Language` header, highest quality first, matching by `Languages.Match` (region ignored: `de-AT` is German, bare `pt` is Portuguese (Brazil), `no` and `nn` are Norwegian bokmål), else English. `?lang=<code>` overrides it and is remembered in a cookie.

A test in `tests/Resurgence.Tests` checks that all 18 non-English files have the same keys and the same `{0}` placeholders as the English one. Native speakers are welcome to check any of them.
