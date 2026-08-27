using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// The minimal read-only reconstruction model: what each attachment currently is, or why it is not
    /// anything yet.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read-only, and provably so.</strong> Every statement is a <c>SELECT</c>, asserted
    /// against the statement texts, and the reader takes a connection the caller owns so it can be used
    /// on a read-only one.</para>
    /// <para><strong>It never replays events.</strong> A resolved attachment is read from the
    /// <c>Attachment</c> projection, which the frozen design designates as the current effective
    /// projection for ordinary reads. Replaying history per attachment would be both the wrong authority
    /// for this question and a query per attachment; proving history and projection agree is
    /// reconciliation's job, not this reader's.</para>
    /// <para><strong>A fixed number of statements, whatever the batch size.</strong> Every read — of one
    /// attachment or of a thousand — issues exactly <see cref="StatementsPerRead"/> statements, all
    /// set-based over the requested identifiers. The single-attachment overload runs the batched
    /// implementation over a one-element list, so the two cannot disagree.</para>
    /// <para><strong>Evidence currency is attachment-scoped.</strong> The current run is the latest
    /// completed run that evaluated <em>this</em> attachment. So a later run covering a different
    /// conversation is not mistaken for this attachment's evidence, and an attachment omitted from the
    /// newest run because it has since been resolved still reads against the run that did evaluate it —
    /// which is why revoking such an attachment reports its earlier evidence rather than
    /// <see cref="Phase7UnresolvedPlaceholderReason.NeverEvaluated"/>.</para>
    /// </remarks>
    public sealed class Phase7ReconstructionReader
    {
        /// <summary>
        /// How many statements one read issues, whatever the batch size.
        /// </summary>
        internal const int StatementsPerRead = 7;

        /// <summary>
        /// How many statements the last read issued, for the test that pins the absence of per-attachment
        /// querying.
        /// </summary>
        /// <remarks>
        /// An internal observation counter, not part of the public surface. The property it exists to
        /// make checkable — that the statement count does not grow with the number of attachments — is
        /// otherwise only assertable by reading the implementation.
        /// </remarks>
        internal int LastReadStatementCount { get; private set; }

        /// <summary>
        /// One attachment's reconstruction state.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The attachment does not exist, or is recorded as resolved to an asset that is not there.
        /// </exception>
        public Phase7ReconstructionState Read(SqliteConnection connection, long attachmentID)
        {
            ArgumentNullException.ThrowIfNull(connection);

            return Read(connection, new[] { attachmentID })[0];
        }

        /// <summary>
        /// Several attachments' reconstruction states, in the order requested.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// No attachment was requested, or the same attachment was requested twice.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Some requested attachment does not exist, or is recorded as resolved to an asset that is not
        /// there.
        /// </exception>
        public IReadOnlyList<Phase7ReconstructionState> Read(
            SqliteConnection connection, IReadOnlyList<long> attachmentIDs)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(attachmentIDs);

            if (attachmentIDs.Count == 0)
            {
                throw new ArgumentException(
                    "No attachment was requested. An empty reconstruction read has no answer to " +
                    "return, and returning an empty list would read as 'none of them has anything', " +
                    "which is a different statement.",
                    nameof(attachmentIDs));
            }

            if (attachmentIDs.Distinct().Count() != attachmentIDs.Count)
            {
                throw new ArgumentException(
                    "The same attachment was requested more than once. The result is positional, so a " +
                    "duplicate would silently make the caller's own indexing ambiguous.",
                    nameof(attachmentIDs));
            }

            LastReadStatementCount = 0;

            var completed = Phase7EvidenceText.Completed;

            var disposition =
                Phase7DecisionText.Format(Phase7CandidateDisposition.IndependentlyDisproven);

            var reviewOutcome =
                Phase7DecisionText.Format(Phase7ReviewOutcome.ReviewedInsufficientEvidence);

            var projections = ReadProjections(connection, attachmentIDs);
            var assets = ReadAssets(connection, attachmentIDs);
            var snapshots = ReadSnapshots(connection, attachmentIDs, completed);
            var candidates = ReadCandidates(connection, attachmentIDs);
            var annotations = ReadAnnotations(connection, attachmentIDs);
            var disproven = ReadDisproven(connection, attachmentIDs, disposition);
            var reviews = ReadReviews(connection, attachmentIDs, reviewOutcome);

            var states = new List<Phase7ReconstructionState>(attachmentIDs.Count);

            foreach (var attachmentID in attachmentIDs)
            {
                if (!projections.TryGetValue(attachmentID, out var projection))
                {
                    throw new InvalidOperationException(
                        $"There is no attachment {Text(attachmentID)} in this workspace. A " +
                        $"reconstruction read describes attachments that exist; an identifier naming " +
                        $"nothing is a caller mistake rather than an attachment with no asset.");
                }

                states.Add(
                    projection.Status == Phase7ResolutionStatus.Resolved
                        ? BuildResolved(attachmentID, projection, assets)
                        : BuildUnresolved(
                            attachmentID,
                            snapshots,
                            candidates,
                            annotations,
                            disproven,
                            reviews));
            }

            return states;
        }

        // ---------------------------------------------------------------------------------------
        // The two shapes.
        // ---------------------------------------------------------------------------------------

        private static Phase7ReconstructionState BuildResolved(
            long attachmentID,
            (Phase7ResolutionStatus Status, long? ResolvedMediaAssetID) projection,
            IReadOnlyDictionary<long, Phase7ResolvedAssetDescriptor> assets)
        {
            var mediaAssetID = projection.ResolvedMediaAssetID
                ?? throw new InvalidOperationException(
                    $"Attachment {Text(attachmentID)} is recorded as resolved with no asset. The " +
                    $"column's own paired CHECK forbids that pairing, so the row was written by " +
                    $"something that bypassed it, and nothing here invents the asset it should name.");

            if (!assets.TryGetValue(mediaAssetID, out var descriptor))
            {
                throw new InvalidOperationException(
                    $"Attachment {Text(attachmentID)} is recorded as resolved to asset " +
                    $"{Text(mediaAssetID)}, which is not in this workspace. The foreign key forbids " +
                    $"that, so rather than return a partial descriptor this reports the " +
                    $"inconsistency.");
            }

            return new Phase7ReconstructionState
            {
                AttachmentID = attachmentID,
                Status = Phase7ResolutionStatus.Resolved,
                ResolvedAsset = descriptor,
                PlaceholderReason = null,

                // A resolved attachment answers from its projection. Its evidence, whatever it was,
                // is not what established the correspondence and is not reported as though it were.
                EvidenceRunID = null,
                ExactDateCandidateCount = null,
                AdjacentDateContextCount = null,
                IsWithinDirectionImpossibleGroup = null,
                CandidateTierCount = null,
                HasCurrentReviewedInsufficientEvidence = false,
                AllCurrentCandidatesIndependentlyDisproven = false,
            };
        }

        /// <remarks>
        /// <para>The precedence is explicit and deterministic, most specific first, and it is the
        /// approved one rather than a fresh judgement:</para>
        /// <list type="number">
        /// <item>never evaluated — there is no snapshot to describe, so nothing else has a basis;</item>
        /// <item>every current candidate independently disproven — the strongest statement the record
        /// supports about the current candidate set;</item>
        /// <item>reviewed insufficient evidence against the current run;</item>
        /// <item>impossible group flagged — the closure record's own distinct cause;</item>
        /// <item>no exact-date candidates but adjacent context exists;</item>
        /// <item>no candidates and no context;</item>
        /// <item>a candidate set the partial order discriminates over;</item>
        /// <item>a candidate set it does not.</item>
        /// </list>
        /// <para>Human review outcomes sit above the evidence-shape reasons because the question this
        /// answers is why the attachment has no asset, and "a person examined this snapshot and could
        /// not identify it" is the most advanced state the record can be in. Every condition the
        /// precedence passed over is still reported on the state itself.</para>
        /// </remarks>
        private static Phase7ReconstructionState BuildUnresolved(
            long attachmentID,
            IReadOnlyDictionary<long, SnapshotFacts> snapshots,
            IReadOnlyDictionary<long, List<CandidateRow>> candidates,
            IReadOnlyDictionary<long, List<OrderConsistencyAnnotation>> annotations,
            IReadOnlySet<long> disproven,
            IReadOnlyDictionary<long, HashSet<long>> reviews)
        {
            if (!snapshots.TryGetValue(attachmentID, out var snapshot))
            {
                return new Phase7ReconstructionState
                {
                    AttachmentID = attachmentID,
                    Status = Phase7ResolutionStatus.Unresolved,
                    ResolvedAsset = null,
                    PlaceholderReason = Phase7UnresolvedPlaceholderReason.NeverEvaluated,
                    EvidenceRunID = null,
                    ExactDateCandidateCount = null,
                    AdjacentDateContextCount = null,
                    IsWithinDirectionImpossibleGroup = null,
                    CandidateTierCount = null,
                    HasCurrentReviewedInsufficientEvidence = false,
                    AllCurrentCandidatesIndependentlyDisproven = false,
                };
            }

            var current = candidates.TryGetValue(attachmentID, out var all)
                ? all.Where(candidate => candidate.RunID == snapshot.RunID).ToList()
                : [];

            var support = current
                .Select(candidate => new Phase7CandidateSupport
                {
                    MediaAssetID = candidate.MediaAssetID,
                    Direction = Phase7SupportReader.ReduceDirection(candidate.Compatibility),
                    OrderSupport = Phase7SupportReader.ReduceOrder(
                        annotations.TryGetValue(candidate.AttachmentCandidateID, out var read)
                            ? read
                            : []),
                })
                .ToList();

            var tierCount = support.Count == 0
                ? 0
                : Phase7SupportRanking.BuildTiers(support).Tiers.Count;

            var allDisproven =
                current.Count > 0 && current.All(c => disproven.Contains(c.AttachmentCandidateID));

            var reviewed = reviews.TryGetValue(attachmentID, out var reviewedRuns)
                && reviewedRuns.Contains(snapshot.RunID);

            return new Phase7ReconstructionState
            {
                AttachmentID = attachmentID,
                Status = Phase7ResolutionStatus.Unresolved,
                ResolvedAsset = null,
                PlaceholderReason = ChooseReason(snapshot, current.Count, tierCount, allDisproven, reviewed),
                EvidenceRunID = snapshot.RunID,
                ExactDateCandidateCount = snapshot.ExactDateCandidateCount,
                AdjacentDateContextCount = snapshot.AdjacentDateContextCount,
                IsWithinDirectionImpossibleGroup = snapshot.IsWithinDirectionImpossibleGroup,
                CandidateTierCount = tierCount,
                HasCurrentReviewedInsufficientEvidence = reviewed,
                AllCurrentCandidatesIndependentlyDisproven = allDisproven,
            };
        }

        private static Phase7UnresolvedPlaceholderReason ChooseReason(
            SnapshotFacts snapshot,
            int candidateCount,
            int tierCount,
            bool allDisproven,
            bool reviewed)
        {
            if (allDisproven)
            {
                return Phase7UnresolvedPlaceholderReason
                    .ReviewedAllCandidatesIndependentlyDisproven;
            }

            if (reviewed)
            {
                return Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence;
            }

            if (snapshot.IsWithinDirectionImpossibleGroup)
            {
                return Phase7UnresolvedPlaceholderReason.ImpossibleGroupFlagged;
            }

            if (candidateCount == 0)
            {
                return snapshot.AdjacentDateContextCount > 0
                    ? Phase7UnresolvedPlaceholderReason.NoExactDateCandidates
                    : Phase7UnresolvedPlaceholderReason.NoCandidates;
            }

            return tierCount > 1
                ? Phase7UnresolvedPlaceholderReason.CandidateSetRanked
                : Phase7UnresolvedPlaceholderReason.CandidateSetUnranked;
        }

        // ---------------------------------------------------------------------------------------
        // The seven reads.
        // ---------------------------------------------------------------------------------------

        private Dictionary<long, (Phase7ResolutionStatus Status, long? ResolvedMediaAssetID)>
            ReadProjections(SqliteConnection connection, IReadOnlyList<long> attachmentIDs)
        {
            using var command = Command(connection, ProjectionsStatement, attachmentIDs);

            var projections =
                new Dictionary<long, (Phase7ResolutionStatus, long?)>(attachmentIDs.Count);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                projections[reader.GetInt64(0)] = (
                    Phase7DecisionText.ParseResolutionStatus(reader.GetString(1)),
                    reader.IsDBNull(2) ? null : reader.GetInt64(2));
            }

            return projections;
        }

        private Dictionary<long, Phase7ResolvedAssetDescriptor> ReadAssets(
            SqliteConnection connection, IReadOnlyList<long> attachmentIDs)
        {
            using var command = Command(connection, ResolvedAssetsStatement, attachmentIDs);

            var assets = new Dictionary<long, Phase7ResolvedAssetDescriptor>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                assets[reader.GetInt64(0)] = new Phase7ResolvedAssetDescriptor
                {
                    MediaAssetID = reader.GetInt64(0),
                    MediaType = reader.GetString(1),
                    SizeBytes = reader.GetInt64(2),
                    DurationMS = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    Width = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Height = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                };
            }

            return assets;
        }

        /// <remarks>
        /// Every completed run's snapshot row for the requested attachments, reduced in memory to the
        /// highest run identifier per attachment. That is the attachment-scoped current run and its
        /// facts in one statement, rather than one statement to find the run and another to read it.
        /// </remarks>
        private Dictionary<long, SnapshotFacts> ReadSnapshots(
            SqliteConnection connection, IReadOnlyList<long> attachmentIDs, string completed)
        {
            using var command = Command(connection, SnapshotsStatement, attachmentIDs);
            command.Parameters.AddWithValue("$completed", completed);

            var latest = new Dictionary<long, SnapshotFacts>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var attachmentID = reader.GetInt64(0);

                var facts = new SnapshotFacts(
                    reader.GetInt64(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3),
                    reader.GetInt64(4) == 1);

                if (!latest.TryGetValue(attachmentID, out var known) || facts.RunID > known.RunID)
                {
                    latest[attachmentID] = facts;
                }
            }

            return latest;
        }

        private Dictionary<long, List<CandidateRow>> ReadCandidates(
            SqliteConnection connection, IReadOnlyList<long> attachmentIDs)
        {
            using var command = Command(connection, CandidatesStatement, attachmentIDs);

            var byAttachment = new Dictionary<long, List<CandidateRow>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var attachmentID = reader.GetInt64(2);

                if (!byAttachment.TryGetValue(attachmentID, out var rows))
                {
                    rows = [];
                    byAttachment[attachmentID] = rows;
                }

                rows.Add(
                    new CandidateRow(
                        reader.GetInt64(0),
                        reader.GetInt64(1),
                        reader.GetInt64(3),
                        Phase7EvidenceText.ParseDirectionCompatibility(reader.GetString(4))));
            }

            return byAttachment;
        }

        private Dictionary<long, List<OrderConsistencyAnnotation>> ReadAnnotations(
            SqliteConnection connection, IReadOnlyList<long> attachmentIDs)
        {
            using var command = Command(connection, AnnotationsStatement, attachmentIDs);

            var byCandidate = new Dictionary<long, List<OrderConsistencyAnnotation>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var candidateID = reader.GetInt64(0);

                if (!byCandidate.TryGetValue(candidateID, out var read))
                {
                    read = [];
                    byCandidate[candidateID] = read;
                }

                read.Add(Phase7EvidenceText.ParseOrderConsistencyAnnotation(reader.GetString(1)));
            }

            return byCandidate;
        }

        private HashSet<long> ReadDisproven(
            SqliteConnection connection, IReadOnlyList<long> attachmentIDs, string disposition)
        {
            using var command = Command(connection, DisprovenStatement, attachmentIDs);
            command.Parameters.AddWithValue("$disposition", disposition);

            var disproven = new HashSet<long>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                disproven.Add(reader.GetInt64(0));
            }

            return disproven;
        }

        private Dictionary<long, HashSet<long>> ReadReviews(
            SqliteConnection connection, IReadOnlyList<long> attachmentIDs, string outcome)
        {
            using var command = Command(connection, ReviewsStatement, attachmentIDs);
            command.Parameters.AddWithValue("$outcome", outcome);

            var byAttachment = new Dictionary<long, HashSet<long>>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                var attachmentID = reader.GetInt64(0);

                if (!byAttachment.TryGetValue(attachmentID, out var runs))
                {
                    runs = [];
                    byAttachment[attachmentID] = runs;
                }

                runs.Add(reader.GetInt64(1));
            }

            return byAttachment;
        }

        // ---------------------------------------------------------------------------------------
        // Plumbing.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// Builds one command with the requested identifiers bound as parameters.
        /// </summary>
        /// <remarks>
        /// The identifier list is parameterised rather than interpolated, so no value reaches the
        /// statement text. The text itself varies only in how many placeholders the <c>IN</c> list
        /// carries, which is why the number of statements a read issues is fixed while the batch size is
        /// not.
        /// </remarks>
        private SqliteCommand Command(
            SqliteConnection connection, string template, IReadOnlyList<long> attachmentIDs)
        {
            var placeholders = new StringBuilder();

            for (var index = 0; index < attachmentIDs.Count; index++)
            {
                if (index > 0)
                {
                    placeholders.Append(", ");
                }

                placeholders.Append("$attachment").Append(
                    index.ToString(CultureInfo.InvariantCulture));
            }

            var command = connection.CreateCommand();
            command.CommandText = template.Replace(AttachmentIDPlaceholders, placeholders.ToString());

            for (var index = 0; index < attachmentIDs.Count; index++)
            {
                command.Parameters.AddWithValue(
                    $"$attachment{index.ToString(CultureInfo.InvariantCulture)}",
                    attachmentIDs[index]);
            }

            LastReadStatementCount++;

            return command;
        }

        private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

        private sealed record SnapshotFacts(
            long RunID,
            int ExactDateCandidateCount,
            int AdjacentDateContextCount,
            bool IsWithinDirectionImpossibleGroup);

        private sealed record CandidateRow(
            long AttachmentCandidateID,
            long RunID,
            long MediaAssetID,
            Matching.DirectionCompatibility Compatibility);

        // ---------------------------------------------------------------------------------------
        // Statements.
        // ---------------------------------------------------------------------------------------

        /// <summary>The token each statement's identifier list is substituted into.</summary>
        private const string AttachmentIDPlaceholders = "$attachmentIDs";

        /// <summary>
        /// Every statement this reader issues, for the structural tests that assert none of them writes
        /// and that none names a ranking-only column, a file name or a path.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            ProjectionsStatement,
            ResolvedAssetsStatement,
            SnapshotsStatement,
            CandidatesStatement,
            AnnotationsStatement,
            DisprovenStatement,
            ReviewsStatement,
        ];

        private const string ProjectionsStatement =
            """
            SELECT AttachmentID, ResolutionStatus, ResolvedMediaAssetID
            FROM Attachment
            WHERE AttachmentID IN ($attachmentIDs);
            """;

        /// <remarks>
        /// Joined through the projection rather than taking a second list of asset identifiers, so this
        /// read is driven by the same requested attachments as every other and the statement count stays
        /// fixed whatever the batch contains.
        /// </remarks>
        private const string ResolvedAssetsStatement =
            """
            SELECT a.MediaAssetID, a.MediaType, a.SizeBytes, a.DurationMS, a.Width, a.Height
            FROM MediaAsset AS a
            INNER JOIN Attachment AS t ON t.ResolvedMediaAssetID = a.MediaAssetID
            WHERE t.AttachmentID IN ($attachmentIDs);
            """;

        private const string SnapshotsStatement =
            """
            SELECT
                s.AttachmentID,
                s.RunID,
                s.ExactDateCandidateCount,
                s.AdjacentDateContextCount,
                s.IsWithinDirectionImpossibleGroup
            FROM AttachmentEvidenceSnapshot AS s
            INNER JOIN Phase7Run AS r ON r.RunID = s.RunID
            WHERE s.AttachmentID IN ($attachmentIDs)
              AND r.Status = $completed;
            """;

        private const string CandidatesStatement =
            """
            SELECT
                c.AttachmentCandidateID,
                c.RunID,
                c.AttachmentID,
                c.MediaAssetID,
                c.DirectionCompatibilityAnnotation
            FROM AttachmentCandidate AS c
            WHERE c.AttachmentID IN ($attachmentIDs);
            """;

        private const string AnnotationsStatement =
            """
            SELECT o.AttachmentCandidateID, o.OrderConsistencyAnnotation
            FROM AttachmentCandidateOccurrenceEvidence AS o
            INNER JOIN AttachmentCandidate AS c
                ON c.AttachmentCandidateID = o.AttachmentCandidateID
            WHERE c.AttachmentID IN ($attachmentIDs)
            ORDER BY o.AttachmentCandidateID, o.AttachmentCandidateOccurrenceEvidenceID;
            """;

        private const string DisprovenStatement =
            """
            SELECT DISTINCT d.AttachmentCandidateID
            FROM AttachmentCandidateDispositionEvent AS d
            INNER JOIN AttachmentCandidate AS c
                ON c.AttachmentCandidateID = d.AttachmentCandidateID
            WHERE c.AttachmentID IN ($attachmentIDs)
              AND d.Disposition = $disposition;
            """;

        private const string ReviewsStatement =
            """
            SELECT AttachmentID, SourceRunID
            FROM AttachmentReviewEvent
            WHERE AttachmentID IN ($attachmentIDs)
              AND ReviewOutcome = $outcome;
            """;
    }
}
