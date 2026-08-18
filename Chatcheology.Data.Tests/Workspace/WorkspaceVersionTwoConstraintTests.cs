using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests that the version-2 tables enforce Chatcheology's own rules about attachments and media.
    /// </summary>
    /// <remarks>
    /// These exercise the project's invariants, not SQLite's behaviour in general: that an attachment
    /// cannot claim to be resolved without an asset, that one physical file cannot have two content
    /// identities, that a hash written in either case is the same hash. Rows are inserted with direct
    /// SQL because no media persistence API exists yet and inventing one to test a constraint would
    /// be building the wrong thing first.
    /// </remarks>
    public class WorkspaceVersionTwoConstraintTests
    {
        private const string HashA =
            "AAAA000000000000000000000000000000000000000000000000000000000001";

        private const string HashB =
            "BBBB000000000000000000000000000000000000000000000000000000000002";

        // ---------------------------------------------------------------------------------------
        // Attachment.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Attachment_DuplicateMessageAndOrdinal_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = InitialiseWithOneMessage(workspace, out var messageID);

            InsertUnresolvedAttachment(connection, messageID, ordinal: 1);

            AssertRejected(connection, InsertUnresolvedAttachmentSql(messageID, ordinal: 1));
        }

        [Fact]
        public void Attachment_SecondOrdinalForTheSameMessage_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = InitialiseWithOneMessage(workspace, out var messageID);

            InsertUnresolvedAttachment(connection, messageID, ordinal: 1);

            // Nothing in this phase creates a second ordinal, but the schema must not be the reason
            // a message can never have two attachments: a format that marks two would otherwise
            // need a migration to represent something it already said.
            InsertUnresolvedAttachment(connection, messageID, ordinal: 2);

            Assert.Equal(2, CountRows(connection, "Attachment"));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        public void Attachment_OrdinalThatIsNotPositive_IsRejected(int ordinal)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = InitialiseWithOneMessage(workspace, out var messageID);

            AssertRejected(connection, InsertUnresolvedAttachmentSql(messageID, ordinal));
        }

        /// <remarks>
        /// The rejected values are the matching and confidence words that belong to the later
        /// evidence tables. Admitting one here would let a guess be stored as a resolution.
        /// </remarks>
        [Theory]
        [InlineData("High")]
        [InlineData("Possible")]
        [InlineData("Ambiguous")]
        [InlineData("Confirmed")]
        [InlineData("Missing")]
        [InlineData("unresolved")]
        [InlineData("")]
        public void Attachment_UnsupportedResolutionStatus_IsRejected(string resolutionStatus)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = InitialiseWithOneMessage(workspace, out var messageID);

            AssertRejected(
                connection,
                $"""
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                VALUES ({messageID}, 1, '{resolutionStatus}');
                """);
        }

        [Fact]
        public void Attachment_UnresolvedWithAnAsset_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = InitialiseWithOneMessage(workspace, out var messageID);

            var mediaAssetID = InsertMediaAsset(connection, HashA);

            AssertRejected(
                connection,
                $"""
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus, ResolvedMediaAssetID)
                VALUES ({messageID}, 1, 'Unresolved', {mediaAssetID});
                """);
        }

        [Fact]
        public void Attachment_ResolvedWithoutAnAsset_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = InitialiseWithOneMessage(workspace, out var messageID);

            AssertRejected(
                connection,
                $"""
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus, ResolvedMediaAssetID)
                VALUES ({messageID}, 1, 'Resolved', NULL);
                """);
        }

        [Fact]
        public void Attachment_ResolvedWithAnAsset_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = InitialiseWithOneMessage(workspace, out var messageID);

            var mediaAssetID = InsertMediaAsset(connection, HashA);

            // Nothing resolves an attachment in this phase. The schema still has to be able to hold
            // the resolved state, or the phase that does the resolving would start by changing it.
            Execute(
                connection,
                $"""
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus, ResolvedMediaAssetID)
                VALUES ({messageID}, 1, 'Resolved', {mediaAssetID});
                """);

            Assert.Equal(1, CountRows(connection, "Attachment"));
        }

        [Fact]
        public void Attachment_MessageThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = InitialiseWithOneMessage(workspace, out var messageID);

            AssertRejected(connection, InsertUnresolvedAttachmentSql(messageID + 1, ordinal: 1));
        }

        [Fact]
        public void Attachment_AssetThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = InitialiseWithOneMessage(workspace, out var messageID);

            AssertRejected(
                connection,
                $"""
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus, ResolvedMediaAssetID)
                VALUES ({messageID}, 1, 'Resolved', 1);
                """);
        }

        // ---------------------------------------------------------------------------------------
        // MediaAsset.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void MediaAsset_DuplicateHash_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            InsertMediaAsset(connection, HashA);

            AssertRejected(connection, InsertMediaAssetSql(HashA));
        }

        /// <remarks>
        /// The same content hashed twice must be one asset however the hex happened to be cased. The
        /// column's NOCASE collation is what makes that a guarantee of the database rather than a
        /// convention the calling code has to remember.
        /// </remarks>
        [Fact]
        public void MediaAsset_HashDifferingOnlyByLetterCase_IsRejectedAsADuplicate()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            InsertMediaAsset(connection, HashA);

            AssertRejected(connection, InsertMediaAssetSql(HashA.ToLowerInvariant()));

            Assert.Equal(1, CountRows(connection, "MediaAsset"));
        }

        [Fact]
        public void MediaAsset_HashDifferingOnlyByLetterCase_FindsTheSameRow()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaAssetID = InsertMediaAsset(connection, HashA);

            var found = ScalarLong(
                connection,
                $"SELECT MediaAssetID FROM MediaAsset WHERE SHA256 = '{HashA.ToLowerInvariant()}';");

            Assert.Equal(mediaAssetID, found);
        }

        [Theory]
        [InlineData("")]
        [InlineData("AAAA")]
        [InlineData("AAAA00000000000000000000000000000000000000000000000000000000000")]
        [InlineData("AAAA0000000000000000000000000000000000000000000000000000000000001")]
        public void MediaAsset_HashOfTheWrongLength_IsRejected(string sha256)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            AssertRejected(connection, InsertMediaAssetSql(sha256));
        }

        [Fact]
        public void MediaAsset_NegativeSize_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            AssertRejected(connection, InsertMediaAssetSql(HashA, sizeBytes: -1));
        }

        [Fact]
        public void MediaAsset_ZeroSize_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            // A zero-byte file is a real thing to find on a recovered phone copy, and refusing to
            // record one would lose the fact that it was there.
            Execute(connection, InsertMediaAssetSql(HashA, sizeBytes: 0));

            Assert.Equal(1, CountRows(connection, "MediaAsset"));
        }

        [Theory]
        [InlineData("DurationMS", -1)]
        [InlineData("Width", 0)]
        [InlineData("Width", -1)]
        [InlineData("Height", 0)]
        [InlineData("Height", -1)]
        public void MediaAsset_ImpossibleDimensionOrDuration_IsRejected(string column, int value)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            AssertRejected(
                connection,
                $"""
                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes, {column})
                VALUES ('{HashA}', 'Image', 1, {value});
                """);
        }

        // ---------------------------------------------------------------------------------------
        // MediaFile.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void MediaFile_DuplicatePathUnderTheSameSource_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);

            InsertMediaFile(connection, mediaSourceID, "Images/one.jpg");

            AssertRejected(connection, InsertMediaFileSql(mediaSourceID, "Images/one.jpg"));
        }

        [Fact]
        public void MediaFile_TheSamePathUnderADifferentSource_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var first = InsertMediaSource(connection, "First");
            var second = InsertMediaSource(connection, "Second");

            InsertMediaFile(connection, first, "Images/one.jpg");
            InsertMediaFile(connection, second, "Images/one.jpg");

            // Two phone copies legitimately contain the same relative path. They are two files until
            // hashing says otherwise.
            Assert.Equal(2, CountRows(connection, "MediaFile"));
        }

        [Fact]
        public void MediaFile_NegativeSize_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);

            AssertRejected(
                connection,
                InsertMediaFileSql(mediaSourceID, "Images/one.jpg", sizeBytes: -1));
        }

        [Theory]
        [InlineData("")]
        [InlineData("AAAA")]
        [InlineData("AAAA00000000000000000000000000000000000000000000000000000000000")]
        public void MediaFile_NonNullHashOfTheWrongLength_IsRejected(string sha256)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);

            AssertRejected(
                connection,
                $"""
                INSERT INTO MediaFile (MediaSourceID, RelativePath, FileName, SizeBytes, MediaType, SHA256)
                VALUES ({mediaSourceID}, 'Images/one.jpg', 'one.jpg', 1, 'Image', '{sha256}');
                """);
        }

        /// <remarks>
        /// Discovery legitimately runs before hashing, so an inventoried file with no known hash has
        /// to be representable.
        /// </remarks>
        [Fact]
        public void MediaFile_NullHash_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);

            InsertMediaFile(connection, mediaSourceID, "Images/one.jpg");

            Assert.Equal(
                1,
                ScalarLong(connection, "SELECT COUNT(*) FROM MediaFile WHERE SHA256 IS NULL;"));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(-1)]
        public void MediaFile_IsSentOutsideItsThreeStates_IsRejected(int isSent)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);

            AssertRejected(
                connection,
                $"""
                INSERT INTO MediaFile (MediaSourceID, RelativePath, FileName, SizeBytes, MediaType, IsSent)
                VALUES ({mediaSourceID}, 'Images/one.jpg', 'one.jpg', 1, 'Image', {isSent});
                """);
        }

        [Theory]
        [InlineData("0")]
        [InlineData("1")]
        [InlineData("NULL")]
        public void MediaFile_EachIsSentState_IsAllowed(string isSent)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);

            Execute(
                connection,
                $"""
                INSERT INTO MediaFile (MediaSourceID, RelativePath, FileName, SizeBytes, MediaType, IsSent)
                VALUES ({mediaSourceID}, 'Images/one.jpg', 'one.jpg', 1, 'Image', {isSent});
                """);

            Assert.Equal(1, CountRows(connection, "MediaFile"));
        }

        [Theory]
        [InlineData("DurationMS", -1)]
        [InlineData("Width", 0)]
        [InlineData("Width", -1)]
        [InlineData("Height", 0)]
        [InlineData("Height", -1)]
        public void MediaFile_ImpossibleDimensionOrDuration_IsRejected(string column, int value)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);

            AssertRejected(
                connection,
                $"""
                INSERT INTO MediaFile (MediaSourceID, RelativePath, FileName, SizeBytes, MediaType, {column})
                VALUES ({mediaSourceID}, 'Images/one.jpg', 'one.jpg', 1, 'Image', {value});
                """);
        }

        [Fact]
        public void MediaFile_SourceThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            AssertRejected(connection, InsertMediaFileSql(mediaSourceID: 1, "Images/one.jpg"));
        }

        // ---------------------------------------------------------------------------------------
        // MediaAssetFile.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void MediaAssetFile_OneFileLinkedToTwoAssets_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);
            var mediaFileID = InsertMediaFile(connection, mediaSourceID, "Images/one.jpg");

            var first = InsertMediaAsset(connection, HashA);
            var second = InsertMediaAsset(connection, HashB);

            Execute(connection, InsertMediaAssetFileSql(first, mediaFileID));

            // One physical file has one content identity. Two would mean the same bytes hashed to
            // two different values.
            AssertRejected(connection, InsertMediaAssetFileSql(second, mediaFileID));
        }

        [Fact]
        public void MediaAssetFile_ManyFilesLinkedToOneAsset_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);
            var mediaAssetID = InsertMediaAsset(connection, HashA);

            foreach (var relativePath in new[] { "Images/one.jpg", "Images/Sent/one.jpg" })
            {
                var mediaFileID = InsertMediaFile(connection, mediaSourceID, relativePath);

                Execute(connection, InsertMediaAssetFileSql(mediaAssetID, mediaFileID));
            }

            // This is deduplication: the same payload found in two places on the phone.
            Assert.Equal(2, CountRows(connection, "MediaAssetFile"));
            Assert.Equal(1, CountRows(connection, "MediaAsset"));
        }

        [Fact]
        public void MediaAssetFile_AssetThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaSourceID = InsertMediaSource(connection);
            var mediaFileID = InsertMediaFile(connection, mediaSourceID, "Images/one.jpg");

            AssertRejected(connection, InsertMediaAssetFileSql(mediaAssetID: 1, mediaFileID));
        }

        [Fact]
        public void MediaAssetFile_FileThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Initialise(workspace);

            var mediaAssetID = InsertMediaAsset(connection, HashA);

            AssertRejected(connection, InsertMediaAssetFileSql(mediaAssetID, mediaFileID: 1));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static SqliteConnection Initialise(TemporaryWorkspaceDatabase workspace)
        {
            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            return WorkspaceDatabase.OpenConnection(workspace.DatabasePath);
        }

        /// <summary>
        /// Initialises a workspace and gives it the one conversation, participant and message an
        /// attachment needs a parent for.
        /// </summary>
        private static SqliteConnection InitialiseWithOneMessage(
            TemporaryWorkspaceDatabase workspace,
            out long messageID)
        {
            var connection = Initialise(workspace);

            Execute(
                connection,
                $"""
                INSERT INTO ImportSource (SourceType, DisplayName, ImportedDateTimeUtc)
                VALUES ('{SourceType}', '{SourceDisplayName}', '{ImportedDateTimeUtcText}');

                INSERT INTO Conversation (Title, CreatedDateTimeUtc)
                VALUES ('{ConversationTitle}', '{ImportedDateTimeUtcText}');

                INSERT INTO Participant (DisplayName) VALUES ('Alex');

                INSERT INTO ConversationParticipant (ConversationID, ParticipantID) VALUES (1, 1);

                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (
                    1, 1, 1, '{MessageDateTimeText}', 1, 'User', 'Hi Sam',
                    '2026/01/05, 14:03 - Alex: Hi Sam', 1, 1);
                """);

            messageID = ScalarLong(connection, "SELECT MessageID FROM Message;");

            return connection;
        }

        private static string InsertUnresolvedAttachmentSql(long messageID, int ordinal) =>
            $"""
            INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
            VALUES ({messageID}, {ordinal}, 'Unresolved');
            """;

        private static void InsertUnresolvedAttachment(
            SqliteConnection connection,
            long messageID,
            int ordinal) =>
            Execute(connection, InsertUnresolvedAttachmentSql(messageID, ordinal));

        private static string InsertMediaSourceSql(string displayName) =>
            $"""
            INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
            VALUES ('{displayName}', 'PhoneCopy', 'MediaRoot', '{ImportedDateTimeUtcText}')
            RETURNING MediaSourceID;
            """;

        /// <remarks>
        /// The root path is a fictional relative name. Nothing in this phase reads it, and no real
        /// path belongs in a test.
        /// </remarks>
        private static long InsertMediaSource(
            SqliteConnection connection,
            string displayName = "Phone copy") =>
            ScalarLong(connection, InsertMediaSourceSql(displayName));

        private static string InsertMediaAssetSql(string sha256, long sizeBytes = 1) =>
            $"""
            INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes)
            VALUES ('{sha256}', 'Image', {sizeBytes})
            RETURNING MediaAssetID;
            """;

        private static long InsertMediaAsset(SqliteConnection connection, string sha256) =>
            ScalarLong(connection, InsertMediaAssetSql(sha256));

        private static string InsertMediaFileSql(
            long mediaSourceID,
            string relativePath,
            long sizeBytes = 1) =>
            $"""
            INSERT INTO MediaFile (MediaSourceID, RelativePath, FileName, SizeBytes, MediaType)
            VALUES ({mediaSourceID}, '{relativePath}', 'one.jpg', {sizeBytes}, 'Image')
            RETURNING MediaFileID;
            """;

        private static long InsertMediaFile(
            SqliteConnection connection,
            long mediaSourceID,
            string relativePath) =>
            ScalarLong(connection, InsertMediaFileSql(mediaSourceID, relativePath));

        private static string InsertMediaAssetFileSql(long mediaAssetID, long mediaFileID) =>
            $"""
            INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
            VALUES ({mediaAssetID}, {mediaFileID});
            """;

        private static void AssertRejected(SqliteConnection connection, string sql) =>
            Assert.Throws<SqliteException>(() => Execute(connection, sql));
    }
}
