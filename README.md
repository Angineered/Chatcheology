# Chatcheology

Chatcheology is an offline-first .NET application for importing, reconstructing and
preserving exported chat histories.

The first planned source is WhatsApp chat exports, but the architecture avoids
unnecessary WhatsApp-specific assumptions where practical.

## Development status

**Early foundation.** The repository currently contains the solution structure, a
synthetic test fixture, and a text parser for one pinned WhatsApp Android export
layout.

No media matching, database, archive or user interface functionality is implemented
yet. Parser output is returned in memory only; nothing is persisted.

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
- A SQLite working database (`workspace.db`) holding reconstruction state.
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
| `Chatcheology.Desktop` | WPF desktop application (.NET 10, Windows). Minimal shell. |
| `Chatcheology.Core.Tests` | xUnit tests for the core library. |

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

Not supported yet: system messages that carry no sender (such as the end-to-end
encryption notice), exports containing invisible text-direction markers, iOS layouts,
12-hour clocks, timestamps with seconds, and other locale variants. Media placeholders
are recognised as text only — no media file is inspected, matched or attached.

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
