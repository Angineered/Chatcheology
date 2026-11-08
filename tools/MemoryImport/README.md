# MemoryImport

Building the PSN shared-activity file from the two accounts' earned-trophy timestamps and comparing
it with an earlier one (CH-71), dry run and import of that file (CH-42) into the memories file
(`memories.db`, beside the main workspace), enrichment with trophy names and icons from a trophy
definitions file (CH-43), and download of the game images and trophy icons into
`memories-media\` beside it. The main workspace is never opened. Input files are only read.

## Build

```
dotnet build tools\MemoryImport\MemoryImport.csproj
```

Run with `dotnet run --project tools\MemoryImport\MemoryImport.csproj -- <arguments>`.

## Input

Schema `chatcheology-psn-shared-activity-v1` only; any other schema is refused. The reader is
strict: it accepts only the shape below and stops with the game or event index on anything else
(missing field, wrong type, alternate field name). Field names match case-insensitively. Of the root
fields only `timezone`, `games` and `events` are used; `sources`, `rules`, `summary` and
`yearlyGameDateCounts` are ignored.

`<a>`/`<b>` stand for the two account prefixes. `--local <prefix>` and `--other <prefix>` map them
to the chat's Local and Other participant roles (letters and digits only, not the same).
`--pair-a <prefix>` names which of the two is side a of the matching pairs: the account whose trophy
set is first in each game `key`. Prefixes are never printed.

- Game (fields used): `key` (`<setA>|<setB>`), `game` (the title), `icon` (URL or null),
  `<a>TrophySet`, `<b>TrophySet`. The key must be the side-a account's set then the other's; where
  the two sets differ this proves `--pair-a`, and a mismatch stops the run.
- Event (fields used): `id` (`<gameKey>|yyyy-MM-dd`, the idempotency key), `gameKey` (must equal the
  first two parts of `id`; links to the game), `date` (local date, must equal the last part of
  `id`), `quality` (copied as the day level), `<a>Trophies`, `<b>Trophies`, `matchingTrophies`.
  The event's own `title` is not used; the title comes from the game.
- Trophy: `id` (integer), `type`, `time` (`HH:mm:ss` local, on the event's date),
  `utc` (`yyyy-MM-ddTHH:mm:ssZ`). The UTC time converted to the file's IANA `timezone` must equal
  the event date plus `time`; a mismatch stops the run with the event index.
- Matching pair: `id` (integer), `aTime`, `bTime` (`HH:mm:ss`). Side a is the `--pair-a` account.
  A pair is located by (role, trophy id, event date + time). A pair not located, or whose two
  trophies fall in different periods, is counted and reported, never guessed. `type` and `seconds`
  are not used: "seconds apart" is taken from the two located trophies' UTC times.

## Periods and labels

Both accounts' trophies sorted by UTC; a new period starts when the gap to the previous trophy
exceeds `--gap` minutes (default 60; exactly 60 minutes still chains). Labels, from the event's own
`matchingTrophies` pairs inside the period only:

| Label | When |
|---|---|
| strong | holds a matching pair at most 60 s apart |
| supporting | holds a matching pair, none within 60 s |
| same-period | both accounts earned trophies in it, no matching pair |
| one-account | only one account earned trophies in it |

The day level (`quality`) is stored as-is and never recomputed.

## Commands

`psn-dry-run` (the default; writes nothing):

```
[psn-dry-run] <psn-file> --local <prefix> --other <prefix> --pair-a <prefix> [--gap <minutes>]
```

Prints games, records, items, periods per label, timeline cards (periods with both accounts),
records with no shared period, matching pairs not located / split, records per year, first and
last date.

`psn-import` (one transaction):

```
psn-import <psn-file> --workspace-folder <folder> --local <prefix> --other <prefix> --pair-a <prefix>
           [--collection <name>] [--gap <minutes>] [--create]
```

- `--workspace-folder`: the main workspace's folder; the file used is `memories.db` in it.
  `--create` creates it when it does not exist yet (never over an existing file).
- `--collection`: the Imported collection's name (default `Shared gaming`; shown by default: no).
- Idempotent: an event already stored (same `id` in the collection) is updated in place only when
  something differs; a re-import of an identical file reports 0 changes. Entries no longer in the
  file are counted, never deleted.

`psn-enrich-dry-run` (writes nothing; the file is opened read-only) and `psn-enrich` (one
transaction):

```
psn-enrich-dry-run --workspace-folder <folder> <definitions-file>
psn-enrich         --workspace-folder <folder> <definitions-file>
```

Stores trophy definitions in the memories file's `ItemDefinition` table, keyed by (set, trophy ID).
Entry items are not changed: viewers and the archive join `EntryItem` to `ItemDefinition` on
(`SetId`, `ItemId`). `EntryItem.Name` and `EntryItem.IconSha256` (from step 1) are unused and stay
null. The memories file must exist (run `psn-import` first).

- Input: format `chatcheology-psn-trophy-definitions-v1` only. Root: `format`, `fetchedUtc` (text,
  not used), `sets`. Set: `npCommunicationId` (non-empty text), `npServiceName` and
  `trophySetVersion` (text or null, not used), `trophies`. Trophy: `trophyId` (whole number, 0 or
  more), `trophyType` (non-empty text), `trophyName`, `trophyDetail`, `trophyIconUrl`,
  `trophyGroupId` (text or null), `trophyHidden` (true, false or null). Every field must be
  present; null only where stated. A missing field, a wrong type, a repeated set or a repeated
  (set, trophy) stops the run with the set and trophy index. Other fields are ignored; field names
  match case-insensitively.
- Values are stored as written (no trimming, no rewording; null stays null).
- A definition whose type differs from an entry item's type for the same (set, trophy) is not
  stored, and is counted (types compared ignoring case only). A stored definition that now
  mismatches is removed and counted. Definitions no entry item uses are stored too.
- Idempotent: a definition identical to the stored one is unchanged; one that differs is updated in
  place (its stored icon is kept while the icon URL is unchanged). A re-run of the same file reports
  0 changes. Stored definitions missing from the file are kept.
- Memories file format: step 1 files are format 1. `psn-enrich` upgrades a format 1 file to format 2
  in the same transaction (adds `ItemDefinition`; every existing row unchanged) and says so;
  the dry run says it would. Every other command reads and writes both formats.

Prints: sets, definitions, hidden, entry items with a definition / without one / whose definition
has another type, definitions not stored (type mismatch), added / updated / unchanged, stored
definitions removed (type mismatch), changes.

`fetch-images`:

```
fetch-images --workspace-folder <folder> [--max-bytes <n>]
```

Downloads each distinct entry image URL, and each distinct trophy icon URL of a definition used by
at least one entry item, whose image is not stored yet: HTTPS only, hosts
`image.api.playstation.com` and `psnobj.prod.dl.playstation.net` only, no redirects followed, a
30-second deadline per image, size cap (default 5 MB), PNG/JPEG/GIF/WebP only (detected from the
bytes). Saved as `memories-media\<sha256>.<ext>`; identical images are stored once; an existing file
is kept only when its own hash matches. Prints fetched / already present / failed separately for
game images and trophy icons (0 trophy icons on a format 1 file, which is not upgraded here). Exit
code 1 if any failed.

`psn-build` (CH-71 D8; replaces the hand-made step that produced the shared-activity file):

```
psn-build --timestamps <file> [--timestamps <file> ...] --manifest <file>
          --account-a <name> --account-b <name>
          (--out <file> [--overwrite] | --dry-run) [--icons-from <shared-activity-file>] [--allow-no-icon]
```

- `--timestamps`: the fetch script's output (`.temp\psn\Fetch-Shared-PSN-Trophies.ps1`), format
  `chatcheology-psn-trophy-batch-v1` only. Repeat it to combine several files; a game key in two
  files stops the run. Root: `format`, `timestampsAreUTC` (true if present), `games`. Game: `key`,
  then one object per account named by the account name, with `npCommunicationId`, `trophies` and
  (if present) `earnedCount`, which must equal the trophies listed. Trophy: `trophyId` (whole number),
  `trophyType` (text), `earnedDateTimeUTC` (`yyyy-MM-ddTHH:mm:ssZ` exactly). Other fields (the
  game's title here, `npServiceName`, `totalTrophies`, `sourceAccounts`, `note`) are not used.
- `--manifest`: the hand-kept game list, the same shape the fetch script reads
  (`PSN-Shared-Trophy-Batch-*.json`). Root: `games`. Game: `key`, `game` (the title written to the
  file), one object per account with `npCommunicationId`, optional `sameTrophySet` (must agree with
  the two sets), optional `icon` (URL or null). Only manifest games are built; a manifest game with
  no timestamps, and a timestamp game not in the manifest, are skipped and counted.
- `--account-a` / `--account-b`: the account field names in both inputs (letters and digits). Account
  a is side a: its set must be first in every `key` (`<setA>|<setB>`), otherwise the run stops. The
  names are also the account prefixes in the built file, so `psn-import` and `psn-compare` take
  `--pair-a <account-a>`.
- `--icons-from`: an earlier shared-activity file; a game's `icon` is copied from it by `key` where
  the manifest gives none (the timestamps carry no icons).
- A game with at least one event and an icon in neither the manifest nor `--icons-from` stops the
  run (with the number of such games), because `psn-import` clears the stored image of every entry
  whose icon becomes null and that cannot be undone. `--allow-no-icon` builds anyway.
- `--out` is written only when it does not exist, unless `--overwrite`; it may not name an input.
  The file is written beside the target first and then moved into place, so a failed write never
  leaves part of a file.
  `--dry-run` writes nothing. Field names match case-insensitively; a field given twice with
  different case stops the run. Any other shape stops the run with the file, game and trophy index.

Rules (the same as the earlier file's):

- Dates are Africa/Johannesburg calendar dates of the UTC earned times.
- One event per (game, date) on which both accounts earned at least one trophy in that game. Its
  trophy lists hold every trophy each account earned in that game on that date.
- Trophy IDs are compared only where both accounts use the same trophy set (`npCommunicationId`);
  games with different sets never have matching pairs, so their events are always same-day.
- Every trophy ID both earned on that date is a matching pair (`id`, `aTime`, `bTime`; `type` and
  `seconds` are written for reading by eye, the reader does not use them).
- Level: **strong** when at least one pair is at most 60 seconds apart (exactly 60 s is strong,
  61 s is not); **supporting** when there are pairs, none within 60 s; **same-day** when there are
  none. A pair split by midnight (Johannesburg) is not a pair on either date.
- Event `id` is `<setA>|<setB>|yyyy-MM-dd`. Games are written in manifest order, events by date then
  key. The same inputs always give the same bytes. The built file is read back with the import
  reader before it is written.

Prints: manifest games, timestamp games, games written (and with different sets), events per
level, matching pairs, trophies in events, skipped (manifest games with no timestamps, timestamp
games not in the manifest, games with no trophies, with one account only, with no common date; game
dates with one account only), game icons (from the manifest, from `--icons-from`, none, games with
events and no icon).

`psn-compare` (the acceptance check before a built file replaces the current one):

```
psn-compare <old-file> <new-file> --local <prefix> --other <prefix> --pair-a <prefix>
```

Both files are read with the import reader and the same prefixes; games are matched by `key`
(with or without events), events by `id`. Prints games and events in each; games only in old, only
in new, and in both with a different title or icon; events in both with the same level (per level),
with a different level (per old -> new level pair), only in old and only in new (per level); and
events in both with the same level whose trophies (role, set, id, type, UTC time), matching pairs
(id, `aTime`, `bTime`, pairs not located or split) or periods (label, start, end, trophies per
account) differ. Exit code 0 only when every one of these matches, 3 when anything differs, 1 when
a file is refused.

### Monthly PSN refresh (Angie)

From the repository folder. `<A>` and `<B>` are the two account field names in the fetch output
(account a's set first in every key); `<local>`/`<other>`/`<A>` are the prefixes of the current
`psn-import` command.

1. Fetch as today (needs her token): `.temp\psn\Fetch-Shared-PSN-Trophies.ps1`, giving
   `PSN-Shared-Trophy-Timestamps.json`. A game the script does not fetch (such as the 68th game,
   merged from older responses) needs both its own timestamp file in the same format, passed as a
   second `--timestamps`, and its own entry in the manifest. Without the timestamp file it is counted
   as "manifest games with no timestamps"; without the manifest entry, as "timestamp games not in the
   manifest". Either way it is not built, and `psn-compare` lists it as a difference.
2. Build a new file, never over the current one:
   `dotnet run --project tools\MemoryImport\MemoryImport.csproj -- psn-build --timestamps <timestamps.json>
   --manifest <manifest.json> --account-a <A> --account-b <B> --icons-from <current-file.json> --out <new-file.json>`
3. First time only (acceptance, D8): `... -- psn-compare <current-file.json> <new-file.json> --local <local>
   --other <other> --pair-a <A>`. Exit 0 and "Result: same." means the build reproduces the current
   file; anything else is listed by count for review before the new file is used.
4. `psn-dry-run`, then `psn-import` with the new file as today; then `psn-enrich` and `fetch-images`
   as today.

## Output

Counts and dates only: never titles, prefixes, URLs, trophy or set IDs, trophy names or details, or
paths. Errors print the
reason with indexes and counts; I/O errors print only their type.
