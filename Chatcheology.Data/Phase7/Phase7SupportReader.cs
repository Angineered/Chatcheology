using System.Globalization;
using Chatcheology.Data.Matching;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Reads one completed run's persisted evidence for one attachment and reduces it to the two
    /// support dimensions the frozen comparison rule is defined over.
    /// </summary>
    /// <remarks>
    /// <para><strong>Run scope is explicit, never guessed.</strong> A run's scope is whatever it
    /// evaluated, and the frozen design says in terms that a missing snapshot row for a run is "a fact
    /// about run scope, not about the attachment". So the evidence read requires a run identifier the
    /// caller chose, and it refuses outright — rather than returning an empty set — when the attachment
    /// has no snapshot row in that run. An empty set is what a legitimately zero-candidate attachment
    /// produces, and the two must stay distinguishable.</para>
    /// <para><strong>What this reader structurally cannot read.</strong> Its statements are internal
    /// constants that select only the candidate's asset, the candidate's direction annotation, and the
    /// occurrences' order annotations. <c>DateTokenKeyUniqueToPayload</c>, <c>SequenceToken</c>,
    /// <c>TokenGrammarSupported</c>, <c>RemainingCompatiblePositionCountUnderHypothesis</c>,
    /// <c>IsForcedPositionGroupShape</c>, <c>IsWithinDirectionImpossibleGroup</c>,
    /// <c>AttachmentAdjacentDateContext</c>, <c>SupportingPhysicalCopyCount</c> and <c>MediaType</c>
    /// appear in none of them — which makes "the ranking never reads them" a property of the queries
    /// rather than a promise about the code above them, the same device
    /// <c>MediaEvidenceIndex</c> uses for file names.</para>
    /// </remarks>
    public sealed class Phase7SupportReader
    {
        /// <summary>
        /// The latest completed run in this workspace, whatever its scope, or <see langword="null"/>
        /// when none has completed.
        /// </summary>
        /// <remarks>
        /// <strong>Workspace-global and scope-blind.</strong> This is not necessarily a run that
        /// evaluated any particular attachment: two runs may cover two different conversations, and
        /// this returns the later one either way. It is <em>not</em> the right input to
        /// <see cref="ReadAttachmentSupport"/> unless the caller has independently established that
        /// the run covers the attachment; for that question use
        /// <see cref="ReadLatestCompletedRunContaining"/>.
        /// <para>
        /// Selected by the maximum run identifier, never by a timestamp: the surrogate key is assigned
        /// in write order and a skewed clock cannot corrupt it. <see langword="null"/> is a valid
        /// answer every caller must handle, not an error.
        /// </para>
        /// </remarks>
        public long? ReadLatestCompletedRunID(SqliteConnection connection)
        {
            ArgumentNullException.ThrowIfNull(connection);

            using var command = connection.CreateCommand();
            command.CommandText = LatestCompletedRunStatement;
            command.Parameters.AddWithValue("$completed", Phase7EvidenceText.Completed);

            var value = command.ExecuteScalar();

            return value is null || value is DBNull
                ? null
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// The latest completed run that actually evaluated <paramref name="attachmentID"/>, or
        /// <see langword="null"/> when no completed run ever has.
        /// </summary>
        /// <remarks>
        /// The per-attachment reading of "current", which is the granularity an evidence read actually
        /// needs: it is immune to a later run whose scope is a different conversation, or neither one
        /// conversation nor the whole workspace. Derived from the run's own snapshot rows, which the
        /// frozen design makes the authority on what a run covered, so no schema change and no stored
        /// scope key is required.
        /// <para>
        /// <see langword="null"/> is a distinct and honest answer, and is not the same as "evaluated
        /// with no candidates".
        /// </para>
        /// <para>
        /// This filters <c>AttachmentEvidenceSnapshot</c> by <c>AttachmentID</c>, which is the second
        /// column of that table's only composite index, so the query scans. The scan is bounded by the
        /// in-scope attachment count times the number of completed runs — tens of thousands of narrow
        /// rows for any realistic run count — and is accepted rather than indexed around, because
        /// adding an index would change a frozen schema for a cost that does not exist.
        /// </para>
        /// </remarks>
        public long? ReadLatestCompletedRunContaining(
            SqliteConnection connection, long attachmentID)
        {
            ArgumentNullException.ThrowIfNull(connection);

            using var command = connection.CreateCommand();
            command.CommandText = LatestCompletedRunContainingStatement;
            command.Parameters.AddWithValue("$attachmentID", attachmentID);
            command.Parameters.AddWithValue("$completed", Phase7EvidenceText.Completed);

            var value = command.ExecuteScalar();

            return value is null || value is DBNull
                ? null
                : Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// One attachment's candidate support, as recorded by one specific completed run.
        /// </summary>
        /// <returns>
        /// One entry per persisted candidate, in ascending <c>MediaAssetID</c> order. An empty list is
        /// a legitimate answer: it means the run evaluated this attachment and found no exact-date
        /// candidate.
        /// </returns>
        /// <exception cref="InvalidOperationException">
        /// The run does not exist, has not completed, or did not evaluate this attachment. None of
        /// those returns an empty list, because an empty list already means something else.
        /// </exception>
        public IReadOnlyList<Phase7CandidateSupport> ReadAttachmentSupport(
            SqliteConnection connection, long runID, long attachmentID)
        {
            ArgumentNullException.ThrowIfNull(connection);

            RequireCompletedRun(connection, runID);
            RequireAttachmentInRunScope(connection, runID, attachmentID);

            var direction = ReadDirectionByCandidate(connection, runID, attachmentID);
            var order = ReadOrderByCandidate(connection, runID, attachmentID);

            var support = new List<Phase7CandidateSupport>(direction.Count);

            foreach (var (candidateID, entry) in direction.OrderBy(pair => pair.Value.MediaAssetID))
            {
                support.Add(
                    new Phase7CandidateSupport
                    {
                        MediaAssetID = entry.MediaAssetID,
                        Direction = ReduceDirection(entry.Compatibility),
                        OrderSupport = ReduceOrder(
                            order.TryGetValue(candidateID, out var annotations) ? annotations : []),
                    });
            }

            return support;
        }

        /// <summary>
        /// One attachment's candidates, reduced and arranged into the tiers of the partial order.
        /// </summary>
        /// <remarks>
        /// A convenience over <see cref="ReadAttachmentSupport"/> and
        /// <see cref="Phase7SupportRanking.BuildTiers"/>, with the same scope requirements and the same
        /// refusals.
        /// </remarks>
        public Phase7RankedCandidateSet ReadRankedCandidateSet(
            SqliteConnection connection, long runID, long attachmentID) =>
            Phase7SupportRanking.BuildTiers(
                ReadAttachmentSupport(connection, runID, attachmentID));

        // ---------------------------------------------------------------------------------------
        // The reduction.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The candidate-level direction reduction.
        /// </summary>
        /// <remarks>
        /// <c>Compatible</c> is available and positive; <c>Mixed</c>, <c>Unknown</c> and
        /// <c>ContradictoryOnly</c> are all unavailable. There is no third possibility to map to, and
        /// <c>ContradictoryOnly</c> is unavailable rather than negative — the closure forbids acting on
        /// it as an exclusion.
        /// </remarks>
        internal static Phase7DirectionSupport ReduceDirection(
            DirectionCompatibility compatibility) =>
            compatibility == DirectionCompatibility.Compatible
                ? Phase7DirectionSupport.Supportive
                : Phase7DirectionSupport.Unavailable;

        /// <summary>
        /// The occurrence-coherent order reduction: unanimous or unavailable.
        /// </summary>
        /// <remarks>
        /// Four steps, and no fifth. Reduce every occurrence; drop the unavailable ones; if none
        /// remain the dimension is unavailable; if the remainder is unanimous that agreed value is the
        /// candidate's witnessed reading; and <strong>if it disagrees the dimension is
        /// unavailable</strong>.
        /// <para>
        /// The last step is the anti-cherry-picking rule. There is no <c>max</c>, no <c>Any</c>, no
        /// majority, no "best" and no weighting by copy count anywhere here, because each of those
        /// would produce a value no single physical copy witnesses. The disagreement stays fully
        /// visible in the stored per-occurrence rows.
        /// </para>
        /// </remarks>
        internal static Phase7OrderSupport ReduceOrder(
            IReadOnlyList<OrderConsistencyAnnotation> annotations)
        {
            ArgumentNullException.ThrowIfNull(annotations);

            Phase7OrderSupport? witnessed = null;

            foreach (var annotation in annotations)
            {
                var reduced = annotation switch
                {
                    OrderConsistencyAnnotation.NotAssessable => Phase7OrderSupport.Unavailable,
                    OrderConsistencyAnnotation.NoCompatibleArrangement =>
                        Phase7OrderSupport.LessSupportive,

                    // Identical strength, deliberately. Uniqueness earns no bonus.
                    OrderConsistencyAnnotation.CompatibleWithAmbiguity =>
                        Phase7OrderSupport.Supportive,
                    OrderConsistencyAnnotation.UniquelyConstrained => Phase7OrderSupport.Supportive,

                    var unexpected => throw new InvalidOperationException(
                        $"There is no ranking reduction for the order annotation {unexpected}. " +
                        $"Nothing is inferred from a value outside the frozen vocabulary."),
                };

                if (reduced == Phase7OrderSupport.Unavailable)
                {
                    continue;
                }

                if (witnessed is null)
                {
                    witnessed = reduced;

                    continue;
                }

                if (witnessed != reduced)
                {
                    return Phase7OrderSupport.Unavailable;
                }
            }

            return witnessed ?? Phase7OrderSupport.Unavailable;
        }

        // ---------------------------------------------------------------------------------------
        // Reads.
        // ---------------------------------------------------------------------------------------

        private static void RequireCompletedRun(SqliteConnection connection, long runID)
        {
            using var command = connection.CreateCommand();
            command.CommandText = RunStatusStatement;
            command.Parameters.AddWithValue("$runID", runID);

            var value = command.ExecuteScalar();

            if (value is null || value is DBNull)
            {
                throw new InvalidOperationException(
                    $"There is no Phase 7 run {runID.ToString(CultureInfo.InvariantCulture)} in this " +
                    $"workspace. An evidence read names the run it is reading, so a run that does not " +
                    $"exist is a caller mistake rather than an absence of evidence.");
            }

            var status = Phase7EvidenceText.ParseRunStatus((string)value);

            if (status == Phase7RunStatus.Completed)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Phase 7 run {runID.ToString(CultureInfo.InvariantCulture)} has status {status} and " +
                $"has therefore published no evidence. Only a Completed run may be read. An empty " +
                $"result is not returned, because it would be indistinguishable from a legitimately " +
                $"zero-candidate attachment.");
        }

        private static void RequireAttachmentInRunScope(
            SqliteConnection connection, long runID, long attachmentID)
        {
            using var command = connection.CreateCommand();
            command.CommandText = SnapshotExistsStatement;
            command.Parameters.AddWithValue("$runID", runID);
            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Attachment {attachmentID.ToString(CultureInfo.InvariantCulture)} was not evaluated " +
                $"in Phase 7 run {runID.ToString(CultureInfo.InvariantCulture)}: that run holds no " +
                $"evidence snapshot row for it. This is a fact about the run's scope, not about the " +
                $"attachment, and an empty candidate set must not be inferred from it — an empty set " +
                $"already means 'evaluated, and no candidate was found'. Read this attachment against " +
                $"a run that evaluated it, which ReadLatestCompletedRunContaining will name.");
        }

        private static Dictionary<long, (long MediaAssetID, DirectionCompatibility Compatibility)>
            ReadDirectionByCandidate(SqliteConnection connection, long runID, long attachmentID)
        {
            using var command = connection.CreateCommand();
            command.CommandText = CandidateDirectionStatement;
            command.Parameters.AddWithValue("$runID", runID);
            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            var byCandidate =
                new Dictionary<long, (long MediaAssetID, DirectionCompatibility Compatibility)>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                byCandidate[reader.GetInt64(0)] =
                    (reader.GetInt64(1),
                     Phase7EvidenceText.ParseDirectionCompatibility(reader.GetString(2)));
            }

            return byCandidate;
        }

        private static Dictionary<long, List<OrderConsistencyAnnotation>> ReadOrderByCandidate(
            SqliteConnection connection, long runID, long attachmentID)
        {
            using var command = connection.CreateCommand();
            command.CommandText = OccurrenceOrderStatement;
            command.Parameters.AddWithValue("$runID", runID);
            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            var byCandidate = new Dictionary<long, List<OrderConsistencyAnnotation>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var candidateID = reader.GetInt64(0);

                if (!byCandidate.TryGetValue(candidateID, out var annotations))
                {
                    annotations = [];
                    byCandidate[candidateID] = annotations;
                }

                annotations.Add(
                    Phase7EvidenceText.ParseOrderConsistencyAnnotation(reader.GetString(1)));
            }

            return byCandidate;
        }

        // ---------------------------------------------------------------------------------------
        // Statements.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Every statement this reader issues, for the structural test that asserts none of them names
        /// a column or table the ranking must not read.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            LatestCompletedRunStatement,
            LatestCompletedRunContainingStatement,
            RunStatusStatement,
            SnapshotExistsStatement,
            CandidateDirectionStatement,
            OccurrenceOrderStatement,
        ];

        private const string LatestCompletedRunStatement =
            "SELECT MAX(RunID) FROM Phase7Run WHERE Status = $completed;";

        private const string LatestCompletedRunContainingStatement =
            """
            SELECT MAX(s.RunID)
            FROM AttachmentEvidenceSnapshot AS s
            INNER JOIN Phase7Run AS r ON r.RunID = s.RunID
            WHERE s.AttachmentID = $attachmentID
              AND r.Status = $completed;
            """;

        private const string RunStatusStatement =
            "SELECT Status FROM Phase7Run WHERE RunID = $runID;";

        private const string SnapshotExistsStatement =
            """
            SELECT COUNT(*)
            FROM AttachmentEvidenceSnapshot
            WHERE RunID = $runID AND AttachmentID = $attachmentID;
            """;

        private const string CandidateDirectionStatement =
            """
            SELECT c.AttachmentCandidateID, c.MediaAssetID, c.DirectionCompatibilityAnnotation
            FROM AttachmentCandidate AS c
            WHERE c.RunID = $runID AND c.AttachmentID = $attachmentID
            ORDER BY c.MediaAssetID;
            """;

        private const string OccurrenceOrderStatement =
            """
            SELECT o.AttachmentCandidateID, o.OrderConsistencyAnnotation
            FROM AttachmentCandidateOccurrenceEvidence AS o
            INNER JOIN AttachmentCandidate AS c
                ON c.AttachmentCandidateID = o.AttachmentCandidateID
            WHERE c.RunID = $runID AND c.AttachmentID = $attachmentID
            ORDER BY o.AttachmentCandidateID, o.AttachmentCandidateOccurrenceEvidenceID;
            """;
    }
}
