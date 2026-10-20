namespace Chatcheology.Archive
{
    /// <summary>
    /// The <c>archive.db</c> tables of format version 1, one statement per element, in creation
    /// order.
    /// </summary>
    /// <remarks>
    /// <para>Only the result of a reconstruction is here. There is no evidence, run, candidate,
    /// disproof or review table, no media source or root path, no phone path and no raw export line;
    /// those stay in the workspace.</para>
    /// <para>The constraints restate the format's rules so a viewer can rely on them: a Restored
    /// attachment has a media file, a name and a basis; any other state has none of them. Foreign keys
    /// are deferred to the end of the writing transaction, because a low-quality copy may name a full
    /// copy that is written later.</para>
    /// <para>Indexes: <c>UNIQUE (SequenceNumber)</c> is the sequence index, and
    /// <c>UNIQUE (MessageID, Ordinal)</c> serves lookups by message, so neither needs a second one.
    /// Day jumps get their own index on <c>DateTimeLocal</c>.</para>
    /// </remarks>
    internal static class ArchiveSchema
    {
        internal static readonly string[] Statements =
        [
            """
            CREATE TABLE ArchiveInfo (
                Key   TEXT PRIMARY KEY,
                Value TEXT NOT NULL
            );
            """,

            """
            CREATE TABLE Participant (
                ParticipantID INTEGER PRIMARY KEY,
                DisplayName   TEXT NOT NULL,
                IsLocal       INTEGER NOT NULL,

                CHECK (IsLocal IN (0, 1))
            );
            """,

            """
            CREATE TABLE Message (
                MessageID           INTEGER PRIMARY KEY,
                SequenceNumber      INTEGER NOT NULL,
                DateTimeLocal       TEXT NOT NULL,
                Kind                TEXT NOT NULL,
                SenderParticipantID INTEGER NULL,
                Text                TEXT NOT NULL,
                IsForwarded         INTEGER NULL,
                ForwardScore        INTEGER NULL,

                UNIQUE (SequenceNumber),

                CHECK (SequenceNumber > 0),
                CHECK (length(DateTimeLocal) = 19),
                CHECK (Kind IN ('User', 'System')),
                CHECK (
                    (Kind = 'User' AND SenderParticipantID IS NOT NULL)
                    OR (Kind = 'System' AND SenderParticipantID IS NULL)
                ),
                CHECK (IsForwarded IS NULL OR IsForwarded IN (0, 1)),

                FOREIGN KEY (SenderParticipantID) REFERENCES Participant (ParticipantID)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,

            """
            CREATE INDEX IX_Message_DateTimeLocal ON Message (DateTimeLocal);
            """,

            """
            CREATE TABLE Media (
                Sha256     TEXT PRIMARY KEY,
                MediaType  TEXT NOT NULL,
                SizeBytes  INTEGER NOT NULL,
                DurationMS INTEGER NULL,
                Width      INTEGER NULL,
                Height     INTEGER NULL,

                CHECK (length(Sha256) = 64 AND Sha256 = lower(Sha256)),
                CHECK (SizeBytes >= 0),
                CHECK (DurationMS IS NULL OR DurationMS >= 0),
                CHECK (Width IS NULL OR Width > 0),
                CHECK (Height IS NULL OR Height > 0)
            );
            """,

            """
            CREATE TABLE Attachment (
                AttachmentID                 INTEGER PRIMARY KEY,
                MessageID                    INTEGER NOT NULL,
                Ordinal                      INTEGER NOT NULL,
                State                        TEXT NOT NULL,
                MediaSha256                  TEXT NULL,
                OriginalFileName             TEXT NULL,
                IsForwarded                  INTEGER NULL,
                ForwardScore                 INTEGER NULL,
                LowQualityCopyOfAttachmentID INTEGER NULL,

                UNIQUE (MessageID, Ordinal),

                CHECK (Ordinal > 0),
                CHECK (State IN ('Restored', 'Missing', 'Unmatched')),
                CHECK (
                    (State = 'Restored' AND MediaSha256 IS NOT NULL AND OriginalFileName IS NOT NULL)
                    OR (State <> 'Restored' AND MediaSha256 IS NULL AND OriginalFileName IS NULL)
                ),
                CHECK (IsForwarded IS NULL OR IsForwarded IN (0, 1)),
                CHECK (LowQualityCopyOfAttachmentID IS NULL
                    OR LowQualityCopyOfAttachmentID <> AttachmentID),

                FOREIGN KEY (MessageID) REFERENCES Message (MessageID)
                    DEFERRABLE INITIALLY DEFERRED,
                FOREIGN KEY (MediaSha256) REFERENCES Media (Sha256)
                    DEFERRABLE INITIALLY DEFERRED,
                FOREIGN KEY (LowQualityCopyOfAttachmentID) REFERENCES Attachment (AttachmentID)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,

            """
            CREATE TABLE AttachmentBasis (
                AttachmentID INTEGER PRIMARY KEY,
                BasisType    TEXT NOT NULL,
                BasisText    TEXT NOT NULL,
                Actor        TEXT NULL,
                ConfirmedUtc TEXT NOT NULL,

                CHECK (length(BasisType) > 0),
                CHECK (length(BasisText) > 0),
                CHECK (length(ConfirmedUtc) = 20),

                FOREIGN KEY (AttachmentID) REFERENCES Attachment (AttachmentID)
                    DEFERRABLE INITIALLY DEFERRED
            );
            """,
        ];
    }
}
