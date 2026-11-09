# MsgstoreImport (CH-2 onward)

Operator tool for the decrypted WhatsApp msgstore. It turns msgstore matches into Phase 7
confirmations, one per attachment, through the public `Phase7ResolutionService.Confirm`, and records
msgstore facts in the workspace: forwarded status and low-quality copies (CH-8, CH-10), text-message
facts (CH-22) and picture captions (CH-63, CH-71). Promoted from `.temp` into `tools\` in CH-71; the
original design notes stay in the private `.temp` folder.

## Usage note

- Every real input is a command-line value: the msgstore and workspace paths, the conversation id,
  the chat `_id`, the UTC offset and the local participant id. Nothing is defaulted and nothing real
  is stored in this folder. `MsgstoreImport -h` prints every command.
- Output is counts only. Identifiers go only to the private manifest, which must be written outside
  the repository (the tool refuses a path inside it).
- Every write follows the same pattern: `plan` (read-only, writes the manifest and prints its
  SHA-256), back up the workspace and close the viewer, `apply` with that SHA-256 and
  `--i-have-a-backup`, and `undo-batch` with the same manifest to withdraw it. Rerunning `apply` is
  safe.
- The workspace must be at the current schema version (6). An older one is refused with
  "migrate first"; see "Migrate the workspace to schema version 6" below.
- Tests: `dotnet test tools\MsgstoreImport.Tests`, synthetic data only.

The import set is the census headline minus the name+size-only rows: every row has a hash match
(hash and name+size, or hash only). Conflicts, assets you have independently disproven for that
attachment (in any Phase 7 run), attachments already resolved, and records with no usable `key_id`
are excluded and counted too. Apply re-checks disproofs, so one recorded after the plan skips its row. All output is counts only. Identifiers go only to the
private manifest.

Paths below are placeholders. Use the same `--conversation`, `--chat`, `--utc-offset` and
`--local-participant` values as the census run.

## 1. Rebuild

```powershell
dotnet build tools\MsgstoreImport\MsgstoreImport.csproj -c Release
$tool = "tools\MsgstoreImport\bin\Release\net10.0\MsgstoreImport.exe"
```

## 2. Plan (read-only)

The manifest must go outside the repository, to a file that does not exist yet.

```powershell
& $tool plan --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> `
    --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM `
    --local-participant <ParticipantID> --manifest-out <PRIVATE FOLDER>\msgstore-import-manifest.tsv
```

Check the counts: the manifest rows by match kind, the exclusions by reason, and "reconciliation OK".
Note the `manifest SHA-256` line. Passing that digest to apply is your approval of exactly this set.
Any change to the manifest file is refused.

## 3. Back up the workspace

Close the viewer first, then copy `<WORKSPACE.DB>` to `<BACKUP FOLDER>\workspace-before-msgstore-import.db`.

## 4. Close the viewer

Keep it closed while apply runs. If something else holds the workspace, apply refuses before writing.
If that happens mid-run, it stops and counts the rest as "not attempted".

## 5. Apply

```powershell
& $tool apply --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> `
    --manifest <PRIVATE FOLDER>\msgstore-import-manifest.tsv --manifest-sha256 <SHA-256> --i-have-a-backup
```

Each row is checked again against the msgstore and the workspace: the attachment must still be
unresolved, and must still pair to the same record and asset. Then it is confirmed with basis
`ExternalProvenanceOrGroundTruth`, actor `Angie (msgstore import)` and no source run. A row that
fails a check is skipped and counted, never forced. Check that `confirmed + already done + skipped`
equals the manifest rows ("reconciliation against manifest rows OK").

Rerunning the same command is safe. Rows this batch already confirmed count as "already done", and
anything resolved some other way is left alone.

## 6. Spot-check in the viewer

Open the viewer and look at a few confirmed attachments, including some from busy minutes. Each one's
basis reads `WhatsApp msgstore record (message key_id ...), file matched by ...; import batch <tag>`,
where `<tag>` is the first 12 characters of the manifest SHA-256.

## Day-sequence pairing (CH-9)

This pairs slots the minute rule left unpaired. It lines up every message of a local day, both sides
in order, and uses only days where all of these hold:

- the message counts are equal;
- the direction agrees at every position;
- every slot sits opposite a media record;
- every earlier import confirmation on that day sits opposite the record it names.

Those earlier confirmations are read from the workspace itself, so no prior manifest is needed. Each
new slot must also pass these checks:

- it is unresolved and was not paired by the minute rule;
- the record's minute is within 2 minutes of the slot's;
- the file matched by hash;
- the asset is in the candidate set;
- the asset is not disproven.

```powershell
& $tool plan --rule day-sequence --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> `
    --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM `
    --local-participant <ParticipantID> --manifest-out <PRIVATE FOLDER>\manifest-dayseq.tsv `
    --sample-out <PRIVATE FOLDER>\dayseq-sample.md --seed 7 --sample-size 20
```

Read any "ATTENTION ... DISAGREE" line before going further. Check the sample against the phone,
then back up and close the viewer. Apply and undo-batch work exactly as for the first batch, with the
new manifest and its SHA-256.

## Outside the candidate set (CH-2 leftovers)

`plan --rule outside-candidate-set` takes paired slots (by minute or by day sequence) that fail only
the candidate-set check. The day must be usable under the day-sequence checks, and the slot must be
hash-matched, not disproven and unresolved. The file date is the naming-derived `MediaFile.FileDate`
(`-YYYYMMDD-WA` in the file name, never the copy's filesystem time). The earliest copy's date counts.
Files dated more than 1 day after the message are excluded, and so are files with no date. The plan
prints how many rows fall in each date-gap band. Same options as day sequence (`--sample-out`, `--seed`,
`--sample-size`). Apply and undo-batch work as before.

## Documents (addendum)

`plan --rule documents` takes slots the outside rule excludes only for having no naming-derived
date, where the msgstore message is a document (message_type 9). WhatsApp keeps a document's
original name, so it carries no date. Every other outside guard still holds. The sample lists the
record file name, which is the name shown in the phone's bubble, and the msgstore media_name.
Check the sample on the phone before applying. Apply and undo-batch work as before.

## Name+size-only matches, by review (addendum)

1. `review-list --rule namesize` (read-only) writes `--review-out <private file>`. It lists the
   paired, unresolved slots on usable days whose record matches exactly one non-empty asset by name and
   size but not by hash, where that asset is not disproven and the record has a key_id. Each row gives the
   proposed file, the full paths of its copies, and whether the viewer lists it. Stdout also prints the
   list's SHA-256.
2. Open each file and type R, W or ? in the last column (typing it just after the final `|` also
   works). Change nothing else.
3. Run `plan --rule namesize-reviewed --review-file <marked list> --review-sha256 <list SHA-256>` with
   the usual inputs and `--manifest-out`. Only R rows are taken, and each is re-checked.
4. Back up the workspace, then run `apply` with `--review-file <marked list>` as well. Each row must
   still be marked R. These are confirmed as HumanContentOrContextInspection by
   `Angie (reviewed name+size list)`. undo-batch works as before.

## Add a media folder (CH-11)

`add-source` registers one more folder as a media source (type `WhatsAppMediaDirectory`, so the
`-YYYYMMDD-WA` name date is read) through the public `MediaInventoryService`, then hashes its files with
`MediaHashingService`. Without `--apply` it only walks the folder and prints counts (files, bytes, how many
have a name date, direction); the workspace is not opened. With `--apply` it refuses a wrong schema
version, a root that overlaps an existing source, or an empty folder, and writes the source and its files
in one transaction before hashing. Output is counts only. Back up the workspace and close the viewer first.
A file with no `Sent` folder anywhere in the tree gets no recorded direction; the outside rule does not
read asset direction.

```powershell
& $tool add-source --workspace <WORKSPACE.DB> --root <NEW FOLDER> --display-name "<NAME>" --device "<DEVICE>" --apply
```

Then run `plan --rule outside-candidate-set` as above.

## Explain the "no naming-derived date" exclusions (read-only)

`explain-nodate` takes the plan inputs plus `--detail-out <private file>`. It covers the slots the
outside rule excludes only because the asset has no `-YYYYMMDD-WA` name date. It counts them by mime
type, match kind, direction and the name pattern of the asset's copies (camera style, other date,
document, audio, none). For dated patterns it gives the date-gap spread, and it also counts shared
assets and copies per media source. The detail file lists every slot with its copy names.

## List what is still unresolved (read-only)

`list-unresolved` takes the plan inputs plus `--list-out <private file>`. It writes one row per
unresolved attachment, in date order, showing how it paired, the WhatsApp record and why no rule took
it. The last column is for marking whether the file was found on a phone. Stdout shows counts only.

## Record the accepted missing list (CH-13)

`plan --rule review-missing` takes every attachment of the conversation that is unresolved and has no
current "Reviewed — insufficient evidence" review. It takes the plan inputs plus `--expected-count`
and `--manifest-out`. It refuses, writing nothing, unless exactly that many qualify. A slot with no
completed Phase 7 run also refuses the plan, because a review must name one. Each manifest row gives
the attachment, its current evidence run and the reason category from `list-unresolved`: no file in
any copy, WhatsApp kept no file, same name different size, dated after message, or `other: <reason>`.
Stdout shows counts by category and the manifest SHA-256.

```powershell
& $tool plan --rule review-missing --expected-count <N> --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID> --manifest-out <PRIVATE FOLDER>\manifest-missing.tsv
```

Back up the workspace and close the viewer. Then run `apply` with the new manifest, its SHA-256 and
`--i-have-a-backup`, as for the import batches. Each row is checked again: it must still exist, be
unresolved, have the same evidence run, have no review yet and have the same reason category. Then
one review is recorded through the public `Phase7ReviewService`, with actor
`Angie (accepted missing list)` and notes
`Accepted as missing <apply date> (CH-13): <reason category>; batch <tag>`. A row resolved or reviewed
since the plan is skipped and counted. Rerunning is safe: rows this batch already reviewed count as
"already done". The viewer then reports these slots as `ReviewedInsufficientEvidence`. A later Confirm
still works.

**There is no undo-batch for this rule.** Phase 7 reviews are append-only and Phase 7 has no revoke for
them, so `undo-batch` refuses a review-missing manifest. The backup taken before apply is the undo.
Restoring it also discards every decision made after the backup.

## Probe one minute (read-only)

To look at a minute that seems wrong in the viewer:

```powershell
& $tool probe --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> `
    --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM `
    --local-participant <ParticipantID> --date YYYY-MM-DD --time HH:MM --probe-out <PRIVATE FOLDER>\probe.md
```

Stdout shows counts only. The private file covers the target minute and the minutes either side,
in both directions. It lists the workspace attachments in key order (with resolution, batch tag,
asset files, and how many slots share an asset) and the msgstore media records in pairing order
(with key_id, type, file, a hash prefix, the match, and how many records share a hash). It also
lists every msgstore message row in the target minute, media or not, without any text.

## Probe forwarding and linked copies (CH-8, CH-10; read-only)

```powershell
& $tool probe-links --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID> [--minute YYYY-MM-DDTHH:MM]
```

Stdout only; writes no file. It prints table and column names, enumeration values and counts, never
text, names, file names, key_ids, paths or row ids. Section 1 lists every table and column whose name
contains forward, hd, quality, assoc(iation), parent, child, original, add_on/addon, related, edit,
revision or thumb, with declared types and row counts. Section 2 (forwarding) reads any table whose
name contains `forward` keyed by `message_row_id` (other columns bucketed: 0, 1, 2-4, 5-9, 10-99,
100+) and a legacy `message.forwarded` column. Section 3 (linked copies) reads any table whose name
contains `assoc` with parent and child `..._row_id` columns, counted by its `...type` column.
"Paired" means the minute rule or the CH-9 day-sequence rule lines the msgstore message up with a
workspace attachment. `--minute` (local) adds the same counts for that minute, plus how many media
records would be left with one side of each link hidden. Section 4 says when none of these exist.

## Probe pairing of text messages (CH-22; read-only)

```powershell
& $tool probe-text --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID>
```

Stdout only; writes no file. Text is read from both databases and compared in memory; the output is
counts, the msgstore text column used (`message.text_data`, else `message.data`) and message_type
values. A workspace text message is a `User` message with no Attachment row whose content is not the
media placeholder, not the placeholder followed by a caption, and not empty. The candidate rule
(measured, not adopted): same local minute, same direction, identical text after normalisation (U+200E/U+200F
removed, line endings to LF, Unicode NFC, trim); unique means one candidate each way. It also counts edited-marker mismatches that would match with the marker removed (reported only). It also reports the same
counts without normalisation and with a +-1 minute window, how unmatched messages split (different
text in the minute, other direction only, other messages only, nothing), forwarded messages without a
`message_media` row by type and pairing, and `message_edit_info` overlap. NFC uses Windows'
`NormalizeString` (the tool runs with invariant globalization).

## Migrate the workspace to schema version 6 (CH-71)

The tool needs schema version 6 (one new table, `AttachmentCaptionFact`, for picture captions).
Every command refuses a version 5 workspace with "migrate first". `migrate` takes a version 5
workspace to version 6 through the product's own `WorkspaceDatabase.Initialise`, in one transaction.
Nothing else changes; the CH-10 attachment facts and CH-22 text facts stay as they are. (Version 5,
CH-22, added `MessageMsgstoreFact`; this command took version 4 to 5 before CH-71.)

```powershell
& $tool migrate --workspace <WORKSPACE.DB>
```

Without `--apply` it is a dry run. It prints the current version and whether the v5-to-v6 shape gate
passes (exactly the version-5 tables and indexes, counted as missing and unexpected), and writes nothing.
It refuses a path with no file (before anything could create one), any version other than 5, and a
workspace another operation has claimed. A version 3 or 4 workspace is refused too: this command takes
only the one step from 5. Back up the workspace, close the viewer, then add `--apply`. It prints the
version before and after. Restoring the backup is the undo.

The older-exports file beside the workspace (`older-exports.db`) is a workspace underneath and must
move to version 6 as well, or the archive export and the viewer refuse it. This tool does not touch
it: use `OlderExportImport migrate --workspace-folder <folder> [--apply]` (see that tool's README).

## Forwarded status and low-quality copies (CH-8, CH-10)

`plan --rule facts` writes one row per attachment paired by the minute rule or the day-sequence rule.
Each row records WhatsApp's own facts:

- **forwarded:** the message has a `message_forwarded` row (or a non-zero legacy `message.forwarded`);
- **forward score:** that row's `forward_score`, or none;
- **low-quality copy:** a link is recorded only for a `message_association` type-12 link whose two sides
  are paired attachments in the same local minute and direction, with the parent's file strictly smaller.
  The parent (smaller) attachment is recorded as the low-quality copy of the child.

A link that fails a check records no link, but the row still records forwarding. Links of other types are
ignored. A copy with two full copies, or a link that would make a chain, records no link. A slot the
day-sequence rule lines up with a record more than 2 minutes away gets no row. Attachments that already
have a fact from another batch are left alone. The plan prints the rows, forwarded, forwarded many times
(score 5 or more), links, links refused by each check, and attachments with no row. A version 5
workspace is refused: migrate first.

```powershell
& $tool plan --rule facts --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID> --manifest-out <PRIVATE FOLDER>\manifest-facts.tsv
```

Back up the workspace and close the viewer. Then run `apply` with the new manifest, its SHA-256 and
`--i-have-a-backup`. Every row is computed again; if any differs from the manifest, the whole batch is
refused and nothing is written (plan again). The rows are written in one transaction through
`MsgstoreFactWriter`, tagged with the batch tag. Rerunning is safe: rows already there count as
"already done". `undo-batch` with the same manifest deletes that batch's rows. These are facts, not
Phase 7 decisions, so the undo removes them completely.

## Forwarded status of text messages (CH-22)

`plan --rule text-facts` writes one row per workspace text message (a `User` message with no Attachment
row, not the media placeholder, not a caption, not empty after normalisation) that pairs uniquely to a
msgstore text message: same local minute, same direction, identical text after normalisation
(U+200E/U+200F removed, LF, NFC, trim; a trailing `<This message was edited>` and the whitespace before
it removed from the workspace text first), and the only candidate both ways. Ambiguous and unmatched
messages get no row. Captions are left out: their attachment already carries the fact.

Each row records whether the msgstore message is forwarded (a `message_forwarded` row; the legacy
`message.forwarded` column only when that table is absent) and its `forward_score` (none when absent or
not a non-negative number; counted). The plan prints text messages, paired, ambiguous, unmatched, the
gain (and any loss) from the edited-marker strip, forwarded, forwarded many times (score 5 or more), the
manifest SHA-256, the batch tag and a reconciliation. Messages that already have a fact from another
batch are left alone. A version 5 workspace is refused: migrate first.

```powershell
& $tool plan --rule text-facts --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID> --manifest-out <PRIVATE FOLDER>\manifest-text-facts.tsv
```

```powershell
& $tool apply --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --manifest <PRIVATE FOLDER>\manifest-text-facts.tsv --manifest-sha256 <SHA-256> --i-have-a-backup
```

```powershell
& $tool undo-batch --workspace <WORKSPACE.DB> --manifest <PRIVATE FOLDER>\manifest-text-facts.tsv --manifest-sha256 <SHA-256> --i-have-a-backup
```

Back up the workspace and close the viewer before `apply`. Every row is computed again; if any differs
from the manifest, or a message would now get a row the manifest lacks, the whole batch is refused and
nothing is written (plan again). The rows are written in one transaction through
`MessageMsgstoreFactWriter`, tagged with the batch tag; a rerun counts them as "already done".
`undo-batch` deletes this batch's rows only.

## Picture captions (CH-63, CH-71)

A picture's export line is exactly the media placeholder, so its caption exists only in the msgstore.
`plan --rule caption-facts` writes one row per attachment that pairs to a msgstore media message and
has a caption. Rules:

- **Pairing** is exactly the `facts` rule's: the minute rule, then the CH-9 day-sequence rule for
  slots it left. An attachment the two rules pair with different records, or whose record is paired
  to two attachments, is *ambiguous*; one neither rule pairs (or the day rule lines up more than
  2 minutes away) is *unpaired*. Neither ever gets a caption; both are counted.
- **The caption** of a msgstore message is its text column (`message.text_data`, or `message.data`
  in an older schema). Only when that holds no caption is a `media_caption` column read, if the
  msgstore has one (`message_media.media_caption`, else `message.media_caption`). The text column
  wins because it is where the current schema keeps a caption (the same column the text rules read);
  `media_caption` is a legacy column whose value may be stale after a WhatsApp schema upgrade.
- A value counts as a caption when it is not empty after the CH-22 normalisation (U+200E/U+200F
  removed, LF, NFC, trim). The normalisation is only used for that decision and for comparing; the
  caption is **stored exactly as the msgstore holds it**, untrimmed.
- **The type-12 partner.** If the attachment's own message has no caption, the messages linked
  to it by `message_association` type 12 (either side) are looked at, but only a partner in the same
  chat, direction and local minute. Exactly one distinct caption among them is used and recorded with
  `FromPartner = 1` (the partner may be the hidden low-quality copy or the shown full copy); two or more different ones make the attachment ambiguous (no row). An own
  caption always wins; a different partner caption is only counted.
- An attachment that already has a caption from another batch is left alone and counted.

Output is counts only: attachments, paired (minute / day sequence), unpaired, ambiguous (by reason),
with a caption (own / from the partner / read from `media_caption`), no caption, already present
(left alone), partner links refused (other chat, direction, minute), and a reconciliation. The
manifest holds identifiers and a SHA-256 of each caption, never the caption itself.

```powershell
& $tool plan --rule caption-facts --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --conversation <ConversationID> --chat <chat _id> --utc-offset +HH:MM --local-participant <ParticipantID> --manifest-out <PRIVATE FOLDER>\manifest-captions.tsv
```

```powershell
& $tool apply --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --manifest <PRIVATE FOLDER>\manifest-captions.tsv --manifest-sha256 <SHA-256> --i-have-a-backup
```

```powershell
& $tool undo-batch --workspace <WORKSPACE.DB> --manifest <PRIVATE FOLDER>\manifest-captions.tsv --manifest-sha256 <SHA-256> --i-have-a-backup
```

Back up the workspace and close the viewer before `apply`. Every row is computed again from the
msgstore; if any row differs from the manifest (a caption whose digest changed included), or an
attachment would now get a row the manifest lacks, the whole batch is refused and nothing is written
(plan again). The rows are written in one transaction through `AttachmentCaptionFactWriter`, tagged
with the batch tag. A rerun records nothing and counts every row as "already done". `undo-batch`
deletes this batch's rows only.

## Newer messages (CH-71 step 3a)

`newer plan` and `newer apply` bring messages newer than the main workspace into `newer-messages.db`, a
separate file beside the workspace (fixed name, its own schema and marker; the workspace is only read).
The archive export and the phone read it from step 3b on.

```powershell
& $tool newer plan --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --conversation <ConversationID> --chat <chat _id> --local-participant <ParticipantID> --media-root <WHATSAPP FOLDER COPY> --manifest-out <PRIVATE FOLDER>\newer-manifest.tsv
```

```powershell
& $tool newer apply --msgstore <MSGSTORE.DB> --workspace <WORKSPACE.DB> --media-root <WHATSAPP FOLDER COPY> --manifest <PRIVATE FOLDER>\newer-manifest.tsv --manifest-sha256 <SHA-256> --i-have-a-backup
```

- **Hand-off, first run.** Every msgstore message of the chat with a readable time is put on its local
  day (Africa/Johannesburg) in `sort_id`, then `_id`, order; every workspace message on its stored day.
  Each day before the workspace's last day must have the same count on both sides. On the last day the
  msgstore's messages must start with exactly the workspace's: same direction at each place; a workspace
  text message faces the same text after the CH-22 normalisation (the export's edited marker removed);
  a workspace attachment faces a msgstore media record; anything else is compared by direction only,
  but a media record never faces workspace text. Everything after that point is new. Otherwise the
  plan refuses with counts only.
- **Later runs.** The last imported key_id must be found exactly once in the chat; the new messages are
  those after it. Messages are only ever added at the end; nothing already imported changes. Edits and
  deletions made on the phone since are counted (the archived version is kept).
- **Kinds.** Text (type 0) is kept exactly as the msgstore holds it. Media types 1, 2, 3, 9, 13 and 20
  with a media record become a message with one attachment (caption as in `caption-facts`: the text
  column, else `media_caption`). Polls (66), locations (5, 16), contact cards (4, 14), calls (90),
  view-once (42, 43, 82) and deleted-for-everyone (15) become a fixed placeholder such as
  "Poll (not shown in this archive)"; system rows (7) "System message (not shown in this archive)";
  any other type "Message type not shown in this archive", counted by its number. Never the item's
  content. Reactions (`message_add_on`) and quotes (`message_quoted`) are counted only. The type
  numbers beyond 1 and 9 are the published reverse-engineered meanings, not yet confirmed on the real
  msgstore: check the plan's "by msgstore message_type" list against the phone before applying.
- **Media.** `--media-root` is a fresh copy of the phone's WhatsApp folder (the one holding `Media\`),
  which msgstore's `file_path` is relative to. An attachment is Restored only when that file exists
  and its SHA-256 equals the base64-decoded `file_hash`; otherwise Unmatched (never Missing, never by
  name or size), counted by reason. The folder is registered in `newer-messages.db`; keep it.
- **Forwarded** from `message_forwarded` (score kept); no forwarding table means "not known".
  **Low-quality copies** (association type 12): same minute and direction, the smaller file is the copy;
  recorded on this run's new rows only.
- **Apply** recomputes the whole plan and refuses unless the manifest it would write is byte-identical
  (so a changed msgstore, workspace, file or media folder means "plan again"). One transaction; the
  first run creates the file. Rerunning an applied manifest writes nothing. The manifest holds ids and
  SHA-256 digests only; the media folder only as a digest of its path.

## Undo

To undo one attachment, use the viewer's Revoke as usual.

To withdraw the whole batch, close the viewer, back up again, and run:

```powershell
& $tool undo-batch --workspace <WORKSPACE.DB> `
    --manifest <PRIVATE FOLDER>\msgstore-import-manifest.tsv --manifest-sha256 <SHA-256> --i-have-a-backup
```

This revokes only the attachments whose current decision is still this batch's confirmation, with the
reason `msgstore import batch <tag> withdrawn`. Anything confirmed by hand, replaced or revoked since
is left alone and counted. Restoring the backup also works, but it discards every decision made after
the backup.

Note: running apply again with the same manifest after undo-batch confirms those attachments again
(each gets a new confirmation event), because they are unresolved again. Only do that if you mean to
re-import the batch.
