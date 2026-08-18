using System.Globalization;
using Chatcheology.Core.Models;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests the importer: what it stores, what it refuses, and what it leaves behind when it fails.
    /// </summary>
    public class WorkspaceImporterTests
    {
        [Fact]
        public void Import_ReturnsTheIdentifiersItCreated()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var result = Import(workspace, [CreateUserMessage(1)]);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(
                result.ImportSourceID,
                ScalarLong(connection, "SELECT ImportSourceID FROM ImportSource;"));
            Assert.Equal(
                result.ConversationID,
                ScalarLong(connection, "SELECT ConversationID FROM Conversation;"));
            Assert.Equal(1, result.ParticipantCount);
            Assert.Equal(1, result.MessageCount);
        }

        // ---------------------------------------------------------------------------------------
        // Source message timestamps: local wall-clock, never converted.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Import_StoresTheSourceTimestampAsLocalWallClockTextEvenWithASourceTimeZone()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(
                workspace,
                [CreateUserMessage(1, messageDateTime: new DateTime(2026, 1, 5, 14, 3, 0))],
                sourceTimeZoneID: SourceTimeZoneID);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var stored = ScalarRequiredText(connection, "SELECT MessageDateTimeLocal FROM Message;");

            Assert.Equal("2026-01-05T14:03:00", stored);

            // Supplying a source timezone must not attach or infer one here.
            Assert.DoesNotContain("Z", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("+02:00", stored, StringComparison.Ordinal);
            Assert.DoesNotContain("+", stored, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData(DateTimeKind.Utc)]
        [InlineData(DateTimeKind.Local)]
        public void Import_MessageTimestampThatIsNotUnspecified_IsRejected(DateTimeKind kind)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var message = CreateUserMessage(
                1, messageDateTime: new DateTime(2026, 1, 5, 14, 3, 0, kind));

            var exception = Assert.Throws<ArgumentException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest([message])));

            Assert.Contains(kind.ToString(), exception.Message, StringComparison.Ordinal);

            // The rejection happens before anything is written.
            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);
            AssertWorkspaceIsEmpty(connection);
        }

        // ---------------------------------------------------------------------------------------
        // Workspace metadata timestamps: real UTC instants.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Import_StoresWorkspaceMetadataAsRoundTrippableUtcText()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(workspace, [CreateUserMessage(1)]);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            var importedDateTimeUtc =
                ScalarRequiredText(connection, "SELECT ImportedDateTimeUtc FROM ImportSource;");
            var createdDateTimeUtc =
                ScalarRequiredText(connection, "SELECT CreatedDateTimeUtc FROM Conversation;");

            Assert.Equal(ImportedDateTimeUtcText, importedDateTimeUtc);

            // One import operation, one instant: the importer never calls UtcNow of its own.
            Assert.Equal(ImportedDateTimeUtcText, createdDateTimeUtc);

            foreach (var stored in new[] { importedDateTimeUtc, createdDateTimeUtc })
            {
                var roundTripped = DateTime.Parse(
                    stored,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);

                Assert.Equal(DateTimeKind.Utc, roundTripped.Kind);
                Assert.Equal(ImportedDateTimeUtc, roundTripped);
            }
        }

        [Theory]
        [InlineData(DateTimeKind.Unspecified)]
        [InlineData(DateTimeKind.Local)]
        public void Import_ImportedDateTimeThatIsNotUtc_IsRejected(DateTimeKind kind)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var request = CreateRequest(
                [CreateUserMessage(1)],
                importedDateTimeUtc: new DateTime(2026, 8, 17, 16, 0, 0, kind));

            var exception = Assert.Throws<ArgumentException>(
                () => new WorkspaceImporter().Import(workspace.DatabasePath, request));

            Assert.Contains(kind.ToString(), exception.Message, StringComparison.Ordinal);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);
            AssertWorkspaceIsEmpty(connection);
        }

        // ---------------------------------------------------------------------------------------
        // Source timezone metadata.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("Africa/Johannesburg")]
        [InlineData("Not/AReal_Timezone")]
        public void Import_StoresTheSourceTimeZoneExactlyAsSuppliedIncludingNull(
            string? sourceTimeZoneID)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(workspace, [CreateUserMessage(1)], sourceTimeZoneID: sourceTimeZoneID);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            // Deliberately not validated or resolved in this phase, so even an unresolvable
            // identifier is recorded rather than rejected.
            Assert.Equal(
                sourceTimeZoneID,
                ScalarText(connection, "SELECT SourceTimeZoneID FROM ImportSource;"));
        }

        // ---------------------------------------------------------------------------------------
        // Sequence numbers come from the parser, not from the importer.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The supplied numbers deliberately do not start at 1 or match their position in the list,
        /// so a renumbering importer would fail this.
        /// </remarks>
        [Fact]
        public void Import_PersistsTheSuppliedSequenceNumbersVerbatim()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(
                workspace,
                [CreateUserMessage(5), CreateUserMessage(6), CreateUserMessage(9)]);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(
                new long[] { 5, 6, 9 },
                ReadColumn(connection, "SELECT SequenceNumber FROM Message ORDER BY MessageID;"));
        }

        // ---------------------------------------------------------------------------------------
        // Participants.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Import_CreatesOneParticipantPerDistinctSenderAndLinksEachToTheConversation()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var result = Import(
                workspace,
                [
                    CreateUserMessage(1, sender: "Alex"),
                    CreateUserMessage(2, sender: "Sam"),
                    CreateUserMessage(3, sender: "Alex"),
                ]);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(2, result.ParticipantCount);
            Assert.Equal(2, CountRows(connection, "Participant"));
            Assert.Equal(2, CountRows(connection, "ConversationParticipant"));

            Assert.Equal(
                2,
                ScalarLong(
                    connection,
                    $"SELECT COUNT(*) FROM ConversationParticipant " +
                    $"WHERE ConversationID = {result.ConversationID};"));
        }

        /// <remarks>
        /// Senders are compared ordinally, so names differing only in case, or by an invisible
        /// character, stay distinct rather than being merged on a guess.
        /// </remarks>
        [Theory]
        [InlineData("Alex", "alex")]
        [InlineData("Alex", "Alex ")]
        [InlineData("Alex", "Ale\u200Bx")]
        public void Import_SendersDifferingOnlySubtly_RemainDistinctParticipants(
            string first,
            string second)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(
                workspace,
                [CreateUserMessage(1, sender: first), CreateUserMessage(2, sender: second)]);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(2, CountRows(connection, "Participant"));

            Assert.Equal(
                new[] { first, second },
                ReadTextColumn(connection, "SELECT DisplayName FROM Participant ORDER BY ParticipantID;"));
        }

        [Fact]
        public void Import_SystemMessage_StoresNoSenderAndCreatesNoParticipant()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(workspace, [CreateSystemMessage(1)]);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal("System", ScalarText(connection, "SELECT MessageType FROM Message;"));

            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM Message WHERE SenderParticipantID IS NULL;"));

            Assert.Equal(0, CountRows(connection, "Participant"));
            Assert.Equal(0, CountRows(connection, "ConversationParticipant"));
        }

        [Fact]
        public void Import_UserMessageWithoutASender_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var message = new ParsedMessage
            {
                SequenceNumber = 1,
                MessageDateTime = MessageDateTime,
                MessageType = MessageType.User,
                Sender = null,
                MessageContent = "Hi Sam",
                RawContent = "2026/01/05, 14:03 - : Hi Sam",
                SourceLineStart = 1,
                SourceLineEnd = 1,
            };

            Assert.Throws<ArgumentException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest([message])));
        }

        [Fact]
        public void Import_SystemMessageCarryingASender_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var message = CreateSystemMessage(1, sender: "Alex");

            Assert.Throws<ArgumentException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest([message])));
        }

        /// <remarks>
        /// The parser still returns an empty collection for an empty or whitespace-only export, as
        /// documented. It is importing that result which is refused, so a workspace never gains a
        /// conversation and an import source with nothing in them.
        /// </remarks>
        [Fact]
        public void Import_WithNoMessages_IsRejectedAndCreatesNothing()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            Assert.Throws<ArgumentException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest([])));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);
            AssertWorkspaceIsEmpty(connection);
        }

        // ---------------------------------------------------------------------------------------
        // Import metadata guards.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("C:\\Users\\someone\\Documents\\Chat.txt")]
        [InlineData("..\\Chat.txt")]
        [InlineData("Exports/Chat.txt")]
        public void Import_OriginalFileNameCarryingPathInformation_IsRejected(string originalFileName)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var request = CreateRequest([CreateUserMessage(1)], originalFileName: originalFileName);

            Assert.Throws<ArgumentException>(
                () => new WorkspaceImporter().Import(workspace.DatabasePath, request));
        }

        [Fact]
        public void Import_OriginalFileNameMayBeNull()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(workspace, [CreateUserMessage(1)], originalFileName: null);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Null(ScalarText(connection, "SELECT OriginalFileName FROM ImportSource;"));
        }

        [Fact]
        public void Import_StoresTheImportSourceMetadataAsSupplied()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(workspace, [CreateUserMessage(1)]);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(SourceType, ScalarText(connection, "SELECT SourceType FROM ImportSource;"));
            Assert.Equal(
                SourceDisplayName, ScalarText(connection, "SELECT DisplayName FROM ImportSource;"));
            Assert.Equal(
                OriginalFileName, ScalarText(connection, "SELECT OriginalFileName FROM ImportSource;"));
            Assert.Equal(SHA256, ScalarText(connection, "SELECT SHA256 FROM ImportSource;"));
            Assert.Equal(
                ConversationTitle, ScalarText(connection, "SELECT Title FROM Conversation;"));
        }

        // ---------------------------------------------------------------------------------------
        // Atomicity.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Duplicate sequence numbers are only reachable because the importer persists what it is
        /// given rather than renumbering. The unique constraint is therefore validating the source
        /// ordering itself.
        /// </remarks>
        [Fact]
        public void Import_DuplicateSequenceNumber_RollsBackTheWholeImport()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var messages = new[]
            {
                CreateUserMessage(1, sender: "Alex"),
                CreateUserMessage(2, sender: "Sam"),
                CreateUserMessage(2, sender: "Alex"),
            };

            Assert.Throws<SqliteException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest(messages)));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            // Not just the messages: the import source, conversation and participants the attempt
            // created are gone too.
            AssertWorkspaceIsEmpty(connection);
        }

        [Fact]
        public void Import_AFailedImport_LeavesAnEarlierValidImportIntact()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var first = Import(
                workspace,
                [CreateUserMessage(1, sender: "Alex"), CreateUserMessage(2, sender: "Sam")]);

            var failing = new[] { CreateUserMessage(1, sender: "Kim"), CreateUserMessage(1, sender: "Kim") };

            Assert.Throws<SqliteException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest(failing)));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, CountRows(connection, "ImportSource"));
            Assert.Equal(1, CountRows(connection, "Conversation"));
            Assert.Equal(2, CountRows(connection, "Participant"));
            Assert.Equal(2, CountRows(connection, "ConversationParticipant"));
            Assert.Equal(2, CountRows(connection, "Message"));

            Assert.Equal(
                2,
                ScalarLong(
                    connection,
                    $"SELECT COUNT(*) FROM Message WHERE ConversationID = {first.ConversationID};"));

            // The failed attempt's participant was never committed.
            Assert.Equal(
                0,
                ScalarLong(connection, "SELECT COUNT(*) FROM Participant WHERE DisplayName = 'Kim';"));
        }

        // ---------------------------------------------------------------------------------------
        // Attachments.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Import_ExactMediaPlaceholder_CreatesOneUnresolvedAttachmentForThatMessage()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(
                workspace,
                [
                    CreateUserMessage(1, content: "Hi Sam"),
                    CreateUserMessage(2, content: ParsedMessage.MediaPlaceholderContent),
                    CreateUserMessage(3, content: "See you tomorrow"),
                ]);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, CountRows(connection, "Attachment"));

            var attached = ScalarLong(
                connection,
                """
                SELECT Message.SequenceNumber
                FROM Attachment
                JOIN Message ON Message.MessageID = Attachment.MessageID;
                """);

            Assert.Equal(2, attached);

            Assert.Equal(1, ScalarLong(connection, "SELECT Ordinal FROM Attachment;"));
            Assert.Equal(
                "Unresolved",
                ScalarRequiredText(connection, "SELECT ResolutionStatus FROM Attachment;"));
            Assert.Null(ScalarText(connection, "SELECT ExpectedMediaType FROM Attachment;"));
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    "SELECT COUNT(*) FROM Attachment WHERE ResolvedMediaAssetID IS NULL;"));
        }

        /// <remarks>
        /// The importer asks <see cref="ParsedMessage.IsMediaPlaceholder"/> rather than deciding for
        /// itself, so the forms the parser refuses to call a placeholder create no attachment here
        /// either: the placeholder with a caption, and the placeholder inside longer text.
        /// </remarks>
        [Theory]
        [InlineData("see this <Media omitted> please")]
        [InlineData("<Media omitted>\na caption")]
        [InlineData("<media omitted>")]
        [InlineData("Hi Sam")]
        public void Import_ContentThatIsNotExactlyAPlaceholder_CreatesNoAttachment(string content)
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Import(workspace, [CreateUserMessage(1, content: content)]);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, CountRows(connection, "Message"));
            Assert.Equal(0, CountRows(connection, "Attachment"));
        }

        [Fact]
        public void Import_ManyPlaceholders_CreatesOneAttachmentEach()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            var messages = new[]
            {
                CreateUserMessage(1, content: ParsedMessage.MediaPlaceholderContent),
                CreateUserMessage(2, content: "Hi Sam"),
                CreateUserMessage(3, content: ParsedMessage.MediaPlaceholderContent),
                CreateUserMessage(4, content: ParsedMessage.MediaPlaceholderContent),
            };

            Import(workspace, messages);

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(3, CountRows(connection, "Attachment"));

            // One attachment per placeholder message, and each pointing at its own message.
            Assert.Equal(
                3,
                ScalarLong(connection, "SELECT COUNT(DISTINCT MessageID) FROM Attachment;"));

            Assert.Equal(
                new List<long> { 1, 3, 4 },
                ReadColumn(
                    connection,
                    """
                    SELECT Message.SequenceNumber
                    FROM Attachment
                    JOIN Message ON Message.MessageID = Attachment.MessageID
                    ORDER BY Message.SequenceNumber;
                    """));
        }

        /// <remarks>
        /// The attachment rows must belong to the same transaction as the messages, not follow it. A
        /// duplicate sequence number later in the same import fails the message insert after an
        /// attachment has already been written, which is the only ordering that can tell the two
        /// cases apart: if attachments were committed separately, this one would survive.
        /// <para>
        /// Reached with valid public input and no fault injection — the importer stores what it is
        /// given rather than renumbering, so a source with a repeated sequence number is a real thing
        /// it can be asked to store and refuse.
        /// </para>
        /// </remarks>
        [Fact]
        public void Import_FailureAfterAnAttachmentWasWritten_RollsBackTheAttachmentToo()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var messages = new[]
            {
                CreateUserMessage(1, sender: "Alex", content: ParsedMessage.MediaPlaceholderContent),
                CreateUserMessage(2, sender: "Sam"),
                CreateUserMessage(2, sender: "Alex"),
            };

            Assert.Throws<SqliteException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest(messages)));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            AssertWorkspaceIsEmpty(connection);
        }

        // ---------------------------------------------------------------------------------------
        // Schema-version guard.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The importer opens the workspace connection with <c>ReadWriteCreate</c>, so without an
        /// existence check it would create an empty database purely in order to reject it.
        /// </remarks>
        [Fact]
        public void Import_DatabaseThatDoesNotExist_IsRejectedWithoutCreatingAFile()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            Assert.Throws<FileNotFoundException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest([CreateUserMessage(1)])));

            SqliteConnection.ClearAllPools();

            Assert.False(File.Exists(workspace.DatabasePath));
            Assert.Empty(Directory.GetFiles(workspace.DirectoryPath));
        }

        [Fact]
        public void Import_DatabaseWithNoWorkspaceSchema_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            // An existing file that has never been initialised: user_version 0, no tables.
            using (var setup = WorkspaceDatabase.OpenConnection(workspace.DatabasePath))
            {
                Execute(setup, "CREATE TABLE Unrelated (Value TEXT NOT NULL);");
            }

            Assert.Throws<InvalidOperationException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest([CreateUserMessage(1)])));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(0, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.False(TableExists(connection, "Message"));
        }

        /// <remarks>
        /// The case the guard exists for. This import contains no media placeholder at all, so
        /// without the version check it would succeed against the five-table version-1 schema and
        /// leave messages behind that never passed through version 2's attachment behaviour.
        /// </remarks>
        [Fact]
        public void Import_GenuineVersionOneDatabase_IsRejectedWithoutWritingAnything()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            SyntheticVersionOneWorkspace.Create(workspace.DatabasePath);

            var messageCountBefore = SyntheticVersionOneWorkspace.MessageCount;

            Assert.Throws<InvalidOperationException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest([CreateUserMessage(99)])));

            using var connection = SyntheticVersionOneWorkspace.OpenPlainConnection(
                workspace.DatabasePath);

            Assert.Equal(1, WorkspaceDatabase.ReadSchemaVersion(connection));

            Assert.Equal(1, CountRows(connection, "ImportSource"));
            Assert.Equal(1, CountRows(connection, "Conversation"));
            Assert.Equal(messageCountBefore, CountRows(connection, "Message"));

            // Not migrated as a side effect of the attempt, either.
            Assert.False(TableExists(connection, "Attachment"));
        }

        [Fact]
        public void Import_DatabaseFromAnUnsupportedFutureVersion_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            using (var setup = WorkspaceDatabase.OpenConnection(workspace.DatabasePath))
            {
                Execute(setup, "PRAGMA user_version = 3;");
            }

            Assert.Throws<InvalidOperationException>(() => new WorkspaceImporter()
                .Import(workspace.DatabasePath, CreateRequest([CreateUserMessage(1)])));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(3, WorkspaceDatabase.ReadSchemaVersion(connection));
            Assert.Equal(0, CountRows(connection, "Message"));
        }

        [Fact]
        public void Import_CurrentVersionDatabase_IsAccepted()
        {
            using var workspace = new TemporaryWorkspaceDatabase();

            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var result = new WorkspaceImporter().Import(
                workspace.DatabasePath,
                CreateRequest([CreateUserMessage(1, content: ParsedMessage.MediaPlaceholderContent)]));

            using var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

            Assert.Equal(1, result.MessageCount);
            Assert.Equal(1, CountRows(connection, "Message"));
            Assert.Equal(1, CountRows(connection, "Attachment"));
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static WorkspaceImportResult Import(
            TemporaryWorkspaceDatabase workspace,
            IReadOnlyList<ParsedMessage> messages,
            string? sourceTimeZoneID = SourceTimeZoneID,
            string? originalFileName = OriginalFileName)
        {
            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            return new WorkspaceImporter().Import(
                workspace.DatabasePath,
                CreateRequest(messages, sourceTimeZoneID, originalFileName));
        }

        private static void AssertWorkspaceIsEmpty(SqliteConnection connection)
        {
            Assert.Equal(0, CountRows(connection, "ImportSource"));
            Assert.Equal(0, CountRows(connection, "Conversation"));
            Assert.Equal(0, CountRows(connection, "Participant"));
            Assert.Equal(0, CountRows(connection, "ConversationParticipant"));
            Assert.Equal(0, CountRows(connection, "Message"));

            // Attachments are written by the same transaction as the messages they belong to, so a
            // rolled-back import must leave none of them either.
            Assert.Equal(0, CountRows(connection, "Attachment"));
        }

        private static List<long> ReadColumn(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();

            var values = new List<long>();

            while (reader.Read())
            {
                values.Add(reader.GetInt64(0));
            }

            return values;
        }

        private static List<string> ReadTextColumn(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            using var reader = command.ExecuteReader();

            var values = new List<string>();

            while (reader.Read())
            {
                values.Add(reader.GetString(0));
            }

            return values;
        }
    }
}
