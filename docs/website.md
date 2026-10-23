# Website

`src/Web/` is the community website. It is a Blazor Web App with interactive server rendering: pages are rendered on the server and kept alive over a SignalR connection, so it needs JavaScript. The pages and the theme live in `src/Ui/`, the same library the [launcher](launcher.md) uses; the website only adds the host, the language switch and the development switches.

## State

The screens follow the original website screen for screen (same theme, places, navigation and English texts; French uses the original French). Everything behind them is a **mock** until the community server exists: the services in `src/Services/Mocks/` return fixed, fictional data and the actions do nothing real. The pictures of the original are not included: each one is a neutral labelled box. See [launcher.md](launcher.md) for the service layer in `src/Services/Contracts/`.

The mock session starts signed in as a fictional hunter (`Hunter01`); sign out in the top bar to see the public site. Sign-in is the email, then a six-digit code (there is no password); the mock accepts any address with an `@` and any six digits.

## Build and run

```
dotnet build hunter-resurgence.slnx -c Release
dotnet run --project src/Web
```

The development profile listens on `http://localhost:5080`.

`settings.json` in `src/Web/` is read for the game name and the external links (`game_name`, `game_full`, `link_*`); the keys are listed in [launcher.md](launcher.md#settingsjson). Without it the texts say "the game" and the links are left out. The real file is git-ignored.

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
| `?lang=en` or `?lang=fr` | picks the language (this one works in every environment) |
| `?alert=<text>` | opens the alert dialog with that text |
| `?modal=settings` or `?modal=initial` | opens the game settings dialog, or the first-start variant |

States: `session:out` (the public site), `session:signup` (an account without a username), `patch:new-patch`, `patch:full-download`, `patch:launcher-update`, `patch:verifying`, `patch:extracting`, `patch:checksum`, `patch:files`, `patch:finishing`, `patch:done`, `patch:steam-update`, `patch:steam-only`, `patch:error` (the patch panel), `settings:first`, `signup:confirmed`, `account:bans`, `profile:photos` (the own gallery has photos), `start:hold`, `browser:hold` (stay in the progress state of the browser pages).

## Languages

English and French. All texts are .NET resources in `src/Ui/` (`Strings.resx`, English and fallback, and `Strings.fr.resx`), shared with the launcher. The website follows the browser's `Accept-Language` header, `?lang=` overrides it and is remembered in a cookie.

The launcher's selftest checks that the French file has the same keys and the same `{0}` placeholders as the English one. Native speakers are welcome to check `src/Ui/Strings.fr.resx`.
