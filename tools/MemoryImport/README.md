# MemoryImport

Dry run and import of the PSN shared-activity file (CH-42) into the memories file
(`memories.db`, beside the main workspace), and download of the game images into
`memories-media\` beside it. The main workspace is never opened. The PSN file is only read.

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

`fetch-images`:

```
fetch-images --workspace-folder <folder> [--max-bytes <n>]
```

Downloads each distinct entry image URL whose image is not stored yet: HTTPS only, hosts
`image.api.playstation.com` and `psnobj.prod.dl.playstation.net` only, no redirects followed, size
cap (default 5 MB), PNG/JPEG/GIF/WebP only. Saved as `memories-media\<sha256>.<ext>`; identical
images are stored once. Prints fetched / already present / failed. Exit code 1 if any failed.

## Output

Counts and dates only: never titles, prefixes, URLs, trophy or set IDs, or paths. Errors print the
reason with indexes and counts; I/O errors print only their type.
