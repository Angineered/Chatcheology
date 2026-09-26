using System.Globalization;
using Chatcheology.Data.Media;
using Chatcheology.Data.Phase7;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// Gathers everything the human review panel shows about one attachment.
    /// </summary>
    /// <remarks>
    /// <para><strong>Read-only, and a consumer rather than an authority.</strong> Six of the seven
    /// reads are existing Phase 7 public APIs, whose answers are carried through unchanged. The
    /// seventh is one <c>SELECT</c> of its own, and it exists only because no Phase 7 reader returns
    /// candidate row identity together with the asset's descriptive facts.</para>
    /// <para><strong>It computes no ranking.</strong> Direction, order support, tiering and dominance
    /// come from <see cref="Phase7SupportReader.ReadRankedCandidateSet"/> and nowhere else. This
    /// class never reads <c>DirectionCompatibilityAnnotation</c>, never reads occurrence evidence,
    /// and never decides that one candidate stands better than another.</para>
    /// <para><strong>Every current candidate survives the assembly.</strong> The candidate rows are
    /// the left side of the merge, so a candidate whose support the ranking API did not return would
    /// still appear — reported as <c>Unavailable</c> rather than dropped. Current Phase 7 source
    /// cannot produce such a candidate, because its support read is itself driven from the candidate
    /// table; building the merge this way makes the guarantee structural instead of inherited.</para>
    /// <para><strong>Adjacent-date context is not read here at all.</strong> It is contextual
    /// evidence that the reconstruction summary already counts, and a chooser is exactly the place it
    /// must not appear.</para>
    /// </remarks>
    public sealed class AttachmentReviewReader
    {
        private readonly Phase7ReconstructionReader _reconstruction = new();
        private readonly Phase7SupportReader _support = new();
        private readonly Phase7CandidateDispositionReader _dispositions = new();
        private readonly Phase7ResolutionHistoryReader _history = new();

        /// <summary>
        /// One attachment's complete review state.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// The attachment does not exist, or the workspace holds a state Phase 7 refuses to describe
        /// — a resolution recorded against an absent asset, or a history that cannot have happened.
        /// Nothing is normalised or repaired.
        /// </exception>
        public AttachmentReviewState Read(SqliteConnection connection, long attachmentID)
        {
            ArgumentNullException.ThrowIfNull(connection);

            // Phase 7's answer to "what is this attachment, or why is it not anything yet".
            var reconstruction = _reconstruction.Read(connection, attachmentID);

            // Attachment-scoped, and deliberately not taken from the reconstruction state: that
            // carries no run for a resolved attachment, which the review panel still needs.
            var currentRunID = _support.ReadLatestCompletedRunContaining(connection, attachmentID);

            var candidates = Array.Empty<AttachmentReviewCandidate>() as IReadOnlyList<AttachmentReviewCandidate>;
            var tierCount = 0;

            if (currentRunID is { } runID)
            {
                (candidates, tierCount) = ReadCandidates(connection, runID, attachmentID);
            }

            return new AttachmentReviewState
            {
                AttachmentID = attachmentID,
                Ordinal = ReadOrdinal(connection, attachmentID),
                Reconstruction = reconstruction,
                CurrentEvidenceRunID = currentRunID,
                Candidates = candidates,
                TierCount = tierCount,
                HasCurrentReviewedInsufficientEvidence =
                    reconstruction.HasCurrentReviewedInsufficientEvidence,
                ResolutionHistory = _history.ReadHistory(connection, attachmentID),
                CurrentEffectiveEventID =
                    _history.ReplayProjection(connection, attachmentID).CurrentEffectiveEventID,
            };
        }

        /// <summary>
        /// The run's candidate rows, merged with Phase 7's support and disproof state.
        /// </summary>
        private (IReadOnlyList<AttachmentReviewCandidate> Candidates, int TierCount) ReadCandidates(
            SqliteConnection connection, long runID, long attachmentID)
        {
            var rows = ReadCandidateRows(connection, runID, attachmentID);

            if (rows.Count == 0)
            {
                return ([], 0);
            }

            var ranked = _support.ReadRankedCandidateSet(connection, runID, attachmentID);

            // Tier membership arrives keyed by asset. The schema's
            // UNIQUE (RunID, AttachmentID, MediaAssetID) makes that key identify exactly one
            // candidate row within this run, so the merge is a lookup rather than a guess.
            var tierByAsset = new Dictionary<long, int>();

            for (var tier = 0; tier < ranked.Tiers.Count; tier++)
            {
                foreach (var support in ranked.Tiers[tier])
                {
                    tierByAsset[support.MediaAssetID] = tier + 1;
                }
            }

            var supportByAsset = ranked.Tiers
                .SelectMany(tier => tier)
                .ToDictionary(support => support.MediaAssetID);

            var disproven = _dispositions
                .ReadIndependentlyDisprovenCandidateIDs(connection, runID, attachmentID)
                .ToHashSet();

            var candidates = new List<AttachmentReviewCandidate>(rows.Count);

            foreach (var row in rows)
            {
                var hasSupport = supportByAsset.TryGetValue(row.MediaAssetID, out var support);

                candidates.Add(new AttachmentReviewCandidate
                {
                    AttachmentCandidateID = row.AttachmentCandidateID,
                    MediaAssetID = row.MediaAssetID,
                    RunID = runID,

                    // A candidate the ranking did not place sits after every tier it did, reported
                    // as saying nothing rather than as saying something weak. Current source does
                    // not produce one; the branch exists so that if it ever did, the candidate would
                    // still be shown.
                    TierIndex = tierByAsset.TryGetValue(row.MediaAssetID, out var tier)
                        ? tier
                        : ranked.Tiers.Count + 1,

                    Direction = hasSupport
                        ? support!.Direction
                        : Phase7DirectionSupport.Unavailable,

                    OrderSupport = hasSupport
                        ? support!.OrderSupport
                        : Phase7OrderSupport.Unavailable,

                    IsIndependentlyDisproven = disproven.Contains(row.AttachmentCandidateID),

                    MediaType = MediaTypeText.Parse(row.MediaType),
                    SizeBytes = row.SizeBytes,
                    DurationMS = row.DurationMS,
                    Width = row.Width,
                    Height = row.Height,
                });
            }

            return (candidates, ranked.Tiers.Count);
        }

        private static List<CandidateRow> ReadCandidateRows(
            SqliteConnection connection, long runID, long attachmentID)
        {
            using var command = connection.CreateCommand();
            command.CommandText = CandidateRowsStatement;
            command.Parameters.AddWithValue("$runID", runID);
            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            var rows = new List<CandidateRow>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                rows.Add(new CandidateRow(
                    reader.GetInt64(0),
                    reader.GetInt64(1),
                    reader.GetString(2),
                    reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    reader.IsDBNull(6) ? null : reader.GetInt32(6)));
            }

            return rows;
        }

        private static int ReadOrdinal(SqliteConnection connection, long attachmentID)
        {
            using var command = connection.CreateCommand();
            command.CommandText = OrdinalStatement;
            command.Parameters.AddWithValue("$attachmentID", attachmentID);

            var ordinal = command.ExecuteScalar();

            return ordinal is null or DBNull
                ? throw new InvalidOperationException(
                    $"There is no attachment " +
                    $"{attachmentID.ToString(CultureInfo.InvariantCulture)} in this workspace.")
                : Convert.ToInt32(ordinal, CultureInfo.InvariantCulture);
        }

        private sealed record CandidateRow(
            long AttachmentCandidateID,
            long MediaAssetID,
            string MediaType,
            long SizeBytes,
            long? DurationMS,
            int? Width,
            int? Height);

        // -------------------------------------------------------------------------------------
        // Statements.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Every statement this reader issues, for the structural test that asserts none of them
        /// writes and that none names a decision table.
        /// </summary>
        internal static IReadOnlyList<string> Statements =>
        [
            CandidateRowsStatement,
            OrdinalStatement,
        ];

        /// <remarks>
        /// The candidate rows and their assets' descriptive facts, and nothing else. No evidence
        /// column is named here — not <c>DirectionCompatibilityAnnotation</c>, not any occurrence
        /// row, not <c>FileDate</c> or <c>IsSent</c> — because reading one would be this class
        /// forming an opinion the ranking API already owns.
        /// <para>
        /// Ordered by asset identifier: stable, repeatable, and carrying no claim about which
        /// candidate deserves attention first.
        /// </para>
        /// </remarks>
        private const string CandidateRowsStatement =
            """
            SELECT
                c.AttachmentCandidateID,
                c.MediaAssetID,
                a.MediaType,
                a.SizeBytes,
                a.DurationMS,
                a.Width,
                a.Height
            FROM AttachmentCandidate AS c
            INNER JOIN MediaAsset AS a ON a.MediaAssetID = c.MediaAssetID
            WHERE c.RunID = $runID AND c.AttachmentID = $attachmentID
            ORDER BY c.MediaAssetID;
            """;

        private const string OrdinalStatement =
            """
            SELECT Ordinal FROM Attachment WHERE AttachmentID = $attachmentID;
            """;
    }
}
