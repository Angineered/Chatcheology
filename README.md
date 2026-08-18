# Chatcheology

Chatcheology is an offline-first .NET application for importing, reconstructing and
preserving exported chat histories.

The first planned source is WhatsApp chat exports, but the architecture avoids
unnecessary WhatsApp-specific assumptions where practical.

## Development status

**Early foundation.** The repository currently contains the solution structure, a
synthetic test fixture, a text parser for one pinned WhatsApp Android export layout,
version 2 of the workspace SQLite database, and the media inventory, hashing and
deduplication services described below.

No media matching, archive generation or user interface functionality is implemented yet.
The desktop application does not use the database.

## Offline and privacy first

- Chatcheology is designed to operate entirely locally.
- Chat history and media stay on the user's machine.
- There are no planned cloud services, telemetry or user accounts.
- Raw source material is treated as read-only.

Raw input files are never renamed, reorganised, deduplicated or modified by the
application. Anything Chatcheology generates is kept separate from the raw source it
was derived from.

## Planned architecture

- A WPF desktop application for import, recovery and archive management.
- A shared core library holding parsing, media inventory, matching and archive logic.
- A SQLite working database holding reconstruction state. Schema version 2 exists; see
  [Workspace database](#workspace-database).
- A media inventory with SHA-256 deduplication.
- Conservative, evidence-based media matching that can explain why a match was
  proposed and that remains reviewable by the user.
- A portable generated archive database (`archive.db`).

Matching is intended to be deliberately cautious. A media file is not treated as
belonging to a message merely because the dates line up or because it was the only
file found that day.

## Components

| Project | Description |
| --- | --- |
| `Chatcheology.Core` | Shared library. Currently the chat export text parser only. |
| `Chatcheology.Data` | Workspace SQLite persistence: schema, conversation import, and media inventory. |
| `Chatcheology.Desktop` | WPF desktop application (.NET 10, Windows). Minimal shell. |
| `Chatcheology.Core.Tests` | xUnit tests for the core library. |
| `Chatcheology.Data.Tests` | xUnit tests for the workspace database and media inventory. |

A .NET MAUI read-only Android viewer for generated archives is planned for later. It
is not part of this repository yet.

## Supported export format

The only supported WhatsApp export format is the Android-style layout:

```text
yyyy/MM/dd, HH:mm - Sender: Message
```

Timestamps are parsed with the invariant culture, so the result does not depend on the
machine's regional settings. A line that does not match this layout continues the
message above it; a line that has this layout's shape but is not a valid header is
rejected rather than reinterpreted. Messages keep their source order, which is
authoritative because the format only records whole minutes.

WhatsApp's own system messages are also read. These carry the same timestamp prefix but
no sender, for example the end-to-end encryption notice:

```text
yyyy/MM/dd, HH:mm - Messages and calls are end-to-end encrypted.
```

A timestamped line is treated as a participant message when the text after the timestamp
carries a non-empty sender followed by the exact `": "` delimiter, and as a system
message when that delimiter is absent. One ambiguity is accepted rather than guessed at:
system text that itself contains `": "` is structurally identical to a participant
message and is read as one. No heuristic tries to separate the two, because a wrong guess
would attribute a message to someone who did not write it.

The invisible direction marks U+200E and U+200F are handled, since real exports place them
around media placeholders and system notices. They are ignored when recognising structure
and removed from the sender and message content, while `RawContent` keeps the source text
exactly as read. No other invisible or control character is altered.

Not supported yet: iOS layouts, 12-hour clocks, timestamps with seconds, other locale
variants, and system-message subtypes — a system message is not classified further than
"system". The parser recognises a media placeholder as text only: no media file is
inspected, and no media type is inferred from it. Turning a placeholder into a stored
attachment belongs to the workspace database, not the parser.

## Workspace database

Parsed messages can be persisted into a workspace SQLite database. The schema version is
recorded in SQLite's own `PRAGMA user_version` and is currently version 2, which holds:

- the import source the messages came from
- the conversation
- its participants, and their membership of that conversation
- the messages
- an attachment per expected media item belonging to a message
- media sources, media files, deduplicated media assets, and which files carry which asset

Creating the schema, migrating it and importing a conversation are each a single
transaction, so a failure leaves neither a partially created workspace, nor a partially
migrated one, nor a partially imported conversation. Foreign keys are enforced on every
connection, and a message's sender must be a participant of the same conversation the
message belongs to.

Not implemented yet: media matching, archive generation, and any user interface for
import or inventory. The desktop application does not consume the database, and the caller
always supplies the workspace file path — no location is assumed.

### Attachments

A media placeholder in an export says that something was omitted, not what it was. Version
2 records that as an explicit `Attachment` row rather than as a flag on the message: one
attachment per expected media item, created when a message's content is exactly the
placeholder text. A placeholder that carries a caption, or that appears inside longer text,
is ordinary message content and produces no attachment.

Every attachment is created unresolved, with no expected media type and no linked asset.
Nothing infers what kind of file was omitted, and nothing is matched to a file on disk.

`ResolutionStatus` records one thing only: whether the attachment is linked to a media
asset. Matching candidates, evidence and confidence are a later phase with tables of their
own, deliberately kept out of this column so a proposal can never be stored as a fact.

### Media model

The media tables exist and are empty. They describe, in order:

| Table | Holds |
| --- | --- |
| `MediaSource` | one user-selected physical media root |
| `MediaFile` | one file discovered beneath a source, by path relative to that root |
| `MediaAsset` | one unique payload, identified by its SHA-256 |
| `MediaAssetFile` | which files carry which asset — many files, one asset |

Content identity is SHA-256 and nothing else: there is no perceptual or fuzzy hashing, so
two files are one asset only when their bytes match. Hash columns are compared without
regard to hexadecimal letter case, so one payload cannot become two assets through casing
alone.

## Media inventory

The codebase can inventory a physical media directory, hash what it finds, and deduplicate
it by content. Three things happen, deliberately kept apart:

**Discovery** walks a chosen source root recursively and records one `MediaFile` row per
physical file, by path relative to that root. It reads directory metadata only — no file
is opened — so it is cheap enough to be atomic: the `MediaSource` row and every one of its
`MediaFile` rows are written in a single transaction, and a failure leaves no source rather
than a source missing some of its files. Reparse points are not followed, so a junction
inside a root cannot quietly extend the walk to somewhere else on the disk, and an
unreadable directory stops the walk rather than being skipped silently. Hidden and system
files are inventoried like any others. A root that overlaps one already registered — the
same directory, or one inside the other — is refused, so a file cannot be recorded twice
under two sources.

Each file's extension is stored lower-cased, its broad media type is derived from that
extension, and two further pieces of evidence are read where the source layout is one this
build knows: whether a whole directory in its path is `Sent`, and whether its name carries
the `-YYYYMMDD-WA` convention with a valid calendar date. Both are recorded as evidence for
a later phase and neither is used to attach anything to a message. For a layout with no
known conventions they are null rather than guessed. A file with no bytes is classified
`Unknown` whatever its extension claims.

**Hashing** is separate and resumable, because the work is large. Files are streamed
through SHA-256 a chunk at a time, so a multi-gigabyte video is never held in memory, and
progress is committed in bounded batches — what a run has committed survives whatever ends
it. Resuming needs no bookmark: a file is pending precisely when its hash is null, so the
database itself records what is left. Running it again when everything is hashed does
nothing. Cancellation is an ordinary outcome rather than an error, and returns the run's
totals so far.

Source files are read-only throughout, and opened in a way that prevents anything else
modifying or deleting them mid-read. A file that has gone missing or changed size since it
was inventoried is reported and left alone rather than re-measured, because quietly
updating the record would erase the evidence that the source changed at all.

**Deduplication** is exact-content only. A file whose hash is already known is linked to
the existing `MediaAsset` instead of creating a second one, so one payload is one asset
however many copies of it an archive holds — including copies in different sources. Where
identical bytes appear under extensions meaning different things, neither classification is
chosen: the conflict is reported and the second file is left unhashed, because guessing
would record a decision no evidence supports.

No real media inventory has been committed to a workspace at this point. The services and
their tests exist; running them over a real archive is a separate, deliberate step.

### Not implemented

Duration, width and height are never populated: no media metadata is extracted, and no
image or video library is referenced. Nothing matches media to messages — attachments stay
unresolved, and file dates, direction and file names are preserved purely as evidence for a
matching phase that does not exist yet. Missing is better than wrong.

### Schema versions

A database with no workspace schema is created as the current version outright. A
version-1 workspace is migrated to version 2 in one transaction, which adds the five new
tables and derives one unresolved attachment from each already-stored message whose
content is exactly the media placeholder. Version-1 rows are never rewritten. Both paths
apply the same version-2 statements, so a migrated workspace and a fresh one are the same
database.

Importing requires a workspace already at the current version. It does not create or
migrate one: that is `WorkspaceDatabase.Initialise`'s responsibility, and keeping the two
apart is what prevents an import from silently writing rows into an older schema that
never applied the current version's rules.

### Timestamps

The two kinds of timestamp a workspace holds are stored differently on purpose, because
they mean different things.

A message timestamp is a local wall-clock reading, exactly as the export wrote it. It is
stored as `2026-01-05T14:03:00` — no `Z`, no offset, no inferred conversion. The timezone
an export was produced in is optional separate metadata recorded against the import
source, stored as supplied and never applied to a message. Nothing derives a UTC instant
for a message, and no historical offset or daylight-saving transition is calculated.

Workspace metadata — when an import was performed, when a conversation record was created
— is a real instant and is stored as round-trippable UTC.

## Test data

Only synthetic test data appears in this repository. The fixtures use fictional
participants and fictional content. No real chat exports and no real media are
included, and none should ever be committed.

## Building

Requires the .NET 10 SDK. The desktop project targets Windows.

```powershell
dotnet build ".\Chatcheology.slnx"
dotnet test ".\Chatcheology.slnx"
```

## Licence

Chatcheology is intended to be an open-source public project. A licence has not yet
been selected.

## Disclaimer

Chatcheology is not affiliated with, endorsed by or sponsored by WhatsApp or Meta.
WhatsApp is a trademark of its respective owner. Chatcheology reads chat data that
users have already exported themselves.
