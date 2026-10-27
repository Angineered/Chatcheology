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

Schema `chatcheology-psn-shared-activity-v1` only; any other schema is refused. Only `timezone`,
`games` and `events` are used.

Account fields are named after the two PSN accounts. `--local <prefix>` and `--other <prefix>` give
the two field prefixes (case-insensitive, letters and digits only, not the same). Both must be
present in every game (`<prefix>TrophySet`) and every event (`<prefix>Trophies`); otherwise the run
stops with the game or event index. The local prefix maps to the chat's Local participant role, the
other to Other. Prefixes are never printed.

Per event: `id` (`<set>|<set>|<date>`, the idempotency key; its two sets name the game), `quality`
(copied as the day level), the two trophy lists (each trophy: `id` or `trophyId`, `type` or
`trophyType`, `utc`, `time`), and `matchingTrophies`. Per game: `title`, `icon`, the two trophy sets.
Each trophy's `utc` is converted to the file's IANA `timezone` and must equal its `time` field to the
second; a mismatch stops the run with the event index.

A matching pair side is read either from a nested object named after the prefix (`id`/`trophyId`,
`time`) or from flat `<prefix>Id`/`<prefix>TrophyId` and `<prefix>Time` fields. A pair is located by
(role, trophy id, local time); a pair not located or whose two trophies fall in different periods
is counted and reported, never guessed.

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
[psn-dry-run] <psn-file> --local <prefix> --other <prefix> [--gap <minutes>]
```

Prints games, records, items, periods per label, timeline cards (periods with both accounts),
records with no shared period, matching pairs not located / split, records per year, first and
last date.

`psn-import` (one transaction):

```
psn-import <psn-file> --workspace-folder <folder> --local <prefix> --other <prefix>
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
