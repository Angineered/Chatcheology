using System.Globalization;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// A synthetic workspace shaped for the Phase 7 evidence snapshot, and the calls the Phase 7 tests
    /// make against it.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="MatchingTestWorkspace"/>, because a Phase 7 run starts from the frozen
    /// matching analysis and therefore needs real messages, participants, attachments and media, and it
    /// borrows <c>SequenceTestFixture</c>'s marker-name convention because a dated copy whose name
    /// carries no marker is a deliberate failure case rather than a convenience.
    /// <para>
    /// Everything here is invented: fictional participants, fictional hashes, and names that name
    /// nothing real. No path under any recovery drive, no real file name, no real hash and no
    /// archive-specific count appears anywhere in this project.
    /// </para>
    /// <para>
    /// <see cref="Run"/> closes the building connection first, following the convention
    /// <c>WorkspaceMatchingSafetyTests</c> already establishes: a Phase 7 run hashes the workspace file
    /// before its first write, and a live handle on that file would make the hash fail on Windows. Every
    /// read after a run therefore goes through a fresh connection.
    /// </para>
    /// </remarks>
    internal sealed class Phase7TestFixture : IDisposable
    {
        /// <summary>The date most fixtures put their first message on.</summary>
        internal static readonly DateOnly FirstDate = new(2026, 3, 2);

        private const string DefaultExtension = ".jpg";

        private readonly MatchingTestWorkspace _workspace = new();
        private readonly long _sourceID;

        private int _hashNumber;

        internal Phase7TestFixture() => _sourceID = _workspace.AddMediaSource();

        internal MatchingTestWorkspace Workspace => _workspace;

        internal string DatabasePath => _workspace.DatabasePath;

        internal string DirectoryPath => _workspace.DirectoryPath;

        internal long SourceID => _sourceID;

        /// <summary>A date a fixed number of days after <see cref="FirstDate"/>.</summary>
        internal static DateOnly Day(int offset) => FirstDate.AddDays(offset);

        /// <summary>A recovered name carrying the marker for <paramref name="date"/>.</summary>
        internal static string Name(DateOnly date, string suffix, string prefix = "IMG") =>
            $"{prefix}-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-WA{suffix}";

        /// <summary>A supported four-digit suffix.</summary>
        internal static string Token(int token) =>
            token.ToString("D4", CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------------------------------
        // Building.
        // ---------------------------------------------------------------------------------------

        /// <summary>A message from the local participant, with its unresolved attachment.</summary>
        internal long AddOutgoing(DateOnly date, string time = "09:00:00") =>
            _workspace.AddMediaAttachment(
                date, MatchingTestWorkspace.LocalParticipantID, time: time);

        /// <summary>A message from the other participant, with its unresolved attachment.</summary>
        internal long AddIncoming(DateOnly date, string time = "09:05:00") =>
            _workspace.AddMediaAttachment(
                date, MatchingTestWorkspace.OtherParticipantID, time: time);

        /// <summary>
        /// A senderless system message carrying an attachment, whose direction is unknown however the
        /// local participant is supplied.
        /// </summary>
        internal long AddSenderless(DateOnly date, string time = "09:10:00") =>
            _workspace.AddMediaAttachment(date, senderParticipantID: null, time: time);

        /// <summary>An attachment in the fixture's second conversation.</summary>
        internal long AddOutgoingInOtherConversation(DateOnly date, string time = "09:15:00") =>
            _workspace.AddMediaAttachment(
                date,
                MatchingTestWorkspace.OutsiderParticipantID,
                MatchingTestWorkspace.OtherConversationID,
                time);

        /// <summary>An asset with one dated copy per supported token.</summary>
        internal long AddTokenAsset(DateOnly date, bool? isSent, params int[] tokens) =>
            AddNamedAsset(date, isSent, [.. tokens.Select(Token)]);

        /// <summary>An asset with one dated copy per suffix, supported or not.</summary>
        internal long AddNamedAsset(DateOnly date, bool? isSent, params string[] suffixes)
        {
            var sha = MatchingTestData.Hash(++_hashNumber);
            var mediaAssetID = _workspace.AddMediaAsset(sha);

            foreach (var suffix in suffixes)
            {
                AddCopy(mediaAssetID, sha, date, isSent, suffix);
            }

            return mediaAssetID;
        }

        /// <summary>An asset with dated copies on several dates, one supported token each.</summary>
        internal long AddTokenAssetOnDates(
            bool? isSent, params (DateOnly Date, int TokenValue)[] copies)
        {
            var sha = MatchingTestData.Hash(++_hashNumber);
            var mediaAssetID = _workspace.AddMediaAsset(sha);

            foreach (var (date, tokenValue) in copies)
            {
                AddCopy(mediaAssetID, sha, date, isSent, Token(tokenValue));
            }

            return mediaAssetID;
        }

        /// <summary>The hash the fixture gave the asset it added <paramref name="ordinal"/>th.</summary>
        internal static string HashOf(int ordinal) => MatchingTestData.Hash(ordinal);

        /// <summary>How many assets this fixture has added through its asset helpers.</summary>
        internal int AssetCount => _hashNumber;

        /// <summary>One more copy of an asset already in the workspace.</summary>
        internal void AddCopy(
            long mediaAssetID,
            string sha256,
            DateOnly date,
            bool? isSent,
            string suffix,
            string extension = DefaultExtension,
            long? mediaSourceID = null) =>
            _workspace.AddMediaFile(
                mediaSourceID ?? _sourceID,
                mediaAssetID,
                sha256,
                date,
                isSent,
                fileName: Name(date, suffix) + extension,
                extension: extension);

        /// <summary>A second acquisition store, for duplicate-collapse fixtures.</summary>
        internal long AddSource(string displayName = "Second synthetic source") =>
            _workspace.AddMediaSource(displayName);

        /// <summary>
        /// An asset whose only dated copy carries a marker for the wrong date — a workspace a run
        /// refuses.
        /// </summary>
        internal long AddAssetWithDisagreeingMarker(DateOnly storedDate, DateOnly markerDate)
        {
            var sha = MatchingTestData.Hash(++_hashNumber);
            var mediaAssetID = _workspace.AddMediaAsset(sha);

            _workspace.AddMediaFile(
                _sourceID,
                mediaAssetID,
                sha,
                storedDate,
                isSent: true,
                fileName: Name(markerDate, Token(1)) + DefaultExtension,
                extension: DefaultExtension);

            return mediaAssetID;
        }

        // ---------------------------------------------------------------------------------------
        // Shapes the order-annotation tests use.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// One outgoing group with <paramref name="memberCount"/> attachments and
        /// <paramref name="positionCount"/> token positions, one compatible asset per position at
        /// tokens <c>0 .. T-1</c>.
        /// </summary>
        /// <returns>
        /// The attachment identifiers in rank order, and the asset identifiers in token order.
        /// </returns>
        internal (long[] Attachments, long[] AssetsByToken) AddOutgoingGroup(
            DateOnly date, int memberCount, int positionCount)
        {
            var attachments = new long[memberCount];

            for (var member = 0; member < memberCount; member++)
            {
                attachments[member] = AddOutgoing(
                    date, time: $"09:{member.ToString("D2", CultureInfo.InvariantCulture)}:00");
            }

            var assets = new long[positionCount];

            for (var position = 0; position < positionCount; position++)
            {
                assets[position] = AddTokenAsset(date, isSent: true, position);
            }

            return (attachments, assets);
        }

        // ---------------------------------------------------------------------------------------
        // Running.
        // ---------------------------------------------------------------------------------------

        /// <summary>The request this fixture's runs use.</summary>
        internal Phase7RunRequest Request(
            long? localParticipantID = MatchingTestWorkspace.LocalParticipantID,
            long conversationID = MatchingTestWorkspace.ConversationID) =>
            new()
            {
                DatabasePath = DatabasePath,
                ConversationID = conversationID,
                LocalParticipantID = localParticipantID,
            };

        /// <summary>
        /// Closes the building connection, so the file can be hashed, copied or written to from
        /// elsewhere.
        /// </summary>
        internal void CloseBuilding() => _workspace.CloseBuildingConnection();

        /// <summary>Runs one evidence snapshot over this workspace.</summary>
        internal Phase7RunResult Run(
            long? localParticipantID = MatchingTestWorkspace.LocalParticipantID,
            long conversationID = MatchingTestWorkspace.ConversationID,
            CancellationToken cancellationToken = default)
        {
            CloseBuilding();

            return new Phase7EvidenceSnapshotService().Run(
                Request(localParticipantID, conversationID), cancellationToken);
        }

        /// <summary>One attachment's support, read back from a completed run.</summary>
        internal IReadOnlyList<Phase7CandidateSupport> Support(long runID, long attachmentID)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);

            return new Phase7SupportReader()
                .ReadAttachmentSupport(connection, runID, attachmentID);
        }

        /// <summary>One attachment's tiers, read back from a completed run.</summary>
        internal Phase7RankedCandidateSet Rank(long runID, long attachmentID)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);

            return new Phase7SupportReader()
                .ReadRankedCandidateSet(connection, runID, attachmentID);
        }

        // ---------------------------------------------------------------------------------------
        // Reading back.
        // ---------------------------------------------------------------------------------------

        /// <summary>Runs SQL through a fresh read-write connection.</summary>
        internal void ExecuteDirect(string sql)
        {
            using var connection = WorkspaceDatabase.OpenConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        /// <summary>Reads one integer through a fresh read-only connection.</summary>
        internal long ScalarLong(string sql)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        /// <summary>Reads one nullable value as text, or <c>&lt;null&gt;</c>.</summary>
        internal string ScalarText(string sql)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            var value = command.ExecuteScalar();

            return value is null || value is DBNull
                ? "<null>"
                : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "<null>";
        }

        /// <summary>
        /// Reads every row of <paramref name="sql"/> as one pipe-joined line, so a whole result set
        /// compares as text.
        /// </summary>
        internal List<string> Rows(string sql)
        {
            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(DatabasePath);
            using var command = connection.CreateCommand();
            command.CommandText = sql;

            var rows = new List<string>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var fields = new List<string>(reader.FieldCount);

                for (var field = 0; field < reader.FieldCount; field++)
                {
                    fields.Add(
                        reader.IsDBNull(field)
                            ? "<null>"
                            : Convert.ToString(reader.GetValue(field), CultureInfo.InvariantCulture)
                              ?? "<null>");
                }

                rows.Add(string.Join(",", fields));
            }

            return rows;
        }

        /// <summary>
        /// One attachment's occurrence rows for one run, as
        /// <c>MediaFileID,TokenGrammarSupported,SequenceToken,DateTokenKeyUniqueToPayload,Annotation</c>
        /// lines, ordered by candidate asset and then physical copy.
        /// </summary>
        internal List<string> Occurrences(long runID, long attachmentID) =>
            Rows(
                $"""
                SELECT c.MediaAssetID, o.MediaFileID, o.TokenGrammarSupported, o.SequenceToken,
                       o.DateTokenKeyUniqueToPayload, o.OrderConsistencyAnnotation
                FROM AttachmentCandidateOccurrenceEvidence AS o
                INNER JOIN AttachmentCandidate AS c
                    ON c.AttachmentCandidateID = o.AttachmentCandidateID
                WHERE c.RunID = {runID} AND c.AttachmentID = {attachmentID}
                ORDER BY c.MediaAssetID, o.MediaFileID;
                """);

        /// <summary>One attachment's snapshot row for one run, as one line.</summary>
        internal string Snapshot(long runID, long attachmentID) =>
            Assert.Single(
                Rows(
                    $"""
                    SELECT ExactDateCandidateCount, AdjacentDateContextCount,
                           HasAnyCompatibleAnnotatedCandidate,
                           HasAnyContradictoryOnlyAnnotatedCandidate,
                           IsWithinDirectionImpossibleGroup, IsForcedPositionGroupShape,
                           RemainingCompatiblePositionCountUnderHypothesis
                    FROM AttachmentEvidenceSnapshot
                    WHERE RunID = {runID} AND AttachmentID = {attachmentID};
                    """));

        /// <summary>The annotation of the single occurrence of one candidate in one run.</summary>
        internal OrderConsistencyAnnotation Annotation(
            long runID, long attachmentID, long mediaAssetID)
        {
            var value = ScalarText(
                $"""
                SELECT o.OrderConsistencyAnnotation
                FROM AttachmentCandidateOccurrenceEvidence AS o
                INNER JOIN AttachmentCandidate AS c
                    ON c.AttachmentCandidateID = o.AttachmentCandidateID
                WHERE c.RunID = {runID}
                  AND c.AttachmentID = {attachmentID}
                  AND c.MediaAssetID = {mediaAssetID};
                """);

            return Enum.Parse<OrderConsistencyAnnotation>(value);
        }

        /// <summary>Every evidence row the run published, rendered so two runs compare as text.</summary>
        /// <remarks>
        /// Deliberately excludes every value that legitimately differs between two runs of the same
        /// workspace: the run identifier, all four surrogate primary keys, both timestamps and the
        /// pre-run hash — the last of which necessarily differs, because the first run wrote.
        /// </remarks>
        internal string EvidenceSignature(long runID) =>
            string.Join(
                "\n",
                [
                    .. Rows(
                        $"""
                        SELECT AttachmentID, ExactDateCandidateCount, AdjacentDateContextCount,
                               HasAnyCompatibleAnnotatedCandidate,
                               HasAnyContradictoryOnlyAnnotatedCandidate,
                               IsWithinDirectionImpossibleGroup, IsForcedPositionGroupShape,
                               RemainingCompatiblePositionCountUnderHypothesis
                        FROM AttachmentEvidenceSnapshot
                        WHERE RunID = {runID}
                        ORDER BY AttachmentID;
                        """),
                    .. Rows(
                        $"""
                        SELECT AttachmentID, MediaAssetID, DirectionCompatibilityAnnotation
                        FROM AttachmentCandidate
                        WHERE RunID = {runID}
                        ORDER BY AttachmentID, MediaAssetID;
                        """),
                    .. Rows(
                        $"""
                        SELECT c.AttachmentID, c.MediaAssetID, o.MediaFileID,
                               o.TokenGrammarSupported, o.SequenceToken,
                               o.DateTokenKeyUniqueToPayload, o.OrderConsistencyAnnotation
                        FROM AttachmentCandidateOccurrenceEvidence AS o
                        INNER JOIN AttachmentCandidate AS c
                            ON c.AttachmentCandidateID = o.AttachmentCandidateID
                        WHERE c.RunID = {runID}
                        ORDER BY c.AttachmentID, c.MediaAssetID, o.MediaFileID;
                        """),
                    .. Rows(
                        $"""
                        SELECT AttachmentID, MediaAssetID, AdjacentOffsetDays
                        FROM AttachmentAdjacentDateContext
                        WHERE RunID = {runID}
                        ORDER BY AttachmentID, MediaAssetID, AdjacentOffsetDays;
                        """),
                ]);

        /// <summary>How many rows the four evidence tables hold, for any run.</summary>
        internal long TotalEvidenceRows() =>
            ScalarLong(
                """
                SELECT (SELECT COUNT(*) FROM AttachmentEvidenceSnapshot)
                     + (SELECT COUNT(*) FROM AttachmentCandidate)
                     + (SELECT COUNT(*) FROM AttachmentCandidateOccurrenceEvidence)
                     + (SELECT COUNT(*) FROM AttachmentAdjacentDateContext);
                """);

        /// <summary>How many rows the four evidence tables hold for one run.</summary>
        internal long EvidenceRowsFor(long runID) =>
            ScalarLong(
                $"""
                SELECT (SELECT COUNT(*) FROM AttachmentEvidenceSnapshot WHERE RunID = {runID})
                     + (SELECT COUNT(*) FROM AttachmentCandidate WHERE RunID = {runID})
                     + (SELECT COUNT(*) FROM AttachmentCandidateOccurrenceEvidence AS o
                        INNER JOIN AttachmentCandidate AS c
                            ON c.AttachmentCandidateID = o.AttachmentCandidateID
                        WHERE c.RunID = {runID})
                     + (SELECT COUNT(*) FROM AttachmentAdjacentDateContext WHERE RunID = {runID});
                """);

        /// <summary>The stored status of one run.</summary>
        internal string RunStatus(long runID) =>
            ScalarText($"SELECT Status FROM Phase7Run WHERE RunID = {runID};");

        /// <summary>
        /// A competing connection to this workspace with a short, explicit, non-zero busy timeout.
        /// </summary>
        /// <remarks>
        /// <strong>Never leave the timeout at its default, and never set it to zero.</strong> The
        /// provider's default is thirty seconds, so each blocking assertion would stall the suite for
        /// that long; and in this provider <c>DefaultTimeout = 0</c> means <em>wait for ever</em>, which
        /// is an ADO.NET convention rather than a fail-fast one and will hang a test run until it is
        /// killed. One second is ample for an assertion that expects <c>SQLITE_BUSY</c> immediately.
        /// <para>
        /// The precedent for building an explicit connection string in a test already exists in
        /// <c>MatchingTestWorkspace.ExecuteWithoutForeignKeys</c>.
        /// </para>
        /// </remarks>
        internal SqliteConnection OpenCompetingWriter()
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                ForeignKeys = true,
                DefaultTimeout = 1,
            }.ToString();

            var connection = new SqliteConnection(connectionString);
            connection.Open();

            return connection;
        }

        public void Dispose() => _workspace.Dispose();
    }
}
