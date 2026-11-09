# ArchiveRefresh (CH-71 step 5)

The monthly archive refresh. It runs the existing library code in process: MsgstoreImport (newer messages,
caption facts), MemoryImport (PSN build, compare, import, enrich) and the archive exporter, validator and
comparer. Output is counts only: never text, names, file names, ids or paths. Tests: synthetic data only
(`dotnet test tools\ArchiveRefresh.Tests`).

## How to run it (monthly)

From the repository folder, with the desktop viewer closed. Use the wrapper; one command per step. Type
folders without a trailing backslash when the path contains a space (PowerShell 5.1 passes `"C:\My Folder\"`
on wrongly; ticket CH-74).

1. Decrypt the msgstore with your own key (outside the project); copy the phone's WhatsApp folder (the
   one holding `Media\`).
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
