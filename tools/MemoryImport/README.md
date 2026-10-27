# MemoryImport

Dry run and import of the PSN shared-activity file (CH-42) into the memories file
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

## Output

Counts and dates only: never titles, prefixes, URLs, trophy or set IDs, trophy names or details, or
paths. Errors print the
reason with indexes and counts; I/O errors print only their type.
