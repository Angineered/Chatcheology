using System.Globalization;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The checks that gate publication: every one runs inside the publication transaction, after the
    /// inserts and before the run is marked completed, by reading the rows back from the transaction
    /// itself.
    /// </summary>
    /// <remarks>
    /// <strong>None of these identifies anything.</strong> Counts agreeing is a statement about
    /// bookkeeping, never about whether a candidate is the attachment's media, and no check here may
    /// ever become a resolution rule. The per-candidate occurrence check proves two derivations of one
    /// <em>set size</em> agree — nothing more.
    /// <para>
    /// Every check is one set-based statement, or one grouped result set walked in step with the
    /// payload's own tallies. Never one query per attachment or per candidate: for a real conversation
    /// that would be of the order of 10^5 statements issued at the exact point where the writer
    /// reservation is held and every other writer is blocked.
    /// </para>
    /// <para>
    /// Nothing here writes. Every statement is a <c>SELECT</c>.
    /// </para>
    /// </remarks>
    internal static class Phase7RunReconciliation
    {
        /// <summary>
        /// Reads the pre-existing state that the mid-run tripwire compares against.
        /// </summary>
        /// <remarks>
        /// Taken on a read-only connection before anything read-write is opened, and read again inside
        /// the publication transaction. It covers the row counts of all ten pre-existing tables, the
        /// <c>Attachment</c> resolution distribution, and a lightweight aggregate over each of the three
        /// Stage 7.2 decision tables — which together are the state Stage 7.1 must be able to say it did
        /// not touch. The whole-row proof is a test, not a run-time check: proving it needs a checkpoint
        /// copy, which a run must not create.
        /// <para>
        /// <strong>The decision-table part is a tripwire, not a fingerprint.</strong> It is four
        /// aggregates per table — row count and the primary key's minimum, maximum and sum — so it
        /// catches an ordinary insert or delete and many renumbering patterns, but four aggregates can
        /// collide (<c>{1,2,5,6}</c> and <c>{1,3,4,6}</c> agree on all four), so it proves neither that
        /// the exact key set is unchanged nor anything at all about non-key columns. What closes those
        /// gaps for the case that matters is that Stage 7.1 has no data-modifying statement naming any
        /// of these tables, which the structural tests assert, plus the synthetic whole-row comparison
        /// against a checkpoint in the Stage 7.1/7.2 compatibility test.
        /// </para>
        /// </remarks>
        internal static Phase7PreExistingBaseline ReadBaseline(
            SqliteConnection connection, SqliteTransaction? transaction = null)
        {
            ArgumentNullException.ThrowIfNull(connection);

            var parts = new List<string>();

            foreach (var tableName in WorkspaceDatabase.VersionTwoTableNames)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                // The name is a private constant of this assembly, never caller input, and a table
                // name cannot be parameterised.
                command.CommandText = $"SELECT COUNT(*) FROM {tableName};";

                var count = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);

                parts.Add($"{tableName}={count.ToString(CultureInfo.InvariantCulture)}");
            }

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = AttachmentResolutionDistributionStatement;

                using var reader = command.ExecuteReader();

                while (reader.Read())
                {
                    parts.Add(
                        $"Attachment[{reader.GetString(0)},unlinked={reader.GetInt64(1)}]=" +
                        $"{reader.GetInt64(2).ToString(CultureInfo.InvariantCulture)}");
                }
            }

            foreach (var tableName in WorkspaceDatabase.VersionThreeDecisionTableNames)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;

                // Same justification as the ten above: both identifiers come from a canonical list in
                // this assembly, never from caller input, and neither can be parameterised. The key
                // column's name follows the schema convention that list documents.
                command.CommandText =
                    $"SELECT COUNT(*), COALESCE(MIN({tableName}ID), 0), " +
                    $"COALESCE(MAX({tableName}ID), 0), COALESCE(SUM({tableName}ID), 0) " +
                    $"FROM {tableName};";

                using var reader = command.ExecuteReader();

                reader.Read();

                parts.Add(
                    $"{tableName}[n={Text(reader.GetInt64(0))},min={Text(reader.GetInt64(1))}," +
                    $"max={Text(reader.GetInt64(2))},sum={Text(reader.GetInt64(3))}]");
            }

            return new Phase7PreExistingBaseline(string.Join("|", parts));
        }

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Runs every publication check against <paramref name="transaction"/>, throwing on the first
        /// failure.
        /// </summary>
        /// <param name="assignedCandidateIDs">
        /// The <c>AttachmentCandidateID</c> each payload candidate index was assigned, in index order.
        /// </param>
        /// <exception cref="InvalidOperationException">
        /// Some check failed. The message names the check and both values. The caller rolls the
        /// publication transaction back, so no evidence row survives.
        /// </exception>
        internal static void Verify(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long runID,
            Phase7RunRequest request,
            Phase7EvidencePayload payload,
            IReadOnlyList<long> assignedCandidateIDs,
            Phase7PreExistingBaseline baseline)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(transaction);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(payload);
            ArgumentNullException.ThrowIfNull(assignedCandidateIDs);
            ArgumentNullException.ThrowIfNull(baseline);

            VerifySnapshotCoverage(connection, transaction, runID, request, payload);
            VerifySnapshotAttachmentSet(connection, transaction, runID, payload);
            VerifyDeclaredCountsAgreeWithRows(connection, transaction, runID);
            VerifyTotals(connection, transaction, runID, payload);
            VerifyOccurrenceContainment(connection, transaction, runID, payload);
            VerifyOccurrenceCountsPerCandidate(
                connection, transaction, runID, payload, assignedCandidateIDs);
            VerifyEveryCandidateHasAnExactDateCopy(connection, transaction, runID);
            VerifyBandsAreDisjoint(connection, transaction, runID);
            VerifySummaryFlags(connection, transaction, runID);
            VerifyGroupInvariant(connection, transaction, runID, request);
            VerifyDirectionInvariant(connection, transaction, runID, request);
            VerifyOccurrenceRowShapes(connection, transaction, runID);
            VerifyTwoPassAgreement(payload);
            VerifyPreExistingDataUnchanged(connection, transaction, baseline);
        }

        /// <summary>
        /// The diagnostic for a disagreement between the two read passes.
        /// </summary>
        /// <remarks>
        /// Deliberately names <em>both</em> possibilities and asserts neither.
        /// <see cref="WorkspaceExclusiveLock"/> is a sibling-file claim honoured by cooperating
        /// Chatcheology callers; it cannot stop an unrelated third-party SQLite client, a backup tool
        /// or a file-sync agent. Claiming "the workspace changed under the lock" would be an assertion
        /// this run cannot support, and blaming the computation instead would be the same mistake the
        /// other way round.
        /// <para>
        /// Exposed as its own function so it can be tested directly. Staging a real mid-run race would
        /// need a production mutation hook that should not exist.
        /// </para>
        /// </remarks>
        internal static string DescribeTwoPassDisagreement(string what, long? attachmentID)
        {
            var subject = attachmentID is { } id
                ? $"{what} for attachment {id}"
                : what;

            return
                $"Two passes of the same read-only analysis over one workspace disagreed about " +
                $"{subject}. Either the workspace was written between the passes by something " +
                $"outside this project's cooperating lock protocol — the sibling .lock file is " +
                $"honoured by Chatcheology callers only and cannot prevent another SQLite client " +
                $"from writing this file — or two computations that are supposed to be deterministic " +
                $"did not agree. This run cannot distinguish those, so it publishes nothing and is " +
                $"marked Failed. No evidence row was written.";
        }

        /// <summary>
        /// The diagnostic for pre-existing data that changed during the run.
        /// </summary>
        /// <remarks>
        /// Same care as <see cref="DescribeTwoPassDisagreement"/>: it reports what differs and names
        /// both possible causes without accusing either.
        /// </remarks>
        internal static string DescribePreExistingDataChange(string before, string after) =>
            $"The workspace's pre-existing data changed during this run: the ten pre-existing tables, " +
            $"the Attachment resolution distribution and the three Stage 7.2 decision tables read '{before}' before the run began and " +
            $"'{after}' inside the publication transaction. Either they were changed from outside " +
            $"this project's cooperating lock protocol, or this run has a defect. This run cannot " +
            $"distinguish those, so it publishes nothing and is marked Failed. No evidence row was " +
            $"written.";

        // ---------------------------------------------------------------------------------------
        // Cardinality and coverage.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Two independent derivations of one number: how many snapshot rows the run wrote, and how
        /// many unresolved attachments the requested conversation holds. The payload's own count is
        /// the third.
        /// </remarks>
        private static void VerifySnapshotCoverage(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long runID,
            Phase7RunRequest request,
            Phase7EvidencePayload payload)
        {
            var persisted = ScalarLong(
                connection,
                transaction,
                SnapshotRowCountStatement,
                ("$runID", runID));

            RequireEqual(
                "snapshot coverage (persisted rows against the payload)",
                payload.SnapshotRows.Count,
                persisted);

            var unresolved = ScalarLong(
                connection,
                transaction,
                ConversationUnresolvedAttachmentCountStatement,
                ("$conversationID", request.ConversationID),
                ("$unresolved", WorkspaceDatabase.UnresolvedAttachmentStatus));

            RequireEqual(
                "snapshot coverage (persisted rows against the conversation's unresolved attachments)",
                unresolved,
                persisted);

            RequireEqual(
                "snapshot coverage (the payload's in-scope count against its own rows)",
                payload.InScopeAttachmentCount,
                payload.SnapshotRows.Count);
        }

        /// <remarks>
        /// The set, not only the size: one grouped statement walked against the payload's sorted
        /// attachment identifiers, so an attachment silently absent — or one present twice — is
        /// impossible rather than merely improbable.
        /// </remarks>
        private static void VerifySnapshotAttachmentSet(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long runID,
            Phase7EvidencePayload payload)
        {
            var expected = payload.SnapshotRows.Select(row => row.AttachmentID).ToList();
            expected.Sort();

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SnapshotAttachmentIDsStatement;
            command.Parameters.AddWithValue("$runID", runID);

            using var reader = command.ExecuteReader();

            var position = 0;

            while (reader.Read())
            {
                var persisted = reader.GetInt64(0);

                if (position >= expected.Count)
                {
                    throw Failure(
                        "snapshot attachment set",
                        $"the run holds a snapshot row for attachment {persisted}, which the payload " +
                        $"does not contain");
                }

                if (expected[position] != persisted)
                {
                    throw Failure(
                        "snapshot attachment set",
                        $"position {position + 1} of the sorted set is attachment " +
                        $"{expected[position]} in the payload and {persisted} in the run");
                }

                position++;
            }

            RequireEqual("snapshot attachment set (size)", expected.Count, position);
        }

        /// <remarks>
        /// Items 3 and 4 of the frozen check list, as one statement each rather than one per
        /// attachment: a single scan of the run's snapshot rows counting those whose declared count
        /// disagrees with the rows actually present.
        /// </remarks>
        private static void VerifyDeclaredCountsAgreeWithRows(
            SqliteConnection connection, SqliteTransaction transaction, long runID)
        {
            RequireZero(
                connection,
                transaction,
                "declared exact-date candidate counts against persisted candidate rows",
                DeclaredCandidateCountMismatchStatement,
                runID);

            RequireZero(
                connection,
                transaction,
                "declared adjacent-date context counts against persisted context rows",
                DeclaredAdjacentCountMismatchStatement,
                runID);
        }

        private static void VerifyTotals(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long runID,
            Phase7EvidencePayload payload)
        {
            var candidates = ScalarLong(
                connection, transaction, CandidateRowCountStatement, ("$runID", runID));

            RequireEqual(
                "total candidate rows against the payload", payload.CandidateRows.Count, candidates);

            RequireEqual(
                "total candidate rows against the sum of declared counts",
                ScalarLong(
                    connection, transaction, DeclaredCandidateCountSumStatement, ("$runID", runID)),
                candidates);

            var adjacent = ScalarLong(
                connection, transaction, AdjacentRowCountStatement, ("$runID", runID));

            RequireEqual(
                "total adjacent-date context rows against the payload",
                payload.AdjacentContextRows.Count,
                adjacent);

            RequireEqual(
                "total adjacent-date context rows against the sum of declared counts",
                ScalarLong(
                    connection, transaction, DeclaredAdjacentCountSumStatement, ("$runID", runID)),
                adjacent);
        }

        // ---------------------------------------------------------------------------------------
        // Structure and containment.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Two statements: how many occurrence rows hang off this run's candidates, and how many
        /// occurrence rows the table holds at all. The first proves the rows reached the right parents;
        /// the pair together proves the run added exactly its own rows and nothing attached to another
        /// run's candidate.
        /// </remarks>
        private static void VerifyOccurrenceContainment(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long runID,
            Phase7EvidencePayload payload)
        {
            RequireEqual(
                "occurrence rows belonging to this run's candidates",
                payload.OccurrenceRows.Count,
                ScalarLong(
                    connection, transaction, OccurrenceRowCountForRunStatement, ("$runID", runID)));

            RequireEqual(
                "occurrence rows attached to no candidate of this or any earlier run",
                0,
                ScalarLong(connection, transaction, OrphanOccurrenceRowCountStatement));
        }

        /// <remarks>
        /// One grouped statement, ordered by candidate identifier and walked in step with the
        /// payload's own per-candidate expectation — the frozen-count agreement proof, restated
        /// against what was actually persisted.
        /// </remarks>
        private static void VerifyOccurrenceCountsPerCandidate(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long runID,
            Phase7EvidencePayload payload,
            IReadOnlyList<long> assignedCandidateIDs)
        {
            RequireEqual(
                "assigned candidate identifiers against the payload's candidate rows",
                payload.CandidateRows.Count,
                assignedCandidateIDs.Count);

            var expected = new Dictionary<long, int>(assignedCandidateIDs.Count);

            for (var index = 0; index < assignedCandidateIDs.Count; index++)
            {
                expected[assignedCandidateIDs[index]] =
                    payload.CandidateRows[index].ExpectedOccurrenceCount;
            }

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = OccurrenceCountsByCandidateStatement;
            command.Parameters.AddWithValue("$runID", runID);

            using var reader = command.ExecuteReader();

            var seen = 0;

            while (reader.Read())
            {
                var candidateID = reader.GetInt64(0);
                var persisted = reader.GetInt64(1);

                if (!expected.TryGetValue(candidateID, out var declared))
                {
                    throw Failure(
                        "per-candidate occurrence counts",
                        $"candidate {candidateID} holds occurrence rows but is not one of this run's " +
                        $"candidates");
                }

                if (declared != persisted)
                {
                    throw Failure(
                        "per-candidate occurrence counts",
                        $"candidate {candidateID} holds {persisted} occurrence rows while the frozen " +
                        $"analysis reports {declared} supporting physical copies");
                }

                seen++;
            }

            // A candidate with zero supporting copies cannot exist — the frozen analysis places an
            // asset in the exact-date set on the strength of at least one dated copy — so every
            // candidate must appear in the grouped result.
            RequireEqual(
                "per-candidate occurrence counts (candidates with at least one occurrence row)",
                expected.Count,
                seen);
        }

        /// <remarks>
        /// Deliberately independent of the payload: for every candidate row the run wrote, the
        /// database itself must hold a physical copy of that asset dated to the attachment's own
        /// message date. That is what makes "no adjacent-date asset is ever written to
        /// AttachmentCandidate" checkable from the stored data rather than from the loop that wrote
        /// it.
        /// </remarks>
        private static void VerifyEveryCandidateHasAnExactDateCopy(
            SqliteConnection connection, SqliteTransaction transaction, long runID) =>
            RequireZero(
                connection,
                transaction,
                "every candidate has a physical copy dated to its attachment's message date",
                CandidateWithoutExactDateCopyStatement,
                runID);

        private static void VerifyBandsAreDisjoint(
            SqliteConnection connection, SqliteTransaction transaction, long runID) =>
            RequireZero(
                connection,
                transaction,
                "exact-date candidates and adjacent-date context are disjoint per attachment",
                BandOverlapStatement,
                runID);

        private static void VerifySummaryFlags(
            SqliteConnection connection, SqliteTransaction transaction, long runID) =>
            RequireZero(
                connection,
                transaction,
                "snapshot direction summary flags against the persisted annotations",
                SummaryFlagMismatchStatement,
                runID);

        /// <remarks>
        /// The group invariant, re-derived from what was persisted rather than trusted from the pass
        /// that computed it: every member of one <c>(date, direction)</c> group must carry the same
        /// three group-shape facts, because those facts are properties of the group and not of the
        /// attachment. The direction is recomputed in SQL from the message's sender and the request's
        /// local participant, which is the same rule the frozen analysis applies.
        /// </remarks>
        private static void VerifyGroupInvariant(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long runID,
            Phase7RunRequest request) =>
            RequireZero(
                connection,
                transaction,
                "all members of one (date, direction) group carry the same group-shape facts",
                GroupInvariantStatement,
                runID,
                request.LocalParticipantID);

        /// <remarks>
        /// The assertion <c>TokenEvidence.BuildPositions</c> makes, restated against stored data: no
        /// copy supporting a <c>Compatible</c> candidate on the message's date may record the
        /// contradicting folder direction, because the frozen rule makes a relation compatible only
        /// when none does.
        /// <para>
        /// The check name says <em>candidate rows</em> rather than copies, because that is what the
        /// statement counts — see <see cref="DirectionInvariantStatement"/>. Only zero versus non-zero
        /// is used either way.
        /// </para>
        /// </remarks>
        private static void VerifyDirectionInvariant(
            SqliteConnection connection,
            SqliteTransaction transaction,
            long runID,
            Phase7RunRequest request) =>
            RequireZero(
                connection,
                transaction,
                "no candidate row annotated Compatible is supported on its message date by a copy " +
                "recording the contradicting direction",
                DirectionInvariantStatement,
                runID,
                request.LocalParticipantID);

        /// <remarks>
        /// The frozen <c>CHECK</c> constraints already enforce every one of these, so this can only
        /// fail if the schema were weakened. It is asserted anyway so that such a failure is reported
        /// as a named Stage 7.1 check rather than as a raw <c>SqliteException</c> from an insert.
        /// </remarks>
        private static void VerifyOccurrenceRowShapes(
            SqliteConnection connection, SqliteTransaction transaction, long runID) =>
            RequireZero(
                connection,
                transaction,
                "occurrence row shapes against the frozen grammar and collision rules",
                OccurrenceRowShapeStatement,
                runID);

        private static void VerifyTwoPassAgreement(Phase7EvidencePayload payload)
        {
            if (payload.TwoPassAgreementVerified)
            {
                return;
            }

            throw Failure(
                "two-pass determinism",
                "the payload does not record that the second read pass was checked against the first");
        }

        private static void VerifyPreExistingDataUnchanged(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Phase7PreExistingBaseline baseline)
        {
            var now = ReadBaseline(connection, transaction);

            if (now.Signature == baseline.Signature)
            {
                return;
            }

            throw new InvalidOperationException(
                DescribePreExistingDataChange(baseline.Signature, now.Signature));
        }

        // ---------------------------------------------------------------------------------------
        // Statements.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Every statement this type issues, for the structural test that asserts none of them writes.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            AttachmentResolutionDistributionStatement,
            SnapshotRowCountStatement,
            ConversationUnresolvedAttachmentCountStatement,
            SnapshotAttachmentIDsStatement,
            DeclaredCandidateCountMismatchStatement,
            DeclaredAdjacentCountMismatchStatement,
            CandidateRowCountStatement,
            DeclaredCandidateCountSumStatement,
            AdjacentRowCountStatement,
            DeclaredAdjacentCountSumStatement,
            OccurrenceRowCountForRunStatement,
            OrphanOccurrenceRowCountStatement,
            OccurrenceCountsByCandidateStatement,
            CandidateWithoutExactDateCopyStatement,
            BandOverlapStatement,
            SummaryFlagMismatchStatement,
            GroupInvariantStatement,
            DirectionInvariantStatement,
            OccurrenceRowShapeStatement,
        ];

        private const string AttachmentResolutionDistributionStatement =
            """
            SELECT ResolutionStatus, ResolvedMediaAssetID IS NULL, COUNT(*)
            FROM Attachment
            GROUP BY ResolutionStatus, ResolvedMediaAssetID IS NULL
            ORDER BY ResolutionStatus, ResolvedMediaAssetID IS NULL;
            """;

        private const string SnapshotRowCountStatement =
            "SELECT COUNT(*) FROM AttachmentEvidenceSnapshot WHERE RunID = $runID;";

        private const string ConversationUnresolvedAttachmentCountStatement =
            """
            SELECT COUNT(*)
            FROM Attachment AS a
            INNER JOIN Message AS m ON m.MessageID = a.MessageID
            WHERE m.ConversationID = $conversationID
              AND a.ResolutionStatus = $unresolved;
            """;

        private const string SnapshotAttachmentIDsStatement =
            """
            SELECT AttachmentID
            FROM AttachmentEvidenceSnapshot
            WHERE RunID = $runID
            ORDER BY AttachmentID;
            """;

        private const string DeclaredCandidateCountMismatchStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentEvidenceSnapshot AS s
            WHERE s.RunID = $runID
              AND s.ExactDateCandidateCount <> (
                  SELECT COUNT(*)
                  FROM AttachmentCandidate AS c
                  WHERE c.RunID = s.RunID AND c.AttachmentID = s.AttachmentID);
            """;

        private const string DeclaredAdjacentCountMismatchStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentEvidenceSnapshot AS s
            WHERE s.RunID = $runID
              AND s.AdjacentDateContextCount <> (
                  SELECT COUNT(*)
                  FROM AttachmentAdjacentDateContext AS x
                  WHERE x.RunID = s.RunID AND x.AttachmentID = s.AttachmentID);
            """;

        private const string CandidateRowCountStatement =
            "SELECT COUNT(*) FROM AttachmentCandidate WHERE RunID = $runID;";

        private const string DeclaredCandidateCountSumStatement =
            """
            SELECT COALESCE(SUM(ExactDateCandidateCount), 0)
            FROM AttachmentEvidenceSnapshot
            WHERE RunID = $runID;
            """;

        private const string AdjacentRowCountStatement =
            "SELECT COUNT(*) FROM AttachmentAdjacentDateContext WHERE RunID = $runID;";

        private const string DeclaredAdjacentCountSumStatement =
            """
            SELECT COALESCE(SUM(AdjacentDateContextCount), 0)
            FROM AttachmentEvidenceSnapshot
            WHERE RunID = $runID;
            """;

        private const string OccurrenceRowCountForRunStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentCandidateOccurrenceEvidence AS o
            INNER JOIN AttachmentCandidate AS c
                ON c.AttachmentCandidateID = o.AttachmentCandidateID
            WHERE c.RunID = $runID;
            """;

        private const string OrphanOccurrenceRowCountStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentCandidateOccurrenceEvidence AS o
            LEFT JOIN AttachmentCandidate AS c
                ON c.AttachmentCandidateID = o.AttachmentCandidateID
            WHERE c.AttachmentCandidateID IS NULL;
            """;

        private const string OccurrenceCountsByCandidateStatement =
            """
            SELECT o.AttachmentCandidateID, COUNT(*)
            FROM AttachmentCandidateOccurrenceEvidence AS o
            INNER JOIN AttachmentCandidate AS c
                ON c.AttachmentCandidateID = o.AttachmentCandidateID
            WHERE c.RunID = $runID
            GROUP BY o.AttachmentCandidateID
            ORDER BY o.AttachmentCandidateID;
            """;

        /// <summary>
        /// Check 8: every persisted candidate's asset really has a physical copy dated to its
        /// attachment's own message date.
        /// </summary>
        /// <remarks>
        /// The media-link side is materialised <strong>once</strong> and then probed, rather than being
        /// scanned again for every candidate row. That shape is not a preference: the frozen v3 schema
        /// indexes <c>MediaAssetFile</c> only by <c>UNIQUE (MediaFileID)</c> and its integer primary
        /// key, with no index on <c>MediaAssetID</c> — the same absence that made
        /// <c>MediaEvidenceIndex</c> build an in-memory index rather than query per attachment — and
        /// SQLite creates no automatic index for a correlated <c>NOT EXISTS</c> of this shape. Written
        /// the obvious way, this check scans all of <c>MediaAssetFile</c> once per candidate: measured
        /// at 20.6 s on the passing path and 262 s on a failing one at real archive scale, against
        /// 78 ms for the form below. That cost would be paid inside the publication transaction, with
        /// the writer reservation held and every other conforming writer blocked.
        /// <para>
        /// <c>MATERIALIZED</c> is stated explicitly rather than left to the planner, so the shape is a
        /// property of the statement rather than of a costing heuristic. No index is added and the
        /// schema is untouched.
        /// </para>
        /// </remarks>
        internal const string CandidateWithoutExactDateCopyStatement =
            """
            WITH DatedAssetKey AS MATERIALIZED (
                SELECT DISTINCT
                    l.MediaAssetID AS MediaAssetID,
                    f.FileDate AS FileDate
                FROM MediaAssetFile AS l
                INNER JOIN MediaFile AS f
                    ON f.MediaFileID = l.MediaFileID
                WHERE f.FileDate IS NOT NULL
            )
            SELECT COUNT(*)
            FROM AttachmentCandidate AS c
            INNER JOIN Attachment AS a
                ON a.AttachmentID = c.AttachmentID
            INNER JOIN Message AS m
                ON m.MessageID = a.MessageID
            LEFT JOIN DatedAssetKey AS k
                ON k.MediaAssetID = c.MediaAssetID
               AND k.FileDate = substr(m.MessageDateTimeLocal, 1, 10)
            WHERE c.RunID = $runID
              AND k.MediaAssetID IS NULL;
            """;

        private const string BandOverlapStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentCandidate AS c
            INNER JOIN AttachmentAdjacentDateContext AS x
                ON x.RunID = c.RunID
               AND x.AttachmentID = c.AttachmentID
               AND x.MediaAssetID = c.MediaAssetID
            WHERE c.RunID = $runID;
            """;

        private const string SummaryFlagMismatchStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentEvidenceSnapshot AS s
            WHERE s.RunID = $runID
              AND (
                  s.HasAnyCompatibleAnnotatedCandidate <> (
                      CASE WHEN EXISTS (
                          SELECT 1 FROM AttachmentCandidate AS c
                          WHERE c.RunID = s.RunID
                            AND c.AttachmentID = s.AttachmentID
                            AND c.DirectionCompatibilityAnnotation = 'Compatible')
                      THEN 1 ELSE 0 END)
                  OR s.HasAnyContradictoryOnlyAnnotatedCandidate <> (
                      CASE WHEN EXISTS (
                          SELECT 1 FROM AttachmentCandidate AS c
                          WHERE c.RunID = s.RunID
                            AND c.AttachmentID = s.AttachmentID
                            AND c.DirectionCompatibilityAnnotation = 'ContradictoryOnly')
                      THEN 1 ELSE 0 END));
            """;

        private const string GroupInvariantStatement =
            """
            SELECT COUNT(*)
            FROM (
                SELECT
                    substr(m.MessageDateTimeLocal, 1, 10) AS GroupDate,
                    CASE
                        WHEN $localParticipantID IS NULL OR m.SenderParticipantID IS NULL
                            THEN 'Unknown'
                        WHEN m.SenderParticipantID = $localParticipantID THEN 'Outgoing'
                        ELSE 'Incoming'
                    END AS GroupDirection,
                    COUNT(DISTINCT s.IsWithinDirectionImpossibleGroup) AS ImpossibleValues,
                    COUNT(DISTINCT s.IsForcedPositionGroupShape) AS ForcedValues,
                    COUNT(DISTINCT COALESCE(
                        s.RemainingCompatiblePositionCountUnderHypothesis, -1)) AS RemainingValues
                FROM AttachmentEvidenceSnapshot AS s
                INNER JOIN Attachment AS a ON a.AttachmentID = s.AttachmentID
                INNER JOIN Message AS m ON m.MessageID = a.MessageID
                WHERE s.RunID = $runID
                  AND s.ExactDateCandidateCount > 0
                GROUP BY GroupDate, GroupDirection)
            WHERE ImpossibleValues > 1 OR ForcedValues > 1 OR RemainingValues > 1;
            """;

        /// <summary>
        /// Check 12: no candidate annotated <c>Compatible</c> is supported, on its attachment's own
        /// message date, by a copy whose stored direction contradicts the message's.
        /// </summary>
        /// <remarks>
        /// Materialised once and probed, for exactly the reason
        /// <see cref="CandidateWithoutExactDateCopyStatement"/> gives: written as a plain join,
        /// <c>MediaAssetFile</c> becomes an inner nested-loop scan repeated per candidate, measured at
        /// 258 s at real archive scale — and unlike check 8 it cannot short-circuit, because proving a
        /// count of zero means exhausting the join. The form below measures 88 ms.
        /// <para>
        /// <strong>Direction-blind semantics are preserved:</strong> the materialised key set excludes
        /// <c>IsSent IS NULL</c>, so a copy from a source that records no direction at all — the whole
        /// of one recovered store, by design — contradicts nothing and is never counted.
        /// </para>
        /// <para>
        /// <strong>What the count means changed with the restructuring.</strong> The key set holds
        /// distinct <c>(MediaAssetID, FileDate, IsSent)</c> triples rather than one row per file, so this
        /// counts candidate rows paired with an offending key — at most one per candidate, since only one
        /// stored direction can contradict a given message direction — rather than counting offending
        /// files. Immaterial to the check, which reads only zero versus non-zero, and the check's name
        /// says candidates rather than copies so the diagnostic cannot mislead.
        /// </para>
        /// </remarks>
        internal const string DirectionInvariantStatement =
            """
            WITH ContradictingKey AS MATERIALIZED (
                SELECT DISTINCT
                    l.MediaAssetID AS MediaAssetID,
                    f.FileDate AS FileDate,
                    f.IsSent AS IsSent
                FROM MediaAssetFile AS l
                INNER JOIN MediaFile AS f
                    ON f.MediaFileID = l.MediaFileID
                WHERE f.FileDate IS NOT NULL
                  AND f.IsSent IS NOT NULL
            )
            SELECT COUNT(*)
            FROM AttachmentCandidate AS c
            INNER JOIN Attachment AS a
                ON a.AttachmentID = c.AttachmentID
            INNER JOIN Message AS m
                ON m.MessageID = a.MessageID
            INNER JOIN ContradictingKey AS k
                ON k.MediaAssetID = c.MediaAssetID
               AND k.FileDate = substr(m.MessageDateTimeLocal, 1, 10)
            WHERE c.RunID = $runID
              AND c.DirectionCompatibilityAnnotation = 'Compatible'
              AND k.IsSent <> (
                  CASE
                      WHEN m.SenderParticipantID = $localParticipantID THEN 1
                      ELSE 0
                  END
              );
            """;

        private const string OccurrenceRowShapeStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentCandidateOccurrenceEvidence AS o
            INNER JOIN AttachmentCandidate AS c
                ON c.AttachmentCandidateID = o.AttachmentCandidateID
            WHERE c.RunID = $runID
              AND (
                  (o.TokenGrammarSupported = 1
                      AND (o.SequenceToken IS NULL OR o.DateTokenKeyUniqueToPayload IS NULL))
                  OR (o.TokenGrammarSupported = 0
                      AND (o.SequenceToken IS NOT NULL
                           OR o.DateTokenKeyUniqueToPayload IS NOT NULL
                           OR o.OrderConsistencyAnnotation <> 'NotAssessable'))
                  OR (o.DateTokenKeyUniqueToPayload = 0
                      AND o.OrderConsistencyAnnotation <> 'NotAssessable'));
            """;

        // ---------------------------------------------------------------------------------------
        // Plumbing.
        // ---------------------------------------------------------------------------------------

        private static long ScalarLong(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string statement,
            params (string Name, object? Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = statement;

            foreach (var (name, value) in parameters)
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static void RequireZero(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string check,
            string statement,
            long runID)
        {
            var offending = ScalarLong(connection, transaction, statement, ("$runID", runID));

            if (offending == 0)
            {
                return;
            }

            throw Failure(check, $"{offending} row(s) fail it");
        }

        private static void RequireZero(
            SqliteConnection connection,
            SqliteTransaction transaction,
            string check,
            string statement,
            long runID,
            long? localParticipantID)
        {
            var offending = ScalarLong(
                connection,
                transaction,
                statement,
                ("$runID", runID),
                ("$localParticipantID", localParticipantID));

            if (offending == 0)
            {
                return;
            }

            throw Failure(check, $"{offending} row(s) fail it");
        }

        private static void RequireEqual(string check, long expected, long actual)
        {
            if (expected == actual)
            {
                return;
            }

            throw Failure(check, $"expected {expected} but the workspace holds {actual}");
        }

        private static InvalidOperationException Failure(string check, string detail) =>
            new(
                $"Stage 7.1 reconciliation check '{check}' failed: {detail}. Reconciliation is " +
                $"bookkeeping and never identifies anything, but a bookkeeping failure means this " +
                $"run cannot say what it computed. The publication transaction is rolled back, so no " +
                $"evidence row survives, and the run is marked Failed.");
    }

    /// <summary>
    /// The pre-existing state a run must leave exactly as it found it, reduced to one comparable
    /// signature.
    /// </summary>
    /// <param name="Signature">
    /// The ten pre-existing tables' row counts and the <c>Attachment</c> resolution distribution,
    /// rendered as text so two readings compare in one operation.
    /// </param>
    /// <remarks>
    /// A run-time tripwire, not the proof. Proving the pre-existing tables are unchanged whole-row and
    /// in both directions needs a checkpoint copy of the workspace, which a run must never create;
    /// that proof is a synthetic test using <c>WorkspaceCheckpoint.RequireIdenticalTables</c>.
    /// </remarks>
    internal sealed record Phase7PreExistingBaseline(string Signature);
}
