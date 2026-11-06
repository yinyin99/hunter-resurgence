# Website

`src/Web/` is the community website. It is a Blazor Web App with interactive server rendering: pages are rendered on the server and kept alive over a SignalR connection, so it needs JavaScript. The pages and the theme live in `src/Ui/`, the same library the [launcher](launcher.md) uses; the website only adds the host, the language switch and the development switches.

## State

The screens follow the original website screen for screen (same theme, places, navigation and English texts; French uses the original French). Everything behind them is a **mock** until the community server exists: the services in `src/Services/Mocks/` return fixed, fictional data and the actions do nothing real. The pictures are not in the repo: each one is a labelled box unless the operator supplies its file ([images.md](images.md)). See [launcher.md](launcher.md) for the service layer in `src/Services/Contracts/`.

The mock session starts signed in as a fictional hunter (`Hunter01`); sign out in the top bar to see the public site. Sign-in is the email, then a six-digit code (there is no password); the mock accepts any address with an `@` and any six digits.

## Sign-in

Without `SERVER_URL` (development, the CI smoke test) everything above holds: the mock session and the email sign-in panel.

With `SERVER_URL` set (for example `http://server:8080`, the community server's address on the internal network) the website uses the server's Steam sign-in, which both serve on the same domain behind one reverse proxy. The server's `web_host` setting must be this website's domain, and the proxy sends `/auth/*` to the server ([deploy.md](deploy.md#adding-the-server-service-or-another-site)).

- **Sign in**: "Already a hunter? Sign in" in the public bar (`src/Ui/Shell/PublicBar.razor`) is a plain link to `/auth/steam/login?next=<current path>`, a full page load. The server sends the player to Steam, asks for a handle the first time, sets the session cookie `hr_session` (HttpOnly, 30 days) and returns to `next`.
- **Who is signed in**: `ServerSessionService` (`src/Services/`) asks the server `GET /auth/me` with the browser's cookie; 200 gives the handle and the hunterscore (the status bar's paw), anything else (401, no answer within 5 s, an error) means signed out and never reaches the page. The paths and the JSON are in `src/Services/Contracts/ServerSessionApi.cs`. Nothing is cached: every read asks again. The answer also carries the wallet: `ServerWalletService` makes its own `GET /auth/me` for the balances (the status bar's em$ and gm$; 0 when the server does not answer), and the status bar shows no ammo or fuel days, which the server does not know.
- **Sign out**: the sign-out button opens `/logout`, which only shows a "Sign Out" button (opening the address signs nobody out); the click calls `POST /auth/signout` with the cookie and `X-Requested-With: XMLHttpRequest`, then shows the public site. The server deletes the session; if it cannot be reached the website is signed out for that tab only.
- **The cookie reaches the server** because the website forwards it: `src/Web/Components/App.razor` reads `hr_session` and the request's `Host` from the page request and passes them to `Routes` as parameters (the page's component marker is protected by data protection, so the browser cannot read it), where they fill a per-circuit holder. Blazor's `HttpContext` is not reliable once the circuit runs, so nothing reads it later. The server picks its site by `Host`, so the website sends the host the browser used.
- **The hunterscore** is the server's ([docs/server.md](server.md#the-hunterscore)) in three places: the status bar, the profile header of every real player (`ServerProfileService` asks `GET /hunters/<handle>`: handle, hunterscore and rank; a handle the server does not know is a fictional hunter of the mock pages, and a failed answer shows no profile) and the hunterscore board (`ServerLeaderboardsService`: `GET /hunters?page=<n>` for the rows, `GET /auth/me` for "Your rank"; a failed answer is an empty board). These two are public and need no cookie; the proxy does not have to route `/hunters/`.
- **The live site** (`HostMode.Live`, true exactly when `SERVER_URL` is set): a signed-in player sees only their own data from the server, or nothing. These pages are not served (`LauncherGuard.MockOnly`: missions, wallet, bank, the Steam callback, messages, new message, settings, game management, invite friends, welcome, community league; the address goes back to the feed), and their links are gone: the menu's "get em$" and missions items, the settings menu's account and game management items (the "Settings" heading opens "Restore your backup"), the competitions page's "Community league" tab, and in the status bar the friends button and list, the messages and wallet links (the em$ and gm$ balances stay, as plain text), the friend and message counts and the account's avatar. What the player owns or has joined is gone from the pages that stay: the store's "you own" lines, "not buyable" and the bundle's "Own" column, the competitions' "enrolled" list, join and leave buttons, "your position" and attempts, and "your rank" on every board but the hunterscore. Buying is the mock's and would spend a wallet that is not the account's, so the buy buttons (store list, item and bundle), the taxidermy button and the bank dialogs are hidden too. No profile offers a friend request (the friends are the mock's). A real player's profile (`Profile.Real`) is the header only: handle, hunterscore and rank, without the membership or the tabs. A handle the server does not know is a fictional hunter of the mock pages, never the signed-in player's own, even when typed by hand. Without `SERVER_URL` (development, the launcher) nothing is hidden.
- **The store** (`ServerStoreService`, with `SERVER_URL`) is the community server's catalogue ([docs/server.md](server.md#the-store-catalogue)): the original's categories, items with their prices, the sales (the struck-through price) and the bundles, for everyone, signed in or not. The server's answer is kept for 5 minutes and shared by every visitor (`StoreCache`); a server that does not answer, or holds no catalogue, is an empty store ("No items in this category"), never the mock's. What the player owns is not known, and buying is not served yet, so the "you own" lines and the buy buttons stay hidden (`HostMode.Live`) until the store buys ([#210](https://github.com/yinyin99/hunter-resurgence/issues/210)). The pictures: an item or bundle of the live site looks for `store-item-<id>` and `store-bundle-<id>`, never the mock's `item-<id>` and `bundle-<id>` ([docs/images.md](images.md)), so it is a labelled box until its picture is mapped.
- **Still mock, by design**: the signed-out preview, and the fictional hunters of the mock pages (profiles with tabs, boards). The other catalogue pages (news, competitions, the other boards, help) are the mock's until the community server's catalogue feeds them ([#210](https://github.com/yinyin99/hunter-resurgence/issues/210)). The e-mail, handle and agreements methods of the session do not apply (the agreements count as accepted).

## Restore and your data

With `SERVER_URL` set, two pages are real; the rest are the catalogue's mock or hidden ([the live site](#sign-in)). They need the community server, and without it the restore page says it is not available here.

- **`/restore`** (signed in; the settings menu has "Restore your backup"): choose the zip the backup tool made, "Upload". The page sends it with `ServerRestoreService` (`src/Services/`) to `POST /account/restore` of the server ([docs/server.md](server.md#upload-a-backup-zip)) with the player's cookie, and shows a headline for the result (`Restore.Result.<status>`: kept for review, already restored, user id in use, not a usable backup, try again later, file too large) with the server's own plain words under it and the technical report under "Details". The rules shown on the page are the server's: every zip is kept for an operator to check by hand and is never restored automatically; a backup is restored once. The upload goes browser to the website's server over its SignalR connection (`InputFile`, at most 64 MiB), then to the community server; the website's client for it has a 2 minute timeout where the sign-in's has 5 s. Below, "Delete my data" (a tick box first) calls `POST /account/delete` and loads the public site.
- **`/your-data`** (public; "Your data" in the footer): what the server stores (Steam id, handle, the game data, each uploaded zip, kept and after it is restored, hashed tokens and cookies), what accounts do not hold (e-mail, real name, address, phone) and how to delete. The wording is the page's keys `Data.*` in `src/Ui/Strings*.resx`.

The texts are translated in every language file of `src/Ui`. The server's messages under a result are English.

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

`settings.json` in `src/Web/` is read for the game name and the external links (`game_name`, `game_full`, `link_*`); the keys are listed in [launcher.md](launcher.md#settingsjson). The `GAME_NAME` and `GAME_FULL` environment variables win over its two names; the live site gets them from the repository variables of the same names at every deploy ([deploy.md](deploy.md#deploys)). Without the file and the variables the texts say "the game" and the links are left out. The real file is git-ignored.

`IMAGES_DIR` names the folder of the operator's pictures, one file per slot, served at `/images/` ([images.md](images.md)). Unset, every picture is its labelled box. Their list is at `/images.json`, for the launcher, and the slots drawn without one at `/images-missing.json`.

The pages use native dialogs, popovers and CSS anchor positioning (dropdowns, menus, modals): use a current Chrome or Edge (the launcher's WebView2 runtime is one). Other browsers are not tested.

The JavaScript modules of `src/Ui/wwwroot` (`shell.js`) are imported by their plain path, `./_content/Resurgence.Ui/shell.js`. On the website, `<ImportMap />` in `src/Web/Components/App.razor` maps that path to the fingerprinted file (`shell.<hash>.js`, served `immutable`), whose URL changes with every build, so a browser never runs a `shell.js` from an older deploy. The plain URL is not enough: the app sends it with `no-cache`, but Cloudflare's Browser Cache TTL raises that to 4 hours. The CI docker smoke test checks that `/feed` names the fingerprinted file. The launcher needs no map: its BlazorWebView serves every file with `no-store`.

The paw favicon (`favicon.ico`, `favicon.svg`, which turns light in a dark theme, `favicon-32.png` and `apple-touch-icon.png`) lives in `src/Web/wwwroot/`, and `App.razor` links it through `@Assets[...]`, so its URLs are fingerprinted and served `immutable`, while the plain paths such as `/favicon.ico` answer too. The launcher's icon is made from `favicon.svg`; when the paw changes, regenerate it ([launcher.md](launcher.md#icon)).

## Search engines

Every page's `<head>` (`src/Web/Components/App.razor`) has a meta description (`App.Description`) and the link preview tags `og:type`, `og:title` (`Landing.Heading`), `og:description` and `og:image` (the paw's `apple-touch-icon.png`, as an absolute URL built from the request's scheme and host). They are in the page's language and say `{game_full}`, so the game's name comes from `GAME_FULL` or `settings.json` and the repository never holds it; without either they say "the game". The landing page (`/`, `src/Ui/Pages/Feed.razor`) has `Landing.Heading` as its `<title>` and in a visually hidden `<h1>`: the hero's logo is the visible title. There is no canonical link: the website has one host, and the language comes from the browser, not the URL. The CI docker smoke test checks the `og:image` URL and the `<h1>`.

`src/Web/wwwroot/robots.txt` (served at `/robots.txt`) turns away two SEO backlink crawlers, `MJ12bot` and `serpstatbot`: they sent most of the website's requests and brought no visitors, and every page they fetch is rendered on the server. It also keeps the Blazor hub (`/_blazor/`) and the pages that give a crawler only an empty shell or a redirect out of search engines: sign-in, sign-up, payment, per-session and launcher-only pages. The news, profiles, competitions, leaderboards, store, help and download stay open. It is a request, not access control: the CDN's bot rules handle crawlers that ignore it. The CI docker smoke test checks that it is served.

## Security headers

A middleware, the first one in `src/Web/Program.cs`, adds these headers to every response (pages, static files, the 404 page, the Blazor endpoints):

| Header | Value |
|---|---|
| `Strict-Transport-Security` | `max-age=31536000` (one year, this host only: no `includeSubDomains`, no `preload`) |
| `X-Content-Type-Options` | `nosniff` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `Content-Security-Policy` | `frame-ancestors 'none'` |

HSTS is sent over http too (`dotnet run`, the CI smoke test): browsers ignore it there. The CSP only forbids framing (clickjacking); a wider policy would have to allow the inline `<ImportMap />` script and the offerwall iframe (`src/Ui/Modals/OfferwallModal.razor`). Blazor's own `frame-ancestors 'self'` header is turned off (`ContentSecurityFrameAncestorsPolicy = null` in `Program.cs`) so this is the only CSP. Responses that issue an antiforgery token also get `X-Frame-Options: SAMEORIGIN` from the antiforgery; browsers ignore it because the CSP is there. The CI docker smoke test checks all four on `/feed` and on a 404.

## Two views of one app

The website is signed-in or public:

- **Signed out**: the public top bar (`Already a hunter? Sign in`), the news with a play button, the store and its items, leaderboards, competitions, help, download, and hunter profiles and score sheets. Pages that need an account (missions, wallet, bank, messages, settings, game management, invite, welcome, community league) go back to the news.
- **Signed in**: the same menu as the launcher without the launcher-only items. The pages that only the launcher has (`/dashboard`, `/servers`, `/launch-map/...`, `/tutorials` and the sign-up screens) go back to the news. With `SERVER_URL` the pages of mock account data go back to the news too, see [the live site](#sign-in).

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

A test in `tests/Resurgence.Tests` checks that all 18 non-English files have the same keys and the same `{0}` placeholders as the English one, and that no key holds its English text in all 18 (a key copied, not translated; names such as `App.Name` are listed in the test). Native speakers are welcome to check any of them.

## Tests

The website and the pages of `src/Ui` are tested in `tests/Resurgence.Tests/WebTests`, without a browser:

- Component tests (bUnit, `UiContext`): a page is rendered with the service mocks and asserted on its markup. Anything behind the mocks' delay is awaited with `WaitForAssertion`.
- Website tests (`WebAppTests`, `WebApplicationFactory`): the real `src/Web` app runs in memory in the Development environment, with no `SERVER_URL` unless a test sets it. They check the security headers, the page language, the cookie policy, `/images.json` and the services chosen by `SERVER_URL`. They read `src/Web/settings.json` when it exists.

Run one class with `dotnet test --project tests/Resurgence.Tests -c Release --filter-class Resurgence.Tests.WebTests.WebAppTests`.
