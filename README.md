# Jellyfin Original Titles

Jellyfin plugin that displays movies and series under their original-language title, per user, in every client.
API spike findings that later work relies on: [docs/spike.md](docs/spike.md).

## Build and test

    dotnet build
    dotnet test

Targets `net10.0`, `Jellyfin.Controller` / `Jellyfin.Model` 12.2.0.

## What it does and what it does not

The plugin has four parts. Each manual check below names the part it needs: **wrapper** (`Display/`), **hints filter** (`Api/`),
**search provider** (`Search/`) and **editor guard** (`Guard/`).

**Wrapper.** The plugin wraps `IDtoService`, so for users who enabled a language it changes the **API-visible** names: the `Name` of movies and series is
replaced by the item's `OriginalTitle` when the item's original language is in that user's list. For episodes and seasons only `SeriesName`
changes, and it is decided from the **series'** own `OriginalTitle` and original language, ignoring the episode's or season's own language.
Every client's browsing, search and detail screens that render names from the API show it. The display layer never writes stored data.

**Hints filter.** `GET /Search/Hints` (the search box) builds its hints without `IDtoService`, so a global MVC action filter (`Api/SearchHintsFilter.cs`)
applies the same decision there: hint `Name` for movies and series, hint `Series` for episodes and seasons. It resolves the user like the
controller does (the `userId` query parameter, else the session's user); an API key without `userId` has no user and is never rewritten.

**Search provider.** `Search/OriginalTitlesSearchProvider.cs` finds movies and series by a romanised (Arabizi or Latin) spelling of their
original title, for users who enabled that language. When it has a romanised hit it also returns the server's own database matches (the search
manager would otherwise drop them); with no romanised hit it returns nothing and search behaves as without the plugin.

Caveats:

- **The wrapper changes only API-visible names.** Sort order stays on the translated title (`SortName`/`CleanName` are not touched), so an
  Arabic-titled movie sorts where its Latin name sorts.
- **Webhook, Playback Reporting and Jellyseerr keep the translated title.** They read the item server-side, not through the wrapped DTO.
- **Dashboard session cards, "now playing" and SyncPlay keep the translated title.** They build their DTOs without a user (ruling R10), and a
  DTO without a user is never rewritten. So "in every client" means every client's browsing, search and detail screens, not these.
- **Meilisearch.** When the installed Meilisearch search plugin returns any results, Jellyfin skips its own SQL search, and on some
  servers it returns junk for Arabic queries on `/Search/Hints`. The plugin's provider is superset-safe (ruling R1: with a romanised hit it also
  returns the SQL matches), but it cannot fix Meilisearch's junk. The remedy, trying `MatchingStrategy=all` and otherwise removing the
  Meilisearch plugin, is a user step.
- **Single-character search keys (ruling R16).** A query whose every romanised key is a SINGLE character is not romanised: `mo`, `mou`, `le`,
  `la`, `ba`, `bab`, `la ba`, and Arabic-script one-consonant words. A 1-character key equals every title word with that key, and a romanised
  hit would outrank the server's genuine prefix matches. Keys of 2+ characters do run, and match an equal title key or a longer one they start with (`shouf` -> `cf` finds `choufli` -> `cfl`) (`mouch` -> `mc`, `7bibi` ->
  `hb`, `dar` -> `dr`). Blind spot this creates: a title whose every key is one character (for example a title that is just `ب`, or `الباب`,
  which transliterates to `albab`, key `b`) cannot be found by any romanised query. The server's own search still finds such a title by its
  translated name and, where core SQL search runs, by the original title. Note that an installed Meilisearch plugin suppresses the
  SQL search for Arabic queries until the Meilisearch step above is done. Accepted trade-off: queries with 2-character keys run and score 90, above SQL's prefix matches (80/75), so stopword-like words keyed to 2
  characters in a non-Arabic enabled language (for example French `les` -> `ls`, `des` -> `ds`, `mon` -> `mn`) can return several same-key titles
  above prefix matches; this is bounded, because the whole query must be covered and a 2-character key matches only an equal title key or one it prefixes (prefix rule, experimental: widens matches, revert `TitleMatcher.WordsMatch`'s prefix branch if it floods results). Revisit
  if seen in use. Seen in use (2026-10-08): the French series "Astérix & Obélix : Le Combat des chefs" (`chefs` -> `cfs`) is returned for
  `shouf` (`cf`, a prefix of `cfs`) next to Choufli Hal; the key drops vowels, so `chouf` and `chef` cannot be told apart. Accepted for now.
  The fix, if it gets annoying: index only original titles that are not in Latin script (`TitleIndex.EntryFor`).
- Episode and season titles are never changed, only `SeriesName`.
- Language codes are two-letter ISO 639-1 (`ar`, `fr`) as stored in `OriginalLanguage`; the settings page trims and lowercases.

### Editor guard and its blind spots

The metadata editor shows the wrapper's output, and `POST /Items/{id}` (v12.2 `ItemUpdateController.UpdateItem`) copies the posted `Name`
into the item, cascades a series' `Name` into every season and episode `SeriesName`, and copies a posted episode/season `SeriesName`. Saving a
displayed item unchanged would therefore store the original title (reproduced on the harness). The editor guard (`Guard/EditorGuardFilter.cs`,
a global MVC action filter) runs **before** that action: when the posted `Name` (movie, series) or `SeriesName` (episode, season) is exactly
what the plugin shows the **saving user** for that item, it puts the stored value back into the request. A request made with an API key has no
user, so then it compares with what **any** configured user is shown. One exception: when the save also sets `OriginalTitle` to the stored
`Name` (an explicit swap, or the repair of an item stored swapped), the posted `Name` is kept, because restoring would store the same title in
both fields. The controller's own write then keeps the real name. The guard itself never writes anything, so it cannot loop. An episode's or
season's own `Name` is never touched. Each kept value is logged at Information (`editor guard kept the stored ...`).

It does **not** cover:

- edits made while the plugin is disabled or uninstalled;
- writers that bypass `POST /Items/{id}` (direct DB edits, scripts or plugins that persist items themselves, metadata refreshes);
- items whose stored `Name` equals the `OriginalTitle` (nothing is displayed differently, nothing to restore), including items whose
  translated `Name` legitimately equals the original title;
- items stored **truly swapped** (`Name` holds the original, `OriginalTitle` the translated title): an unchanged save keeps them swapped,
  and repairing them works only by also swapping `OriginalTitle` back in the same save (the exception above). The guard neither causes nor
  repairs the swap;
- **a false positive**: a user who has the language enabled and deliberately types the original title they are shown into `Name` (or
  `SeriesName`), without swapping `OriginalTitle`, is reverted, because that request looks exactly like the accidental save. With an API key
  this applies when any configured user has the language. To store it on purpose, save from an **administrator** account without that
  language (`UpdateItem` requires elevation; an API-key save compares against every configured user);
- **a swap that also edits the Latin title**: a deliberate swap where the admin ALSO changes the Latin title (the posted `OriginalTitle` then
  differs from the stored `Name`) looks exactly like an `OriginalTitle` typo fix, so the guard restores `Name` and the Arabic title is lost
  from both fields;
- **the stock web UI blanks an episode's stored `SeriesName` on save, with or without this plugin.** Observed on the Jellyfin 12.2.0 web UI
  (harness browser check, reproduced for a user with no plugin entry): saving an episode in the metadata editor posts `SeriesName: ""` (the
  field is only filled for Book items). The guard cannot catch it, because an empty string is not what the plugin displays. The series name is
  re-derived the next time the series itself is saved or refreshed;
- a Jellyfin upgrade that renames the controller or action (the guard then silently stops matching). A renamed `itemId`/`request` argument
  is detected and logged as an Error on every save. Re-run the harness check after each Jellyfin upgrade.

### Known limitations

- **Search keys, Arabizi digits before digraphs.** The romanisation key maps the digits first (`7` to `h`, `5` to `kh`) and merges digraphs
  after, so a digit can fuse with the letter before it: `s7` becomes `sh` (key `c`, the same as `sh`), `t7` becomes `th` (key `t`, the 7 is
  lost) and `k5` becomes `kkh` (key `kx`, the same as `kkh`). Searching with such a spelling can therefore also match titles that contain the
  plain digraph. Pinned by `KeyOf_DigitsRunBeforeDigraphs_SoAnArabiziDigitFusesWithAPrecedingLetter`.

### Switching it off

Clear the user's language list on the plugin page, or uninstall the plugin. The display layer never changes stored data, so there is nothing
to clean up. The plugin never writes stored data itself: the editor guard only puts the stored value back into an incoming save request.

**Recovery if Jellyfin does not start.** The `IDtoService` decorator builds core's `DtoService` the first time `IDtoService` is resolved, so a
break there can stop Jellyfin from starting, and the web UI cannot then uninstall the plugin. Delete the plugin folder and restart: inside the
container `/config/data/plugins/Original Titles_*`, or the matching folder in the config volume on the host.

## Local harness

A throwaway Jellyfin **12.2** server with the plugin loaded as a manual plugin, hot-reloading on every build. No metadata providers are
enabled and no real library server is contacted; everything binds to `127.0.0.1:18096`.

Requirements: Docker with Compose 5.5 (the only version tested; older ones are untested, not known to fail), the .NET 10 SDK, PowerShell (`pwsh` or Windows PowerShell 5.1).
`ffmpeg` on the PATH is optional; without it `seed.ps1` uses the ffmpeg inside the Jellyfin image.

### Start

    dotnet build
    docker compose -f docker-compose.debug.yml watch     # leave running: this is the hot-reload loop
    pwsh scripts/harness/seed.ps1                        # second terminal, once the server is up

`watch` starts the container if needed. On first start the entrypoint waits up to 40 s for the synced build output before Jellyfin boots.
Check that `GET /Plugins` (below) lists `Original Titles` as `Active`.

`seed.ps1` is idempotent. It generates 3 tiny videos under `scripts/harness/media/` (gitignored), completes the startup wizard with the
throwaway admin `harness-admin`, writes the password, API key and user id to `.env.local` (gitignored, never commit it), creates the libraries
`Movies` (`/media/Movies`) and `Shows` (`/media/Shows`) without metadata fetchers, waits for the scan and sets the fixture titles:

| Item | Name | OriginalTitle | OriginalLanguage |
|---|---|---|---|
| Movie | `Mouch Fi Thniti` | `موش في ثنيتي` | `ar` |
| Series | `Harness Show` | `برنامج الاختبار` | `ar` |
| Episode 1 | `Pilot` | none | none (inherits `ar` from the series) |
| Episode 2 | `Harness Show S01E02` (from the file name) | none | none |

`pwsh scripts/harness/items.ps1` lists every item as the API returns it (`-UserId <id>` looks through another user, `-AsJson` gives raw JSON).
Web UI: <http://127.0.0.1:18096> (log in as `harness-admin`, password in `.env.local`).

### Rebuild and hot-reload

Jellyfin cannot load or replace a plugin in a running server, so every change is a **restart**. The `sync+restart` watch rules copy
`Jellyfin.Plugin.OriginalTitles/bin/Debug/net10.0` and `scripts/harness/meta.json` into `/plugin-staging` in the container and restart it; the
entrypoint then copies staging into `/config/data/plugins/Original Titles_0.1.0.0/` *before* Jellyfin starts. Watch never writes into the plugin
directory itself, because overwriting the DLL of a running or stopping Jellyfin corrupts the loaded assembly. The library, users and settings
survive restarts in the `jf-config` volume.

Two ways to trigger a reload:

    dotnet build                                                  # once per change, from the repo root
    cd Jellyfin.Plugin.OriginalTitles && dotnet watch build       # rebuilds on every source save

Run `dotnet watch build` from inside the project folder: with SDK 10.0.103, `dotnet watch --project ... build` fails with `MSB1001`.

If you bump the plugin version, change it in three places: `AssemblyVersion` in the csproj, `version` in `scripts/harness/meta.json`, and
`PLUGIN_DIR` (`Original Titles_<version>`) in `docker-compose.debug.yml`.

### Read the Jellyfin log

    docker compose -f docker-compose.debug.yml logs -f jellyfin
    docker exec jf-original-titles-harness sh -c 'tail -n 200 /config/log/log_*.log'
    docker logs jf-original-titles-harness 2>&1 | grep -E "Loaded (assembly|plugin: Original)"

A `BadImageFormatException` on plugin load means the DLL was replaced while the server ran or stopped: restart once more.

### Verify the features manually

Which check needs which part: the item lists and details, latest/resume/next up, and the stored-data comparison need the **wrapper**; the
`Search/Hints` call needs the **hints filter**; the editor-guard script needs the **editor guard**. The **search provider** has no scripted
harness check yet (unit tests only).

All calls use the API key from `.env.local`. On 12.x the header is `Authorization: MediaBrowser Token="<key>"`. In bash:

    set -a; . ./.env.local; set +a
    H="Authorization: MediaBrowser Token=\"$API_KEY\""
    B=$BASE_URL
    curl -s -H "$H" "$B/Plugins"                       # 'Original Titles' must be Active
    curl -s -H "$H" "$B/Users"                         # user ids
    # a control user whose language list stays empty (random password, kept in .env.local, never printed;
    # `pwsh scripts/harness/editor-guard.ps1` creates the same user the same way)
    CP=$(head -c 18 /dev/urandom | base64 | tr -dc 'A-Za-z0-9')
    echo "CONTROL_PASSWORD=$CP" >> .env.local
    curl -s -X POST -H "$H" -H 'Content-Type: application/json' -d "{\"Name\":\"harness-user\",\"Password\":\"$CP\"}" "$B/Users/New"

Enable `ar` for `harness-admin` only (Dashboard, Plugins, Original Titles, per-user list; the same data is at
`GET`/`POST /Plugins/7c1e5a3d-92b4-4f6e-8d0a-3b5f1c2e9a47/Configuration`). Then run the calls below for the enabled user and for the control.
Expected for the enabled user: `Name` is the Arabic `OriginalTitle` for the movie and the series, `SeriesName` on the season and both
episodes is the Arabic series title, episode `Name` is unchanged. Expected for the control: everything translated, as stored.

    # grid / lists (wrapper)
    curl -s -H "$H" "$B/Users/<uid>/Items?Recursive=true&IncludeItemTypes=Movie,Series&Fields=OriginalTitle,OriginalLanguage"
    # details (wrapper; the episode shows SeriesName)
    curl -s -H "$H" "$B/Users/<uid>/Items/<movieId>"
    curl -s -H "$H" "$B/Users/<uid>/Items/<episodeId>"
    # latest, resume, next up (wrapper)
    curl -s -H "$H" "$B/Users/<uid>/Items/Latest?IncludeItemTypes=Movie,Episode"
    curl -s -H "$H" "$B/UserItems/Resume?userId=<uid>"
    curl -s -H "$H" "$B/Shows/NextUp?userId=<uid>"
    # hints (search box; hints filter)
    curl -s -H "$H" "$B/Search/Hints?searchTerm=Mouch&userId=<uid>"
    # stored data must be unchanged: the same item through the control user (wrapper does not write)
    curl -s -H "$H" "$B/Users/<controlUid>/Items/<movieId>"

Editor guard (needs the **editor guard**): `pwsh scripts/harness/editor-guard.ps1` enables `ar` for `harness-admin` only, posts every displayed fixture item back
unchanged (API key and admin session), reads the stored values through the control user and must end with `FAILS=0`. Run it after
every Jellyfin upgrade: a renamed controller or action silently disables the guard. After an upgrade, first bump the harness image
(`jellyfin/jellyfin:12.2` in `docker-compose.debug.yml`) to the live Jellyfin version, otherwise the script checks the old one.

`GET /Items/{id}` returns 400 without a user, so always use `/Users/{uid}/Items/{id}`. In PowerShell send
`@{Authorization = "MediaBrowser Token=`"$key`""}` and set `[Console]::OutputEncoding = [Text.Encoding]::UTF8`, otherwise Arabic prints as `?`.

### What `POST /Items/{id}` can set (Jellyfin v12.2, verified on the harness)

`ItemUpdateController.UpdateItem` copies the posted `BaseItemDto` onto the item **unconditionally**, so a field missing from the body is written
as null. Always `GET /Users/{uid}/Items/{id}`, change fields, drop `Trickplay` and post the whole object back (as `seed.ps1` does).

| Field | Settable? | Behaviour (measured) |
|---|---|---|
| `Name` | yes | Copied as-is. Omitted or null leaves the item with an **empty** `Name`. |
| `OriginalTitle` | yes | Empty or whitespace-only becomes null; otherwise stored verbatim (Arabic round-trips). |
| `OriginalLanguage` | yes | Normalised by `LocalizationManager`: `ar`, `AR`, `ara`, `Arabic` all become `ar`. Unknown (`xx`) and padded (`" ar "`, stored with the spaces) values are stored raw; empty becomes null. Also settable on episodes and seasons, where it overrides the series; an empty string does not fall back to the series (see `docs/spike.md`). |
| `SeriesName` (episode, season) | derived, do not set | Renaming a series through `Name` cascades to its seasons and episodes. |

The values survived `POST /Library/Refresh` on the harness, which has no metadata fetchers. A real provider (TMDb) refresh may overwrite
`Name`, `OriginalTitle` and `OriginalLanguage`; that is not exercised here.

Gotchas: posting a fetched item back can return 500 because of the `Trickplay` key. It did not reproduce on the fixtures (no trickplay data), but
`seed.ps1` drops the key anyway. `GET /Items/{id}` without a user is a reproduced 400.

### Stop and clean up

    docker compose -f docker-compose.debug.yml down -v     # also deletes the config and cache volumes: users, libraries and the API key are gone
    rm .env.local                                          # its credentials are now stale

`docker-compose.debug.yml` and `scripts/harness/*` stay. `scripts/harness/media/` (generated videos) is gitignored and can be deleted.

### Differences from other Jellyfin images

The harness uses the official `jellyfin/jellyfin:12.2` image, which reports `Jellyfin.Server 12.2.0.0` (no `12.2.0` tag exists; `12.2` floats to
the newest 12.2 build). `linuxserver/jellyfin` is not used here. The plugin path is the same, `/config/data/plugins/`, because the harness sets
`JELLYFIN_DATA_DIR=/config/data` (the official image defaults to `/config/plugins`).
