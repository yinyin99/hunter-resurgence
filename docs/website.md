# Website

`src/Web/` is the community website. It is a Blazor Web App that renders every page on the server as plain HTML (static server rendering). Nothing on it needs JavaScript. The shared pages and the theme live in `src/Ui/`, the same library the launcher uses.

## Build and run

```
dotnet build hunter-resurgence.slnx -c Release
dotnet run --project src/Web
```

The development profile listens on `http://localhost:5080`.

## Pages

| Page | Route |
|---|---|
| Home: pitch, top hunters, community servers, news | `/` |
| Leaderboards, with species, weapon class and season filters | `/leaderboards` |
| Player search | `/players?q=` |
| Player overview, trophies, hunt history | `/players/{handle}`, `/players/{handle}/trophies`, `/players/{handle}/hunts` |
| One hunt with its kills, one kill's score sheet | `/hunts/{id}`, `/kills/{id}` |
| Download the launcher, start it from Steam, link to the backup tool | `/download` |
| Sign in (email, then code) | `/sign-in` |

Filters and search are plain GET forms, so they work without scripts. Sign-in is a two-step POST form with an antiforgery token. It stores and sends nothing: any email gives the code step, and any six digits show "Accounts open when the community server is live".

## A smaller surface than the launcher

Nobody is signed in on the website. These exist only in the [launcher](launcher.md): Play and Hunt now, the server picker, Game settings, Launcher settings, the store, missions, friends and Restore backup.

## Sample data

Every number, player, hunt and news item is fictional sample data from `src/Ui/SampleData*.cs` until the community server exists. The download button is disabled ("Not released yet"): there is no release yet.

## Unknown pages

An unknown player, hunt, kill or address answers with HTTP 404 and the "Page not found" page. The launcher shows the same message inside its window.

## Languages

All texts are .NET resources in `src/Ui/Strings.resx` (English, the fallback), with one `Strings.<culture>.resx` per added language. The website follows the browser's `Accept-Language` header.
