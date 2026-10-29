# Restore library

`src/Restore` (`Resurgence.Restore`) reads a backup zip made by any version of the [backup tool](backup.md) and returns a neutral player model and a report. It has no database and no server types: storing the model is a later layer. It also holds `BackupZip`, the quick check the launcher uses.

## Run it

Until the server has its `import backup` command, a stub prints the report and writes the model as JSON:

```
dotnet run --project src/Restore/Cli -- path\to\backup.zip [model.json] [--catalogue path\to\Application_application__lang=en.json]
```

Without a second argument the model goes to `<zip name>.model.json` beside the zip. `--catalogue` takes the server's own copy of the game's item catalogue (an `Application/application` reply; never the one inside the zip being read) so the units rule is exact; without it the rule is `zero-only`. Exit code 0 = read, 1 = refused or unreadable (the message says why: `refused: <reason>` for a zip the reader refuses, `refused: <exception type>: <message>` for any other fault, such as a bad catalogue, never a crash), 2 = wrong arguments. In code: `Importer.Read(path or stream, catalogue, limits)` returns `Restored(Model, Report)` or throws `RestoreException`; `ApplicationCatalogue.Read(stream)` is the catalogue.

## What is refused, what is rejected

The zip is read once, in memory; nothing is extracted. The whole zip is **refused** when it breaks a cap (`Limits`: 20,000 entries, 1 GiB of content, 64 MiB per entry, 32 MiB for the manifest, 128 MiB of JSON in total, `manifest.json` included, and 32 MiB per JSON entry (a real backup holds under 4 MiB of JSON), JSON nesting 64, more than 100:1 compression for an entry of 1 MiB or more), has an unsafe entry name (`..`, a rooted path, a drive letter, a backslash), has no `manifest.json` at the root or one folder down, a manifest that does not parse, a `uid` that is not digits only or too large for a 64-bit id, no `claim.key` or one that does not hash to `claim_hash`, a `format` newer than 2, or an identity reply whose `id` is not the manifest `uid`. A single file is **rejected** (and reported) when its SHA-256 differs from the manifest or it is not valid JSON; a file the manifest does not list is rejected too. Manifest entries with an `error` or no `sha256` are missing data, listed under `failed`, not a fault of the zip.

## Version and mode

No `format` in the manifest = format 1 (every release up to v0.3.0-rc.1); `format: 2` adds `mode`, `identity`, a per-file `date` and `checks`. `tool_version` is copied, never compared. The mode comes from the files: `Me_me.json` present = my account, otherwise public (by handle when the `Public_user/getByHostname` reply is there, else by id). A manifest `mode` that disagrees gives a warning and the files win.

## Field rules

- **Items**: `Inventory/list` wins over `Inventory/dict` for a row both hold (the dict can lag a change); a difference in `metadata`, `units` or `dir` is a warning. `Units` is the saved value only when the item catalogue the caller passes (`IItemCatalogue.HasUnits`) says the item has units, otherwise null. Without a catalogue: 0 reads as null, every other value is kept, and the report says `zero-only`.
- **Positions**: `round(value * 1000)` per axis, from the number's decimal text (never a double), half away from zero. A coordinate beyond ±2,000,000 (or not a number) means no location, with a warning. A placed row (slot 23) without a position is a warning and goes to `Extras`, not to `Items`; a position without a direction keeps the direction null, with a warning. `metadata` is kept as sent (a feeder's `metadata.reserve` is never renumbered, integers stay integers).
- **Skills** as sent (level plus progress); `SkillSet.Floor()` gives the levels. **Wallet** from `Me/me` (`ems`, `gms`), own zips only. **Statistics**: the four `data` objects, `distance`, `duration` and the two minigame scores. **Achievements** one to one, with their `triggers` (earn times). **Trophies and lodges** as raw rows; a harvest reply with status 404 means none.
- **Account defaults** for the fields of the game's login reply that no web reply holds. **Membership**: `freeplay` 0, `staff` 2, `premium` 1 and `freebeta` 0 (these two inferred, flagged in the report), anything else 0 with a warning.
- Every other reply (hunts, messages, friends, missions, competitions and the rest) is kept whole in `Extras` and counted under `unsupported`, one line per endpoint with the ids in its path shown as `*` (`/game/user/*/ranks`); "no entry" answers (status 404, such as a leaderboard the player is not on) are counted on one line of their own. Derived files (`map-setups.json`, `messages-index.json`, `competition-history.json`) are checked but not used: the raw replies decide.

## Report

`mode`, `complete` (`yes` = `completed_utc` present, `no`, `unknown` for a format 1 zip without it), the units rule used, and the lists `restored`, `unsupported`, `missing` (for example the wallet of a public zip, or the hunts `checks.hunts` says were saved short or whose list did not load completely, `list_failed`), `warnings`, `failed` and `rejected`. The same zip always gives the same model and report (items by item id, then guid).
