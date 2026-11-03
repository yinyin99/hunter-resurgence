# Player kit

`tools/play/` is a small Windows tool that lets the game reach the community server from your own PC, with your own copy of the game. It has two steps: a one-time `setup` that points the game's server names at your PC, and `run`, a helper that answers the game on your PC and passes every request on, unchanged, to the community server over the internet. The game's files are never changed.

While the kit is installed, the game talks only to the community server; `3-remove.cmd` gives the official servers back. Make your backup with the [backup tool](backup.md) **before** `setup`: it needs the official servers.

## For players

1. Download `play-win-x64.zip` from the latest release and unpack it. Keep every file in the same folder: `play.exe`, `settings.json` and the three `.cmd` files. The files are not code-signed, so Windows may warn that it does not know the publisher: choose More info, then Run anyway. To make sure your download is the real one, see [checking the download](backup.md#checking-the-download).
2. Double-click `1-setup.cmd`. It lists what it will change, then Windows asks you twice:
   - "Do you want to install this certificate?": choose **Yes**. This is the certificate authority that only your PC knows ([what it is](#what-setup-changes)).
   - A permission window for `play.exe` (administrator): choose **Yes**. Only the edit of the hosts file runs as administrator.

   You do this once.
3. Double-click `2-run.cmd` and **leave its window open** while you play. Then start the game as you always do.
4. The first time the game starts, **Windows Firewall asks about the game: click Allow**. Without it the game cannot hunt.
5. When you stop playing, close the `2-run.cmd` window. Next time, only step 3 is needed.
6. To undo everything, double-click `3-remove.cmd`. Windows asks once to remove the certificate authority: choose Yes. After that the game talks to its own servers again (if they still exist), and you can delete the folder.

If something does not work, see [Problems](#problems).

## Commands

```
play setup            one-time setup
play setup --remove    undoes everything setup changed
play run               answers the game and forwards to the community server
```

The `.cmd` files are `play setup`, `play run` and `play setup --remove` followed by a pause, so their window stays open. `play hosts on|off` exists only because `setup` starts it through the administrator prompt; it edits the hosts file and nothing else.

## What setup changes

`setup` prints this before it starts. It changes three things on your PC and nothing else:

1. **The Windows hosts file** (`C:\Windows\System32\drivers\etc\hosts`): the game's server names (listed in `settings.json`) point to your PC (`127.0.0.1`), between two marked lines (`# >>> hunter-resurgence play` and `# <<< hunter-resurgence play`). Your other lines are not touched. `--remove` takes the block out again and leaves the file byte for byte as it was.
2. **Your Windows trust list** (the current user's only, not the whole PC): a local certificate authority named `Hunter Resurgence local CA`. It is made on your PC, valid for two years, and **name-constrained**: Windows accepts a certificate it signed only for the game's server names, never for any other site. Its private key is created in memory, used once to sign the next item and then thrown away: it is never saved, so nobody can export or reuse it.
3. **A certificate for those names**, signed by that authority. Its public part is the file `%LOCALAPPDATA%\hunter-resurgence-play\leaf.cer`; its private key is a non-exportable key in your Windows key store. `run` uses it to answer the game over HTTPS.

Running `setup` again replaces all of it (and asks again). If `run` says the certificate has expired, run `setup` again.

The administrator window: `setup` itself runs as you, so the certificate lands in your own trust list. Only when it needs to write the hosts file does it start a second copy of `play.exe` through the Windows permission window; that copy edits the file and exits. If you run `setup` from an administrator command prompt, no window appears. `run` never needs administrator rights.

## How run works

`run` listens on `127.0.0.1` port 443 (HTTPS, with the certificate above) and port 80 (plain HTTP: the game's built-in browser sends some requests that way). Both speak HTTP/1.1 only. Each request is relayed unchanged (method, path and query exactly as sent, headers, body) over normal HTTPS to the community server at `server_url`, and the reply is relayed back with its length in `Content-Length` (never chunked, the game cannot read that) and without `Alt-Svc`, otherwise unchanged. The name the game asked for travels in the header `X-Game-Host`, because the community server sits behind a proxy that hides it; the server reads it only from that proxy ([docs/server.md](server.md#behind-a-reverse-proxy)). Redirects, cookies and compression are not touched. A request that cannot reach the server gets a 502 and a line in the window.

The kit sends everything the game sends, including your sign-in tokens, to the one address in `settings.json` and nowhere else, and keeps no log or copy of it.

## settings.json

`settings.json` sits next to `play.exe` and is read at every start. The repository only has `tools/play/settings.example.json` with placeholders ([CONTRIBUTING.md](../CONTRIBUTING.md#rules)); the release zip carries the real one, in plain text.

| Key | Meaning |
|---|---|
| `server_url` | The community server's `https://` address, without a query. The one setting that changes the server |
| every `*_host` key | The game's server names: the current build needs `game_web_host` (everything it sends) and `crash_host` (crash reports); the older `*_host` keys of the server's `settings.json` ([server.md](server.md#run-it)) are only for the old build. Never `web_host`: that is the community server's own website. They go into the hosts block and into the certificate. Only plain host names are accepted; a name starting with `www.` also redirects the bare name |

The host list is data: a new name needs a new `settings.json`, not a new build.

## Problems

- **"Could not listen on port 80 or 443"**: another program uses the port (a web server, Skype, a local server of this project, a second copy of `run`). Close it and run again.
- **The game still reaches the official servers**: `run` says so when the hosts block is missing. Run `setup`. Some antivirus programs block edits of the hosts file; allow it or turn the protection off for the edit.
- **The game cannot connect**: check that the `2-run.cmd` window is open and that you clicked Allow in the firewall prompt (Windows Defender Firewall, "Allow an app through the firewall", the game's exe).
- **The administrator window does not appear or you said No**: `setup` stops with a message; run it again. The certificate may already be installed: `3-remove.cmd` undoes whatever was done.

## Languages

The messages are in English, French, Spanish, Portuguese (Brazil), German or Russian, following the Windows display language, otherwise English. The texts are standard .NET resources in `tools/play/`: `Strings.resx` (English, the fallback) and one `Strings.<culture>.resx` per language. `TranslationsTests` checks that every file has the same keys and `{0}` placeholders. Error messages from Windows itself are in your Windows language.

## Build and test

```
dotnet build tools/play -c Release
dotnet test tests/Resurgence.Tests -c Release
tools\play\publish.cmd <path\to\your\settings.json> [version]
```

`publish.cmd` publishes a self-contained single-file `play.exe` (the project uses the web server library, so the exe is large), copies your `settings.json` and the three `.cmd` files from `tools/play/cmd/` beside it and writes `tools\play\bin\Release\net10.0-windows\win-x64\play-win-x64.zip`. CI does this with the example settings and checks the zip's content; the release workflow builds, attests and uploads it with the backup tool ([CONTRIBUTING.md](../CONTRIBUTING.md#releases)).

The tests (`tests/Resurgence.Tests/PlayKit`) use temporary files and servers on `127.0.0.1` only: the hosts block round trip on a temporary file (byte for byte), the name constraints of the authority (the extension, and that Windows refuses another name signed by the same authority), the leaf's key being non-exportable, the relay (method, raw target, query, body, headers, `X-Game-Host` set, `Host` dropped, the reply unchanged except for its length and `Alt-Svc`, a chunked body, `HEAD`, a dead server) against a local test server, the HTTPS answer with a leaf whose key is in the key store (the test makes that key and deletes it again), the settings rules and the translations. Nothing edits the real hosts file or trust list; `setup`, the administrator window and the game itself need a real PC.
