# Chatcheology

Chatcheology is an offline-first .NET application for importing, reconstructing and
preserving exported chat histories.

The first planned source is WhatsApp chat exports, but the architecture avoids
unnecessary WhatsApp-specific assumptions where practical.

## Development status

**Early foundation.** The repository currently contains the solution structure, a
synthetic test fixture, a text parser for one pinned WhatsApp Android export layout,
and the first version of the workspace SQLite database.

No media matching, archive generation or user interface functionality is implemented
yet. The desktop application does not use the database, and no real chat archive has
been imported into SQLite.

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
- A SQLite working database holding reconstruction state. Schema version 1 exists; see
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
| `Chatcheology.Data` | Workspace SQLite persistence: schema and conversation import. |
| `Chatcheology.Desktop` | WPF desktop application (.NET 10, Windows). Minimal shell. |
| `Chatcheology.Core.Tests` | xUnit tests for the core library. |
| `Chatcheology.Data.Tests` | xUnit tests for the workspace database. |

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
"system". Media placeholders are recognised as text only — no media file is inspected,
matched or attached.

## Workspace database

Parsed messages can be persisted into a workspace SQLite database. The schema version is
recorded in SQLite's own `PRAGMA user_version` and is currently version 1, which holds:

- the import source the messages came from
- the conversation
- its participants, and their membership of that conversation
- the messages

Creating the schema and importing a conversation are each a single transaction, so a
failure leaves neither a partially created workspace nor a partially imported
conversation. Foreign keys are enforced on every connection, and a message's sender must
be a participant of the same conversation the message belongs to.

Not implemented yet: attachment and media tables, media matching, archive generation, and
any user interface for import. The desktop application does not consume the database, and
the caller always supplies the workspace file path — no location is assumed.

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
