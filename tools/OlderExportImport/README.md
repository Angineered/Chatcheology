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
file present/absent in the export's own folder, `<Media omitted>` lines, distinct sender count.

```
dry-run <export-folder>... [--fallback-folder <folder>] [--overlap <inner-folder> <outer-folder>]
```

- `--fallback-folder`: also count absent `(file attached)` names that a file of exactly that name
  in this folder would resolve (meant for the superset export).
- `--overlap`: count the lines of the inner export not found exactly (timestamp, sender, text) in
  the outer export. A non-zero count is reviewed before anything is shown (D2).

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
- The same chat file (same SHA-256) is never imported twice.

`--parent <folder>` (any command): export folders, `--fallback-folder` and `--overlap` are folder
names under this parent.

## Output

`dry-run` and `import` print counts, layouts and dates only, never message text, names, file names
or paths. Errors print the reason with counts; I/O errors print only their type.
