# ArchiveRefresh (CH-71 step 5)

The monthly archive refresh. It runs the existing library code in process: MsgstoreImport (newer messages,
caption facts), MemoryImport (PSN build, compare, import, enrich) and the archive exporter, validator and
comparer. Output is counts only: never text, names, file names, ids or paths. Tests: synthetic data only
(`dotnet test tools\ArchiveRefresh.Tests`).

## How to run it (monthly)

From the repository folder, with the desktop viewer closed. Use the wrapper; one command per step. Type
folders without a trailing backslash when the path contains a space (PowerShell 5.1 passes `"C:\My Folder\"`
on wrongly; ticket CH-74).

1. Copy from the phone (USB, unlocked, USB debugging allowed; in WhatsApp back up the chats first and wait
   for it to finish). Give a new folder, and last month's pull folder as `-Previous` (the first time: the
   folder the old `.temp` pull script made, which holds `WhatsApp\` and `Databases\`). Put it on the same
   drive as the previous one so unchanged files are hard-linked instead of copied:

   ```powershell
   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Pull -Destination <new folder> -Previous <last month's pull folder>
   ```

   Add `-Serial <id>` when more than one phone is connected. Without `-Previous` everything is pulled
   (about 55 GB, over half an hour). Read the counts. If it stops, run the same command again: it carries
   on. Then decrypt `<new folder>\Databases\msgstore.db.crypt15` with your own key (outside the project);
   the media root for Plan and Apply is `<new folder>\WhatsApp`. Keep this folder: next month it is the
   `-Previous`.
   If PSN is included:
   1. In Postman run Request 1, then Request 2, for a fresh token (and keep `partnerAccountId` from the
      collection's Variables to hand).
   2. Fetch (asks for the other account's id and the token, hidden; never saved):

      ```powershell
      .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step PsnFetch -PsnManifest <last game list> -PsnDefinitions <last definitions file> -PsnDecisions <your decisions csv> -PsnAccountA <name> -PsnAccountB <name> -PsnLocal <a or b name> -PsnOut <new private folder>
      ```

      The last game list and definitions file are last month's `psn-game-list.json` and
      `psn-trophy-definitions.json` (the first time: the October game list and definitions file).
      The decisions csv is your one decisions file (columns `game_title`, `status` = together,
      uncertain or excluded, and one `<name>_trophy_set_ids` column per account; see
      `tools\MemoryImport\README.md`, psn-fetch). A game is added only when a row marks it together.
      Read the counts. If "awaiting review" is above 0, open `psn-to-review.csv` in the PsnOut folder
      (newest first), fill in the status of the rows you decide, copy them into your decisions file (comma-separated UTF-8;
      in Excel "CSV UTF-8"), and if you want
      them in this month, run PsnFetch again into a new PsnOut folder (same game list). If it stops
      (token, network, HTTP 403 which usually means an account's trophy privacy, a listed game no
      longer returned), fix the cause and run the same command again; finished games are skipped. If
      it stops on decisions (rows that disagree, or a listed game marked excluded or uncertain),
      correct the decisions file and run it into a new PsnOut folder.
   3. In Plan and Apply below give `-PsnTimestamps <PsnOut>\psn-trophy-timestamps.json`,
      `-PsnManifest <PsnOut>\psn-game-list.json`, `-PsnDefinitions <PsnOut>\psn-trophy-definitions.json`,
      and `-PsnCurrent` the `psn-shared-activity.json` in the previous plan folder.
2. Plan (read-only; writes only a new private plan folder outside the repository):

   ```powershell
   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Plan -Workspace <workspace.db> -Msgstore <msgstore.db> -MediaRoot <WhatsApp folder copy> -Conversation <id> -Chat <chat _id> -LocalParticipant <id> -ExpectRestored <n> -ExpectMissing <n> -PlanFolder <new private folder> [PSN parameters]
   ```

   Read the counts (check the "by msgstore message_type" list against the phone, and any ATTENTION line
   in the PSN section). Note the `PLAN SHA-256`.
3. Apply with that SHA-256 (same paths and PSN parameters as the plan):

   ```powershell
   .\tools\ArchiveRefresh\Run-Refresh.ps1 -Step Apply <same parameters> -PlanFolder <same folder> -Sha256 <PLAN SHA-256> -BackupRoot <existing backup folder> -ArchiveOut <new archive folder>
   ```

4. Optional, needs the internet: game images and trophy icons
   (`Run-Refresh.ps1 -Step Images -Workspace <workspace.db>`). They reach the phone with the next archive.
5. Copy to the phone: `Run-Refresh.ps1 -Step Copy -ArchiveOut <archive folder>` (runs
   `tools\Copy-ArchiveToPhone.ps1`), then Import archive... on the phone.

PSN parameters (all or none): `-PsnTimestamps <file>[,<file>]`, `-PsnManifest <game list>`,
`-PsnAccountA <name>`, `-PsnAccountB <name>` (account a's set first in every key), `-PsnLocal <a or b name>`,
`-PsnCurrent <current shared-activity file>`, `-PsnDefinitions <definitions file>`. Next month, give
`-PsnCurrent` the `psn-shared-activity.json` in this month's plan folder, and PsnFetch this month's
`psn-game-list.json` and `psn-trophy-definitions.json`.

## What pull does (CH-76)

Read-only on the phone: it runs only `adb devices`, one listing and `adb pull -a`.

1. Refuses unless exactly one phone is connected and authorised (state `device`; emulators ignored), or the
   one `-Serial` names; unless the destination is new (or an unfinished pull, see below); unless the previous
   copy holds `WhatsApp\`, is not itself unfinished, and is separate from the destination.
2. Lists the phone's WhatsApp folder in one call: `find <folder> '(' -type f -o -type d ')' -exec stat -c
   '%f/%s/%Y/%n' {} +` (toybox: mode in hex, size, mtime in whole seconds, full path). The three numbers
   come first and a name cannot hold `/`, so names with spaces, quotes or other characters split safely.
   The listing must include the folder itself in that format (otherwise the phone's stat is not as expected
   and nothing is copied). Names Windows cannot hold (`: ? * "`, a line break, a trailing dot or space, a
   reserved name) or that differ only in case are refused with a count: a full copy would fail on them too.
3. For each file: the same relative path, size and mtime (whole seconds) as in the previous copy's
   `WhatsApp\` means a hard link from the previous copy (a local copy, with its mtime, when the two are not on
   one NTFS volume). Everything else is pulled with `adb pull -a`, which keeps the phone's mtime in whole
   seconds. The whole `Databases` folder, and `Databases\msgstore.db.crypt15` into `<destination>\Databases`,
   are always pulled. Files on the previous copy but no longer on the phone are counted, not copied. Empty
   folders are made too, so `WhatsApp\` ends up as a full pull would make it.
4. Free space: what is pulled (plus what is copied locally when linking is not possible) plus 5 GB.
5. On screen, counts only: same (hard-linked / copied locally), new, changed, Databases files, gone from the
   phone, bytes pulled, duration, and the date of the phone's msgstore backup.
6. `pull-manifest.tsv` in the destination lists every file: how it was obtained (linked, copied, pulled),
   size, mtime and relative path.

The previous copy is never written: files are only read from it or linked to. A hard link is the same file
under two names, so never edit a file inside a pull folder (deleting a whole pull folder is fine; the other
months keep their files).

A stop (phone locked or unplugged, the window closed): `pull-state.tsv` stays in the destination. Run the
same command again (same `-Previous`) and it continues: files already in place with the right size and mtime
are kept, the rest are fetched, and files the phone no longer has are taken out. Or delete the destination
folder and start again.

## What plan does

Preflight: the workspace at schema 6; no `-wal`/`-shm`/`-journal` beside workspace, older-exports,
newer-messages or memories; the msgstore refused if a `-wal`/`-journal` sits beside it; the media folder
there; older-exports, newer-messages, memories and `archive-id.json` read exactly as the export reads them
(the export's Preview); restored and reviewed-missing counts equal to `--expect-*`; PSN files present and
`memories.db` there. Then the newer-messages plan (step 3a), the caption-facts plan (attachments already
captioned are left alone), and with PSN: the build into the plan folder, a compare with the current file
and an import dry run. The plan file (`refresh-plan.tsv`) records every parameter, the size and SHA-256 of
every input (the five writable files, the msgstore, the PSN files), paths and account names only as
digests, and each sub-manifest's SHA-256. Its SHA-256 is the one plan SHA-256.

## What apply does

1. Refuses unless the plan file's SHA-256 is the one given, the paths are the plan's, every sub-manifest
   is unchanged, and every input is as planned (or as a stopped apply left it).
2. Makes `<backup-root>\refresh-backup-<yyyyMMdd-HHmmss>` (refused if it exists) with the workspace,
   older-exports.db, newer-messages.db, memories.db and archive-id.json that exist, each verified by
   size and SHA-256 (`backup-contents.tsv` lists them), before the first write.
3. Applies in order: newer messages, caption facts, PSN import, PSN enrich, export (format 4) to
   `--archive-out`. Each step is one transaction. It stops at the first refusal and says what was applied.
4. Runs the validator and the comparer, prints the counts and the copy command.

`apply-progress.tsv` in the plan folder records each backup, step begun and step done with the file
state. Rerun the same apply after a stop: finished steps are skipped, a step stopped inside is run again
(each is idempotent), and anything else changing the files in between is refused (plan again). Undo:
restore the files of the FIRST backup folder (named in the apply output and in every stop message) beside
the workspace: it holds them as they were before the refresh. Each rerun takes another backup, "taken
before this rerun", which holds the partly refreshed files.

If an apply stops during the export, a folder named like `--archive-out` with `.partial` on the end may
be left beside it. Delete that `.partial` folder, or give a new `--archive-out`, then run the same apply
again.

Exit codes: 0 done and the archive passed; 1 failed or the archive did not pass; 2 refused or stopped.
