# SQLite for profiles, on the community server and the local server

Status: accepted

The community server and the local server store profiles in SQLite: one database file per server, the same schema and the same migrations for both. The local server runs on a player's own PC, where nothing should need installing or running beside it; a SQLite file needs neither. The community server runs as one service on one VPS (see [deploy.md](../deploy.md)) and is sized for up to about 500 players at once, which one SQLite file in WAL mode on a local disk carries: readers never wait for the writer, and the writes of a hunt are small. One database for both keeps one code path for storing a restored profile, and anyone can run the community server's code without a database server ([GLOSSARY.md](../../GLOSSARY.md): "anyone can run the same code").

## Considered options

- **PostgreSQL for the community server, SQLite for the local server.** PostgreSQL takes many writers at once and has `jsonb`. Rejected: two providers means two SQL dialects, two sets of migrations and tests against both, for a load that one SQLite file carries; it also adds a database container, a password secret and major-version upgrades to the VPS.
- **PostgreSQL for both.** One database everywhere, but every player who runs a local server would have to install and run PostgreSQL on Windows. Rejected.
- **The website opens the same file, through a volume shared by both containers.** It works on one host, but two processes then write the file and both must agree on migrations and deploy in step. Rejected: the website goes through the community server's API, which the launcher needs anyway.

## Consequences

- SQLite allows one writer at a time and one machine: the community server is one process on one VPS, and the database file sits on a local disk in a Docker volume, never on a network share.
- In `deploy/compose.yml` the database is no service of its own: the community server's service mounts a named volume at `/data` and opens the file there. The volume outlives the container, so a deploy keeps the data; Compose stops the old container before it starts the new one, so two servers never write at once. If the server's image runs as the non-root `$APP_UID` like `src/Web/Dockerfile`, its Dockerfile must create `/data` owned by that user (an empty named volume takes the owner of the folder it is mounted over); otherwise the first write fails.
- Only the community server opens the file. The website reads and changes profiles through the community server's HTTP API, the contracts in `src/Services/Contracts/` that the launcher also uses.
- A copy of the running database is made with SQLite's backup (`sqlite3 <file> ".backup <copy>"` or `VACUUM INTO`), not by copying the file, which can miss what is still in the `-wal` file.
- The parts of a profile kept as raw JSON (`Extras`, the trophy and lodge rows, item metadata, user stats, and achievement values and triggers of `PlayerModel`) go in text columns; SQLite's JSON functions read them when needed.
- Tests can use an in-memory SQLite database, so they still need no network and no service.
- Revisit, with a new ADR that supersedes this one, when the community server needs more than about 500 players at once, more than one server process, or when writers wait on the database.
- The library used to reach SQLite (and its entry in `THIRD-PARTY-NOTICES.md`) is chosen with the storage layer, not here.
