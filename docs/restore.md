# Restore library

`src/Restore` (`Resurgence.Restore`) reads a backup zip made by any version of the [backup tool](backup.md) and returns a neutral player model and a report. It has no database and no server types: storing the model is a later layer, in SQLite ([ADR 0001](adr/0001-sqlite-for-profiles.md)). It also holds `BackupZip`, a smaller check of a backup zip (see [BackupZip](#backupzip)).

## Run it

Until the server has its `import backup` command, a stub prints the report and writes the model as JSON:

```
dotnet run --project src/Restore/Cli -- path\to\backup.zip [model.json] [--catalogue path\to\Application_application__lang=en.json]
```

Without a second argument the model goes to `<backup zip name>.model.json` beside the backup zip. `--catalogue` takes the server's own copy of the game's item catalogue (an `Application/application` reply; never the one inside the backup zip being read) so the units rule is exact; without it the rule is `zero-only`. Exit code 0 = read, 1 = refused or unreadable (the message says why: `refused: <reason>` for a backup zip the reader refuses, `cannot read: <message>` when a file cannot be read (an `IOException`: the backup zip or the catalogue is missing or locked), `refused: <exception type>: <message>` for any other fault, such as a bad catalogue, never a crash), 2 = wrong arguments. In code: `Importer.Read(path or stream, catalogue, limits)` returns `Restored(Model, Report)` or throws `RestoreException`; `ApplicationCatalogue.Read(stream)` is the catalogue.

## What is refused, what is rejected

The backup zip is read once, in memory; nothing is extracted. The whole backup zip is **refused** when it breaks a cap (`Limits`: 20,000 entries, 1 GiB of content, 64 MiB per entry, 32 MiB for the manifest, 128 MiB of JSON in total, `manifest.json` included, and 32 MiB per JSON entry (a real backup holds under 4 MiB of JSON), JSON nesting 64, more than 100:1 compression for an entry of 1 MiB or more), has an unsafe entry name (`..`, a rooted path, a drive letter, a backslash), has no `manifest.json` at the root or one folder down, a manifest that does not parse, a `uid` that is not digits only or too large for a 64-bit id, no `claim.key` or one that does not hash to `claim_hash`, a `format` newer than 2, or an identity reply whose `id` is not the manifest `uid`. A single file is **rejected** (and reported) when its SHA-256 differs from the manifest or it is not valid JSON; a file the manifest does not list is rejected too. Manifest entries with an `error` or no `sha256` are missing data, listed under `failed`, not a fault of the backup zip.

## BackupZip

`BackupZip.Read(path)` is a separate, smaller check: it opens a backup zip, reads `manifest.json` and `claim.key` and returns a `BackupResult`: either `Info` (`Uid`, `Handle`, `ToolVersion`, `StartedUtc`, `Groups`, `SteamId64`, the file and failed counts, and the same per endpoint) or one `BackupError`; a bad file never throws. It reads nothing else and sends nothing. Its caps are its own, not `Limits`: a file over `MaxZipBytes` (512 MiB) is `TooBig`, a manifest over `MaxManifestBytes` (64 MiB) is `BadManifest`, a `claim.key` over 1 KiB is `ClaimMismatch`. It does not check entry names, entry counts, compression or file hashes.

| `BackupError` | When |
|---|---|
| `TooBig` | the file is over 512 MiB |
| `Unreadable` | an I/O or access error |
| `NotZip` | not a zip |
| `NoManifest` | no `manifest.json` at the root or one folder down |
| `BadManifest` | it does not parse, is over 64 MiB, or has no `uid` or `claim_hash` |
| `NoClaimKey` | no `claim.key` beside the manifest |
| `ClaimMismatch` | the SHA-256 of `claim.key` is not `claim_hash` (or the key is over 1 KiB) |

The launcher does not call it yet; only the tests do.

## Version and mode

No `format` in the manifest = format 1 (defined in [backup.md](backup.md#what-is-saved)); `format: 2` adds `mode`, `identity`, a per-file `date` and `checks`. `tool_version` is copied, never compared. The mode comes from the files: `Me_me.json` present = my account, otherwise public (by handle when the `Public_user/getByHostname` reply is there, else by id). A manifest `mode` that disagrees gives a warning and the files win.

## Field rules

- **Items**: `Inventory/list` wins over `Inventory/dict` for a row both hold (the dict can lag a change); a difference in `metadata`, `units` or `dir` is a warning. `Units` is the saved value only when the item catalogue the caller passes (`IItemCatalogue.HasUnits`) says the item has units (a count, as climbing gear has), otherwise null (a placeable has none, whatever number the dict holds, 0 or 1). Without a catalogue: 0 reads as null, every other value is kept, and the report says `zero-only`.
- **Positions**: `round(value * 1000)` per axis, from the number's decimal text (never a double), half away from zero. A coordinate beyond ±2,000,000 (or not a number) means no location, with a warning. A placed row (slot 23) without a position is a warning and goes to `Extras`, not to `Items`; a position without a direction keeps the direction null, with a warning. `metadata` is kept as sent (a feeder's `metadata.reserve` is never renumbered, integers stay integers).
- **Skills** as sent (level plus progress); `SkillSet.Floor()` gives the levels. **Wallet** from `Me/me` (`ems`, `gms`), own backup zips only. **Statistics**: the four `data` objects, `distance`, `duration` and the two minigame scores. **Achievements** one to one, with their `triggers` (earn times). **Trophies and lodges** as raw rows; a harvest reply with status 404 means none. So does one without a status whose body is not a harvest record (a JSON object with at least one member and none of `message`, `error`, `errorCode`). A backup run killed before it wrote its manifest leaves a 404 that way. Such a file is kept in `Extras`, with the warning `<n> harvest file(s) without a status hold no harvest record: not restored as trophies, kept in Extras`.
- **Account defaults** for the fields of the game's login reply that no web reply holds. **Membership**: `freeplay` 0, `staff` 2, `premium` 1 and `freebeta` 0 (these two inferred, flagged in the report), anything else 0 with a warning.
- **Shapes**: where an object is expected (the manifest's `checks`, a `Trophy/list` or `Page_content/minigames_list` reply, its `position`, an item's `location`), an empty `[]` reads as empty (this API sends `[]` for an empty map) and any other non-object is not read, with the warning `<what> is not an object: not read`; the rest of the reply is still read. An `Inventory/list` element that is not an object (other than `null` or `[]`), and the `own` rows of an element whose `item` is not an object, go to `Extras` as inventory rows not read.
- Every other reply (hunts, messages, friends, missions, competitions and the rest) is kept whole in `Extras` and counted under `unsupported`, one line per endpoint with the ids in its path shown as `*` (`/game/user/*/ranks`); "no entry" answers (status 404, such as a leaderboard the player is not on) are counted on one line of their own. Derived files (`map-setups.json`, `messages-index.json`, `competition-history.json`) are checked but not used: the raw replies decide.

## Report

`mode`, `complete` (`yes` = `completed_utc` present, `no`, `unknown` for a format 1 backup zip without it), the units rule used, and the lists `restored`, `unsupported`, `missing` (for example the wallet of a public backup zip, or the hunts `checks.hunts` says were saved short or whose list did not load completely, `list_failed`), `warnings`, `failed` and `rejected`. The same backup zip always gives the same model and report (items by item id, then guid).
