# Third-party notices

Bundled in `src/Ui/wwwroot`, with their licence texts next to the files. `shell.js` and `theme.css` in the same folder are this project's own files.

| What | Files | Version | Licence |
|---|---|---|---|
| Roboto (latin 300/400/500, math 400 for ∞) | `fonts/roboto-*.woff2`, `fonts/roboto-LICENSE.txt` | `@fontsource/roboto` 5.3.0 | SIL Open Font License 1.1 |
| Open Sans (latin, 400/700) | `fonts/open-sans-*.woff2`, `fonts/open-sans-LICENSE.txt` | `@fontsource/open-sans` 5.3.0 | SIL Open Font License 1.1 |
| Font Awesome Free icons (a subset as SVG symbols) | `icons.svg`, `icons-LICENSE.txt` | 6.7.2 | Creative Commons Attribution 4.0 (icons) |
| Flags (language list, server list) | `flags/*.svg`, `flags/LICENSE.txt` | `flag-icons` 7.6.0 | MIT |

Bundled in the backup tool's exe (`tools/backup`):

| What | Version | Licence |
|---|---|---|
| [WPF-UI](https://github.com/lepoco/wpfui) (NuGet `WPF-UI`) | 4.3.0 | MIT, copyright 2021-2025 Leszek Pomianowski and WPF UI Contributors |

Bundled in the launcher (`src/Launcher`):

| What | Version | Licence |
|---|---|---|
| [Velopack](https://github.com/velopack/velopack) (NuGet `Velopack`; the Setup and Update programs inside the release packages are Velopack's too) | 1.2.161 | MIT, copyright 2021 Caelan Sayler, 2024 Velopack Ltd. |

Bundled with the server (`server/Server`; NuGet packages, direct and transitive; checked against the package metadata, and all are permissive licences that the GNU AGPL-3.0 can include):

| What | Version | Licence |
|---|---|---|
| `Microsoft.AspNetCore.OpenApi`, `Microsoft.Data.Sqlite` (with `.Core`), `Microsoft.OpenApi` | 10.0.12, 10.0.12, 2.12.0 | MIT |
| `Swashbuckle.AspNetCore.SwaggerUI` (embeds the Swagger UI files that the server's admin pages serve) | 10.3.0 | MIT; Swagger UI: Apache-2.0 |
| `SQLitePCLRaw.bundle_e_sqlite3` (with `.core`, `.provider.e_sqlite3`, `.lib.e_sqlite3`; the native SQLite library) | 2.1.12 | Apache-2.0, copyright 2014-2024 SourceGear, LLC; SQLite itself is in the public domain |

The server's tests also use `xunit.v3` (Apache-2.0) and `Microsoft.NET.Test.Sdk` (MIT); they are not shipped.

Font Awesome Free 6.7.2 by [@fontawesome](https://fontawesome.com), copyright 2024 Fonticons, Inc.; the icons are licensed under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); they were cut out of the SVG files unchanged except for being merged into one sprite.
