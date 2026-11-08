# OlderExportImport

Dry run, sender listing and import of the older WhatsApp exports (CH-33) into the
older-exports file (`older-exports.db`, beside the main workspace). The main workspace is never
opened.

Each export is a folder holding exactly one `.txt` (the chat) plus its media files. The tool finds
the `.txt` itself; a folder with zero or several `.txt` files stops with the count. Export folders
are only read: nothing is renamed, moved or written in them.

## Build

```
dotnet build tools\OlderExportImport\OlderExportImport.csproj
```

Run with `dotnet run --project tools\OlderExportImport\OlderExportImport.csproj -- <arguments>`.

## Commands

`dry-run` (the default; writes nothing). Counts per export, labelled by command-line position:
layout, first/last timestamp, messages per year, system lines, `(file attached)` lines with the
file present/absent in the export's own folder, `<Media omitted>` lines, distinct sender count,
and the largest number of source lines one message spans (a large value means header lines the
parser did not recognise were absorbed as continuation text), and how many messages had their year
supplied by the year rule (D6: a final run of shape A headers written without a year takes the
year after the last dated header).

An export that stops prints `Export N: Stopped: <reason>` and the others are still reported; the
overlap section prints its own stop reason. The exit code is 1 if anything stopped. `import`
stops on the first problem as before.

```
dry-run <export-folder>... [--fallback-folder <folder>] [--overlap <inner-folder> <outer-folder>]
```

- `--fallback-folder`: also count absent `(file attached)` names that a file of exactly that name
  in this folder would resolve (meant for the superset export).
- `--overlap`: count the lines of the inner export not found exactly (timestamp, sender, text) in
  the outer export. A non-zero count is reviewed before anything is shown (D2). The unmatched
  lines are broken down (counts only): file attached where the outer has media omitted at the same
  time and sender; identical line repeated more often than in the outer; same time and sender with
  text differing only in leading/trailing whitespace; differing otherwise; no outer line at that
  time and sender. The reverse count is the outer lines within the inner's period that are not
  found in the inner, broken down the same way. In both directions each category is split by
  line kind (user text / file attached / media omitted / system), and for lines with no line at
  the same time and sender the report counts how many have a line by the same sender within
  +/-1 minute, and an identical line within +/-60 and +/-120 minutes (clock or timezone shifts).
- `--overlap-lines` (with `--overlap`): also prints the 1-based source line numbers of the
  unmatched lines in each export, grouped by category. Line numbers only, never text or names;
  not printed by default.

- `--cut <inner-folder> <outer-folder>`: the revised D2 cut. Reports how many times the inner
  export's last line occurs exactly (timestamp, type, sender, text) in the outer (must be 1), and
  then the outer messages hidden (up to and including the match; split into dated within the
  inner's period, before its first message and after its last message, the latter two shown
  nowhere) and shown, how many shown lines exactly duplicate an inner line (must be 0, or they
  would be shown twice), and the shown part's first and last timestamp. Counts and dates only.

"Exact" everywhere (overlap, cut, duplicates) compares the timestamp, type, sender and text, with
the text compared character for character after the parser has removed only the invisible
direction marks U+200E and U+200F.

`senders` (prints sender names with counts; personal data, only on this explicit command):

```
senders <export-folder>
```

`import` (one export per run, one transaction):

```
import <export-folder> --workspace-folder <folder> --local <name> --other <name>
       --position <n> --shown yes|no [--fallback-folder <folder>] [--create]
```

- `--workspace-folder`: the folder of the main workspace; the file used is `older-exports.db` in it.
  `--create` creates that file when it does not exist yet (never over an existing file).
- `--local` / `--other`: the export's two sender names exactly as `senders` prints them. An export
  with a third sender name stops.
- `--position`: the export's place in the timeline (1 = oldest). `--shown no` keeps an export as
  evidence only; a shown export whose period overlaps another shown export is refused.
- `--fallback-folder`: only for the superset export; its absent `(file attached)` names are also
  looked for in this folder, by exact name.
- `--shown-after <inner-folder>`: show this export only after the cut (see `--cut`). The inner
  export must already be imported and shown, the cut must be found exactly once, and `--shown`
  must be `yes`. Every line is still imported; lines up to and including the match are kept as
  evidence, not shown. The overlap rule applies to the shown part only. Import the inner export
  first, and give this export a later position. A shown line after the cut that exactly
  duplicates an inner line stops the import.
- Positions must follow the timeline: at every import, the shown exports sorted by position must
  have shown periods that each start after the previous one ends (a cut export may start in its
  inner export's last minute).
- The same chat file (same SHA-256) is never imported twice.
- Format note: an older-exports file made before `--shown-after` existed is refused; move it
  aside and import again into a new file.

`--parent <folder>` (dry-run, senders, import): export folders, `--fallback-folder` and
`--overlap` are folder names under this parent.

`migrate --workspace-folder <folder> [--apply]` (CH-71): the older-exports file is an ordinary
workspace underneath, so when the workspace schema moves on (version 5 to 6 added the caption
table), the file has to move with it, or the archive export and the viewer refuse it. Without
`--apply` it opens the file read-only and prints its schema version. With `--apply` it proves the
file is an older-exports file (its own tables and marker), checks it has exactly the version-5
shape, and adds the version-6 table in one transaction. Nothing else changes. Back up the file and
close the viewer first; restoring the backup is the undo. A file already at the current version is
left alone.

## Output

`dry-run` and `import` print counts, layouts and dates only, never message text, names, file names
or paths. Errors print the reason with counts; I/O errors print only their type.
