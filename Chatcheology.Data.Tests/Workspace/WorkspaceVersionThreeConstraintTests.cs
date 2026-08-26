using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;
using static Chatcheology.Data.Tests.Workspace.WorkspaceTestData;

namespace Chatcheology.Data.Tests.Workspace
{
    /// <summary>
    /// Tests that the version-3 tables enforce the Phase 7 rules the frozen design states.
    /// </summary>
    /// <remarks>
    /// These exercise the project's own invariants rather than SQLite's behaviour in general: that
    /// sequence evidence cannot claim a reading it does not have, that a confirmation cannot be
    /// recorded without an independently justified basis, that a revocation must say why, and that a
    /// replacement or revocation cannot act on another attachment's history.
    /// <para>
    /// Rows are inserted with direct SQL because no Phase 7 persistence API exists yet — writing one
    /// to test a constraint would be building the next milestone early. Nothing here computes
    /// evidence, ranks a candidate or resolves an attachment.
    /// </para>
    /// </remarks>
    public class WorkspaceVersionThreeConstraintTests
    {
        private const string ValidHash =
            "AAAA000000000000000000000000000000000000000000000000000000000001";

        private const string RunHash =
            "CCCC000000000000000000000000000000000000000000000000000000000003";

        // ---------------------------------------------------------------------------------------
        // Phase7Run.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("Complete")]
        [InlineData("running")]
        [InlineData("Cancelled")]
        [InlineData("")]
        public void Phase7Run_UnsupportedStatus_IsRejected(string status)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out _);

            AssertRejected(
                connection,
                $"""
                INSERT INTO Phase7Run (
                    StartedAtUtc, CompletedAtUtc, Status, PreRunWorkspaceFileSha256,
                    RankingDimensionSetVersion)
                VALUES ('{ImportedDateTimeUtcText}', NULL, '{status}', '{RunHash}', 'v1');
                """);
        }

        [Fact]
        public void Phase7Run_CompletedWithoutACompletionTime_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out _);

            // A run that claims to have finished must say when. Otherwise an unfinished run and a
            // finished one are indistinguishable, and the unfinished one would read as current.
            AssertRejected(
                connection,
                $"""
                INSERT INTO Phase7Run (
                    StartedAtUtc, CompletedAtUtc, Status, PreRunWorkspaceFileSha256,
                    RankingDimensionSetVersion)
                VALUES ('{ImportedDateTimeUtcText}', NULL, 'Completed', '{RunHash}', 'v1');
                """);
        }

        [Theory]
        [InlineData("Running")]
        [InlineData("Failed")]
        public void Phase7Run_UnfinishedOrFailedWithNoCompletionTime_IsAllowed(string status)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out _);

            Execute(
                connection,
                $"""
                INSERT INTO Phase7Run (
                    StartedAtUtc, CompletedAtUtc, Status, PreRunWorkspaceFileSha256,
                    RankingDimensionSetVersion)
                VALUES ('{ImportedDateTimeUtcText}', NULL, '{status}', '{RunHash}', 'v1');
                """);

            // The seeded workspace already holds one completed run, so this counts only the new one.
            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    $"SELECT COUNT(*) FROM Phase7Run WHERE Status = '{status}';"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("AAAA")]
        public void Phase7Run_PreRunHashOfTheWrongLength_IsRejected(string hash)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out _);

            AssertRejected(
                connection,
                $"""
                INSERT INTO Phase7Run (
                    StartedAtUtc, Status, PreRunWorkspaceFileSha256, RankingDimensionSetVersion)
                VALUES ('{ImportedDateTimeUtcText}', 'Running', '{hash}', 'v1');
                """);
        }

        [Fact]
        public void Phase7Run_EmptyRankingDimensionSetVersion_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out _);

            AssertRejected(
                connection,
                $"""
                INSERT INTO Phase7Run (
                    StartedAtUtc, Status, PreRunWorkspaceFileSha256, RankingDimensionSetVersion)
                VALUES ('{ImportedDateTimeUtcText}', 'Running', '{RunHash}', '');
                """);
        }

        // ---------------------------------------------------------------------------------------
        // AttachmentEvidenceSnapshot.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The row this table exists for: an attachment evaluated in a run that produced no candidate
        /// at all. A zero count is a recorded finding, and it must be storable.
        /// </remarks>
        [Fact]
        public void EvidenceSnapshot_ZeroCandidates_IsAllowedAndDistinctFromNoRow()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            Execute(connection, InsertSnapshotSql(seed.RunID, seed.AttachmentID, exactCount: 0));

            Assert.Equal(1, CountRows(connection, "AttachmentEvidenceSnapshot"));

            Assert.Equal(
                1,
                ScalarLong(
                    connection,
                    """
                    SELECT COUNT(*) FROM AttachmentEvidenceSnapshot
                    WHERE ExactDateCandidateCount = 0 AND AdjacentDateContextCount = 0;
                    """));
        }

        [Fact]
        public void EvidenceSnapshot_TwoRowsForOneAttachmentInOneRun_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            Execute(connection, InsertSnapshotSql(seed.RunID, seed.AttachmentID));

            AssertRejected(connection, InsertSnapshotSql(seed.RunID, seed.AttachmentID));
        }

        [Fact]
        public void EvidenceSnapshot_NegativeCandidateCount_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection, InsertSnapshotSql(seed.RunID, seed.AttachmentID, exactCount: -1));
        }

        [Theory]
        [InlineData("HasAnyCompatibleAnnotatedCandidate")]
        [InlineData("HasAnyContradictoryOnlyAnnotatedCandidate")]
        [InlineData("IsWithinDirectionImpossibleGroup")]
        [InlineData("IsForcedPositionGroupShape")]
        public void EvidenceSnapshot_FlagOutsideZeroOrOne_IsRejected(string column)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            // Every flag is written once, with the one under test set to a value outside its two
            // states. Naming a column twice in one INSERT would let SQLite keep the first value and
            // quietly discard the invalid one, proving nothing.
            var flags = new[]
            {
                "HasAnyCompatibleAnnotatedCandidate",
                "HasAnyContradictoryOnlyAnnotatedCandidate",
                "IsWithinDirectionImpossibleGroup",
                "IsForcedPositionGroupShape",
            };

            var values = flags.Select(flag => flag == column ? "2" : "0");

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentEvidenceSnapshot (
                    RunID, AttachmentID, ExactDateCandidateCount, AdjacentDateContextCount,
                    {string.Join(", ", flags)})
                VALUES ({seed.RunID}, {seed.AttachmentID}, 0, 0, {string.Join(", ", values)});
                """);
        }

        [Fact]
        public void EvidenceSnapshot_RunThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(connection, InsertSnapshotSql(runID: 9999, seed.AttachmentID));
        }

        // ---------------------------------------------------------------------------------------
        // AttachmentCandidate.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData("Contradictory")]
        [InlineData("compatible")]
        [InlineData("Likely")]
        [InlineData("")]
        public void Candidate_UnsupportedDirectionAnnotation_IsRejected(string annotation)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentCandidate (
                    RunID, AttachmentID, MediaAssetID, DirectionCompatibilityAnnotation)
                VALUES ({seed.RunID}, {seed.AttachmentID}, {seed.MediaAssetID}, '{annotation}');
                """);
        }

        /// <remarks>
        /// ContradictoryOnly is a recorded annotation and never an exclusion, so a candidate carrying
        /// it must be storable exactly like any other.
        /// </remarks>
        [Theory]
        [InlineData("Unknown")]
        [InlineData("Compatible")]
        [InlineData("Mixed")]
        [InlineData("ContradictoryOnly")]
        public void Candidate_EachDirectionAnnotation_IsAllowed(string annotation)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            Execute(
                connection,
                $"""
                INSERT INTO AttachmentCandidate (
                    RunID, AttachmentID, MediaAssetID, DirectionCompatibilityAnnotation)
                VALUES ({seed.RunID}, {seed.AttachmentID}, {seed.MediaAssetID}, '{annotation}');
                """);

            Assert.Equal(1, CountRows(connection, "AttachmentCandidate"));
        }

        [Fact]
        public void Candidate_SameAssetTwiceInOneRun_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            InsertCandidate(connection, seed);

            AssertRejected(connection, InsertCandidateSql(seed));
        }

        [Fact]
        public void Candidate_AssetThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentCandidate (
                    RunID, AttachmentID, MediaAssetID, DirectionCompatibilityAnnotation)
                VALUES ({seed.RunID}, {seed.AttachmentID}, 9999, 'Compatible');
                """);
        }

        // ---------------------------------------------------------------------------------------
        // AttachmentCandidateOccurrenceEvidence.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The invariant the frozen design states outright: a genuine (FileDate, SequenceToken)
        /// collision makes the position that occurrence would witness ambiguous, so its order reading
        /// is not assessable. Anything else would let a collided token support an ordering claim.
        /// </remarks>
        [Theory]
        [InlineData("NoCompatibleArrangement")]
        [InlineData("CompatibleWithAmbiguity")]
        [InlineData("UniquelyConstrained")]
        public void Occurrence_CollidedTokenClaimingAnOrderReading_IsRejected(string annotation)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            AssertRejected(
                connection,
                InsertOccurrenceSql(
                    candidateID,
                    seed.MediaFileID,
                    grammarSupported: 1,
                    sequenceToken: "8",
                    uniqueToPayload: "0",
                    annotation));
        }

        [Fact]
        public void Occurrence_CollidedTokenNotAssessable_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            Execute(
                connection,
                InsertOccurrenceSql(
                    candidateID,
                    seed.MediaFileID,
                    grammarSupported: 1,
                    sequenceToken: "8",
                    uniqueToPayload: "0",
                    "NotAssessable"));

            Assert.Equal(1, CountRows(connection, "AttachmentCandidateOccurrenceEvidence"));
        }

        [Theory]
        [InlineData("NoCompatibleArrangement")]
        [InlineData("CompatibleWithAmbiguity")]
        [InlineData("UniquelyConstrained")]
        public void Occurrence_NoGrammarClaimingAnOrderReading_IsRejected(string annotation)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            AssertRejected(
                connection,
                InsertOccurrenceSql(
                    candidateID,
                    seed.MediaFileID,
                    grammarSupported: 0,
                    sequenceToken: "NULL",
                    uniqueToPayload: "NULL",
                    annotation));
        }

        /// <remarks>
        /// The grammar pairings are biconditional: if the grammar parsed, both derived facts exist and
        /// must be recorded, and if it did not, neither may be. A half-populated row would otherwise
        /// reach a later ranking computation looking like a computed negative.
        /// </remarks>
        [Theory]
        [InlineData(1, "NULL", "1")]
        [InlineData(1, "7", "NULL")]
        [InlineData(0, "7", "NULL")]
        [InlineData(0, "NULL", "1")]
        public void Occurrence_GrammarAndDerivedFactsDisagreeing_IsRejected(
            int grammarSupported, string sequenceToken, string uniqueToPayload)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            AssertRejected(
                connection,
                InsertOccurrenceSql(
                    candidateID,
                    seed.MediaFileID,
                    grammarSupported,
                    sequenceToken,
                    uniqueToPayload,
                    grammarSupported == 1 ? "CompatibleWithAmbiguity" : "NotAssessable"));
        }

        [Theory]
        [InlineData("-1")]
        [InlineData("10000")]
        public void Occurrence_TokenOutsideTheFourDigitGrammar_IsRejected(string sequenceToken)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            AssertRejected(
                connection,
                InsertOccurrenceSql(
                    candidateID, seed.MediaFileID, 1, sequenceToken, "1", "CompatibleWithAmbiguity"));
        }

        [Theory]
        [InlineData("Consistent")]
        [InlineData("Inconsistent")]
        [InlineData("")]
        public void Occurrence_UnsupportedOrderAnnotation_IsRejected(string annotation)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            AssertRejected(
                connection,
                InsertOccurrenceSql(candidateID, seed.MediaFileID, 1, "7", "1", annotation));
        }

        /// <remarks>
        /// The ambiguity distinction the frozen design keeps: a several-embedding case and a uniquely
        /// constrained one are different stored facts, even though neither is a match and neither
        /// outranks the other.
        /// </remarks>
        [Theory]
        [InlineData("CompatibleWithAmbiguity")]
        [InlineData("UniquelyConstrained")]
        [InlineData("NoCompatibleArrangement")]
        [InlineData("NotAssessable")]
        public void Occurrence_EachOrderAnnotation_IsStorableWithGrammarPresent(string annotation)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            Execute(
                connection,
                InsertOccurrenceSql(candidateID, seed.MediaFileID, 1, "7", "1", annotation));

            Assert.Equal(
                annotation,
                ScalarRequiredText(
                    connection,
                    "SELECT OrderConsistencyAnnotation FROM AttachmentCandidateOccurrenceEvidence;"));
        }

        /// <remarks>
        /// Sequence evidence is scoped to a physical occurrence, so two copies of one payload
        /// legitimately carry different tokens under one candidate. Collapsing them onto the asset
        /// would invent a token no file had.
        /// </remarks>
        [Fact]
        public void Occurrence_TwoCopiesOfOnePayloadWithDifferentTokens_AreBothStored()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            Execute(
                connection,
                InsertOccurrenceSql(candidateID, seed.MediaFileID, 1, "1", "1", "CompatibleWithAmbiguity"));

            Execute(
                connection,
                InsertOccurrenceSql(
                    candidateID, seed.SecondMediaFileID, 1, "2", "1", "NoCompatibleArrangement"));

            Assert.Equal(2, CountRows(connection, "AttachmentCandidateOccurrenceEvidence"));

            Assert.Equal(
                2,
                ScalarLong(
                    connection,
                    """
                    SELECT COUNT(DISTINCT SequenceToken)
                    FROM AttachmentCandidateOccurrenceEvidence;
                    """));
        }

        [Fact]
        public void Occurrence_SameFileTwiceUnderOneCandidate_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            Execute(
                connection,
                InsertOccurrenceSql(candidateID, seed.MediaFileID, 1, "7", "1", "CompatibleWithAmbiguity"));

            AssertRejected(
                connection,
                InsertOccurrenceSql(candidateID, seed.MediaFileID, 1, "8", "1", "CompatibleWithAmbiguity"));
        }

        // ---------------------------------------------------------------------------------------
        // AttachmentAdjacentDateContext.
        // ---------------------------------------------------------------------------------------

        [Theory]
        [InlineData(0)]
        [InlineData(2)]
        [InlineData(-2)]
        public void AdjacentContext_OffsetThatIsNotOneDayEitherSide_IsRejected(int offset)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(connection, InsertAdjacentContextSql(seed, offset));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(1)]
        public void AdjacentContext_EitherAdjacentDay_IsAllowed(int offset)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            Execute(connection, InsertAdjacentContextSql(seed, offset));

            Assert.Equal(1, CountRows(connection, "AttachmentAdjacentDateContext"));
        }

        [Fact]
        public void AdjacentContext_BothAdjacentDaysForOneAsset_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            Execute(connection, InsertAdjacentContextSql(seed, -1));
            Execute(connection, InsertAdjacentContextSql(seed, 1));

            // Both days are separate qualifying reasons the asset is context at all.
            Assert.Equal(2, CountRows(connection, "AttachmentAdjacentDateContext"));
        }

        [Fact]
        public void AdjacentContext_TheSameDayTwiceForOneAsset_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            Execute(connection, InsertAdjacentContextSql(seed, 1));

            AssertRejected(connection, InsertAdjacentContextSql(seed, 1));
        }

        // ---------------------------------------------------------------------------------------
        // AttachmentReviewEvent.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The rejected values are the ones the frozen design deliberately does not store: whether
        /// every candidate has been disproven is derived against a chosen run, never recorded as a
        /// durable summary a later run could make wrong.
        /// </remarks>
        [Theory]
        [InlineData("ReviewedAllCandidatesIndependentlyDisproven")]
        [InlineData("Reviewed")]
        [InlineData("Confirmed")]
        [InlineData("")]
        public void ReviewEvent_UnsupportedOutcome_IsRejected(string outcome)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentReviewEvent (
                    AttachmentID, SourceRunID, ReviewOutcome, OccurredAtUtc)
                VALUES ({seed.AttachmentID}, {seed.RunID}, '{outcome}', '{ImportedDateTimeUtcText}');
                """);
        }

        [Fact]
        public void ReviewEvent_ReviewedInsufficientEvidenceAgainstARun_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            Execute(
                connection,
                $"""
                INSERT INTO AttachmentReviewEvent (
                    AttachmentID, SourceRunID, ReviewOutcome, ReviewNotes, ActorLabel, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, {seed.RunID}, 'ReviewedInsufficientEvidence',
                    'Looked at every candidate; nothing independently identifies one.', 'operator',
                    '{ImportedDateTimeUtcText}');
                """);

            Assert.Equal(1, CountRows(connection, "AttachmentReviewEvent"));
        }

        [Fact]
        public void ReviewEvent_WithNoSourceRun_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            // A review is always a review of a particular snapshot.
            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentReviewEvent (
                    AttachmentID, SourceRunID, ReviewOutcome, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, NULL, 'ReviewedInsufficientEvidence',
                    '{ImportedDateTimeUtcText}');
                """);
        }

        [Fact]
        public void ReviewEvent_EmptyNotes_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentReviewEvent (
                    AttachmentID, SourceRunID, ReviewOutcome, ReviewNotes, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, {seed.RunID}, 'ReviewedInsufficientEvidence', '',
                    '{ImportedDateTimeUtcText}');
                """);
        }

        // ---------------------------------------------------------------------------------------
        // AttachmentCandidateDispositionEvent.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Disposition_WithAStatedReason_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            Execute(connection, InsertDispositionSql(candidateID, "The depicted document is not this one."));

            Assert.Equal(1, CountRows(connection, "AttachmentCandidateDispositionEvent"));
        }

        [Fact]
        public void Disposition_WithNoReason_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            // Agreeing with a ranking is not disproving a candidate, so the reason cannot be empty.
            AssertRejected(connection, InsertDispositionSql(candidateID, string.Empty));
        }

        [Theory]
        [InlineData("Rejected")]
        [InlineData("Unlikely")]
        [InlineData("")]
        public void Disposition_UnsupportedDisposition_IsRejected(string disposition)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var candidateID = InsertCandidate(connection, seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentCandidateDispositionEvent (
                    AttachmentCandidateID, Disposition, Reason, OccurredAtUtc)
                VALUES ({candidateID}, '{disposition}', 'Because.', '{ImportedDateTimeUtcText}');
                """);
        }

        [Fact]
        public void Disposition_CandidateThatDoesNotExist_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out _);

            AssertRejected(connection, InsertDispositionSql(candidateID: 9999, "Because."));
        }

        // ---------------------------------------------------------------------------------------
        // AttachmentResolutionEvent: basis vocabulary.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// <c>OtherIndependentIndividualFact</c> is the value the frozen design deliberately removed:
        /// an open basis would become the place a ranking gets recorded as though it were identity.
        /// The others are rejected for the same reason — none of them is an independent basis.
        /// </remarks>
        [Theory]
        [InlineData("OtherIndependentIndividualFact")]
        [InlineData("HighestRanked")]
        [InlineData("OnlyCandidate")]
        [InlineData("OrderConsistent")]
        [InlineData("DirectionCompatible")]
        [InlineData("")]
        public void ResolutionEvent_UnsupportedIndependentBasis_IsRejected(string basisType)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                    IndependentBasisDescription, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Confirm', {seed.MediaAssetID}, '{basisType}',
                    'Stated basis.', '{ImportedDateTimeUtcText}');
                """);
        }

        [Theory]
        [InlineData("HumanContentOrContextInspection")]
        [InlineData("ExternalProvenanceOrGroundTruth")]
        public void ResolutionEvent_EachSupportedBasis_IsAllowed(string basisType)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            Execute(connection, InsertConfirmSql(seed, basisType));

            Assert.Equal(1, CountRows(connection, "AttachmentResolutionEvent"));
        }

        [Fact]
        public void ResolutionEvent_ConfirmWithNoBasis_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Confirm', {seed.MediaAssetID},
                    '{ImportedDateTimeUtcText}');
                """);
        }

        [Fact]
        public void ResolutionEvent_ConfirmWithAnEmptyBasisDescription_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                    IndependentBasisDescription, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Confirm', {seed.MediaAssetID},
                    'HumanContentOrContextInspection', '', '{ImportedDateTimeUtcText}');
                """);
        }

        [Fact]
        public void ResolutionEvent_ConfirmWithNoAsset_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                    IndependentBasisDescription, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Confirm', NULL, 'HumanContentOrContextInspection',
                    'Stated basis.', '{ImportedDateTimeUtcText}');
                """);
        }

        // ---------------------------------------------------------------------------------------
        // AttachmentResolutionEvent: supersession.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ResolutionEvent_ConfirmThatSupersedesSomething_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var confirmID = InsertConfirm(connection, seed);

            // A fresh confirmation has nothing to supersede: it is only valid from unresolved.
            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                    IndependentBasisDescription, SupersedesEventID, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Confirm', {seed.MediaAssetID},
                    'HumanContentOrContextInspection', 'Stated basis.', {confirmID},
                    '{ImportedDateTimeUtcText}');
                """);
        }

        [Theory]
        [InlineData("ConfirmReplacement")]
        [InlineData("Revoke")]
        public void ResolutionEvent_ReplacementOrRevocationWithNothingSuperseded_IsRejected(
            string eventType)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            InsertConfirm(connection, seed);

            // Both must say which event they act against.
            AssertRejected(
                connection,
                eventType == "Revoke"
                    ? $"""
                       INSERT INTO AttachmentResolutionEvent (
                           AttachmentID, EventType, RevocationReason, SupersedesEventID, OccurredAtUtc)
                       VALUES (
                           {seed.AttachmentID}, 'Revoke', 'Found the real file elsewhere.', NULL,
                           '{ImportedDateTimeUtcText}');
                       """
                    : $"""
                       INSERT INTO AttachmentResolutionEvent (
                           AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                           IndependentBasisDescription, SupersedesEventID, OccurredAtUtc)
                       VALUES (
                           {seed.AttachmentID}, 'ConfirmReplacement', {seed.SecondMediaAssetID},
                           'HumanContentOrContextInspection', 'Stated basis.', NULL,
                           '{ImportedDateTimeUtcText}');
                       """);
        }

        [Fact]
        public void ResolutionEvent_ReplacementSupersedingTheAttachmentsOwnConfirm_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var confirmID = InsertConfirm(connection, seed);

            Execute(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                    IndependentBasisDescription, SupersedesEventID, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'ConfirmReplacement', {seed.SecondMediaAssetID},
                    'ExternalProvenanceOrGroundTruth', 'A record names the other payload.',
                    {confirmID}, '{ImportedDateTimeUtcText}');
                """);

            Assert.Equal(2, CountRows(connection, "AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// The invariant the composite foreign key exists for. Without it a revocation or replacement
        /// on one attachment could name an event belonging to a different attachment entirely, which
        /// the frozen design never contemplates.
        /// </remarks>
        [Theory]
        [InlineData("ConfirmReplacement")]
        [InlineData("Revoke")]
        public void ResolutionEvent_SupersedingAnotherAttachmentsEvent_IsRejected(string eventType)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var otherConfirmID = InsertConfirm(connection, seed);

            AssertRejected(
                connection,
                eventType == "Revoke"
                    ? $"""
                       INSERT INTO AttachmentResolutionEvent (
                           AttachmentID, EventType, RevocationReason, SupersedesEventID, OccurredAtUtc)
                       VALUES (
                           {seed.SecondAttachmentID}, 'Revoke', 'Not this one after all.',
                           {otherConfirmID}, '{ImportedDateTimeUtcText}');
                       """
                    : $"""
                       INSERT INTO AttachmentResolutionEvent (
                           AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                           IndependentBasisDescription, SupersedesEventID, OccurredAtUtc)
                       VALUES (
                           {seed.SecondAttachmentID}, 'ConfirmReplacement', {seed.SecondMediaAssetID},
                           'HumanContentOrContextInspection', 'Stated basis.', {otherConfirmID},
                           '{ImportedDateTimeUtcText}');
                       """);
        }

        // ---------------------------------------------------------------------------------------
        // AttachmentResolutionEvent: revocation.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ResolutionEvent_RevokeWithAStatedReason_IsAllowed()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var confirmID = InsertConfirm(connection, seed);

            Execute(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, RevocationReason, SupersedesEventID, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Revoke', 'The inspection that justified it was mistaken.',
                    {confirmID}, '{ImportedDateTimeUtcText}');
                """);

            Assert.Equal(2, CountRows(connection, "AttachmentResolutionEvent"));
        }

        [Theory]
        [InlineData("NULL")]
        [InlineData("''")]
        public void ResolutionEvent_RevokeWithNoReason_IsRejected(string revocationReason)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var confirmID = InsertConfirm(connection, seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, RevocationReason, SupersedesEventID, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Revoke', {revocationReason}, {confirmID},
                    '{ImportedDateTimeUtcText}');
                """);
        }

        /// <remarks>
        /// A revocation reason answers why a resolution is no longer believed, which is a different
        /// question from what established it. Carrying an independent basis as well would blur the two
        /// for every later reader.
        /// </remarks>
        [Fact]
        public void ResolutionEvent_RevokeCarryingAnIndependentBasis_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var confirmID = InsertConfirm(connection, seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, IndependentBasisType, IndependentBasisDescription,
                    RevocationReason, SupersedesEventID, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Revoke', 'HumanContentOrContextInspection', 'Stated basis.',
                    'No longer believed.', {confirmID}, '{ImportedDateTimeUtcText}');
                """);
        }

        [Fact]
        public void ResolutionEvent_ConfirmCarryingARevocationReason_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                    IndependentBasisDescription, RevocationReason, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Confirm', {seed.MediaAssetID},
                    'HumanContentOrContextInspection', 'Stated basis.', 'Also revoked?',
                    '{ImportedDateTimeUtcText}');
                """);
        }

        [Fact]
        public void ResolutionEvent_RevokeNamingAnAsset_IsRejected()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var confirmID = InsertConfirm(connection, seed);

            // A revocation leaves the attachment unresolved; it does not name a payload.
            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, RevocationReason, SupersedesEventID,
                    OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Revoke', {seed.MediaAssetID}, 'Mistaken.', {confirmID},
                    '{ImportedDateTimeUtcText}');
                """);
        }

        [Theory]
        [InlineData("Resolve")]
        [InlineData("Unresolve")]
        [InlineData("confirm")]
        [InlineData("")]
        public void ResolutionEvent_UnsupportedEventType_IsRejected(string eventType)
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            AssertRejected(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                    IndependentBasisDescription, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, '{eventType}', {seed.MediaAssetID},
                    'HumanContentOrContextInspection', 'Stated basis.', '{ImportedDateTimeUtcText}');
                """);
        }

        /// <remarks>
        /// The identifier is the replay ordering authority, so it has to be assigned in write order
        /// without the caller supplying one. Plain <c>INTEGER PRIMARY KEY</c> gives that, and the
        /// table never deletes a row, which is why no <c>AUTOINCREMENT</c> is declared.
        /// </remarks>
        [Fact]
        public void ResolutionEvent_IdentifiersAreAssignedInWriteOrder()
        {
            using var workspace = new TemporaryWorkspaceDatabase();
            using var connection = Seed(workspace, out var seed);

            var confirmID = InsertConfirm(connection, seed);

            Execute(
                connection,
                $"""
                INSERT INTO AttachmentResolutionEvent (
                    AttachmentID, EventType, RevocationReason, SupersedesEventID, OccurredAtUtc)
                VALUES (
                    {seed.AttachmentID}, 'Revoke', 'Mistaken.', {confirmID},
                    '{ImportedDateTimeUtcText}');
                """);

            var revokeID = ScalarLong(
                connection,
                "SELECT AttachmentResolutionEventID FROM AttachmentResolutionEvent WHERE EventType = 'Revoke';");

            Assert.True(revokeID > confirmID);
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The identifiers a constraint test needs to hang a Phase 7 row off.
        /// </summary>
        private sealed record SeedIdentifiers(
            long RunID,
            long AttachmentID,
            long SecondAttachmentID,
            long MediaAssetID,
            long SecondMediaAssetID,
            long MediaFileID,
            long SecondMediaFileID);

        /// <summary>
        /// Creates a version-3 workspace holding the minimum real rows a Phase 7 row can reference:
        /// two attachments on their own messages, two payloads, and two physical copies of the first
        /// payload.
        /// </summary>
        private static SqliteConnection Seed(
            TemporaryWorkspaceDatabase workspace, out SeedIdentifiers seed)
        {
            WorkspaceDatabase.Initialise(workspace.DatabasePath);

            var connection = WorkspaceDatabase.OpenConnection(workspace.DatabasePath);

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
                VALUES (1, 1, 1, '{MessageDateTimeText}', 1, 'User', 'one', 'one', 1, 1);

                INSERT INTO Message (
                    ConversationID, ImportSourceID, SequenceNumber, MessageDateTimeLocal,
                    SenderParticipantID, MessageType, MessageContent, RawContent,
                    SourceLineStart, SourceLineEnd)
                VALUES (1, 1, 2, '{MessageDateTimeText}', 1, 'User', 'two', 'two', 2, 2);

                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                VALUES (1, 1, 'Unresolved');

                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                VALUES (2, 1, 'Unresolved');

                INSERT INTO MediaSource (DisplayName, SourceType, RootPath, ImportedDateTimeUtc)
                VALUES ('Copy', 'PhoneCopy', 'MediaRoot', '{ImportedDateTimeUtcText}');

                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes)
                VALUES ('{ValidHash}', 'Image', 1);

                INSERT INTO MediaAsset (SHA256, MediaType, SizeBytes)
                VALUES ('BBBB000000000000000000000000000000000000000000000000000000000002', 'Image', 2);

                INSERT INTO MediaFile (MediaSourceID, RelativePath, FileName, SizeBytes, MediaType)
                VALUES (1, 'Images/one.jpg', 'one.jpg', 1, 'Image');

                INSERT INTO MediaFile (MediaSourceID, RelativePath, FileName, SizeBytes, MediaType)
                VALUES (1, 'Images/Sent/one.jpg', 'one.jpg', 1, 'Image');

                INSERT INTO Phase7Run (
                    StartedAtUtc, CompletedAtUtc, Status, PreRunWorkspaceFileSha256,
                    RankingDimensionSetVersion)
                VALUES (
                    '{ImportedDateTimeUtcText}', '{ImportedDateTimeUtcText}', 'Completed',
                    '{RunHash}', 'stage-7.0');
                """);

            seed = new SeedIdentifiers(
                RunID: ScalarLong(connection, "SELECT RunID FROM Phase7Run;"),
                AttachmentID: ScalarLong(
                    connection, "SELECT AttachmentID FROM Attachment ORDER BY AttachmentID LIMIT 1;"),
                SecondAttachmentID: ScalarLong(
                    connection,
                    "SELECT AttachmentID FROM Attachment ORDER BY AttachmentID DESC LIMIT 1;"),
                MediaAssetID: ScalarLong(
                    connection, "SELECT MediaAssetID FROM MediaAsset ORDER BY MediaAssetID LIMIT 1;"),
                SecondMediaAssetID: ScalarLong(
                    connection,
                    "SELECT MediaAssetID FROM MediaAsset ORDER BY MediaAssetID DESC LIMIT 1;"),
                MediaFileID: ScalarLong(
                    connection, "SELECT MediaFileID FROM MediaFile ORDER BY MediaFileID LIMIT 1;"),
                SecondMediaFileID: ScalarLong(
                    connection, "SELECT MediaFileID FROM MediaFile ORDER BY MediaFileID DESC LIMIT 1;"));

            return connection;
        }

        private static string InsertSnapshotSql(long runID, long attachmentID, int exactCount = 1) =>
            $"""
            INSERT INTO AttachmentEvidenceSnapshot (
                RunID, AttachmentID, ExactDateCandidateCount, AdjacentDateContextCount,
                HasAnyCompatibleAnnotatedCandidate, HasAnyContradictoryOnlyAnnotatedCandidate,
                IsWithinDirectionImpossibleGroup, IsForcedPositionGroupShape,
                RemainingCompatiblePositionCountUnderHypothesis)
            VALUES ({runID}, {attachmentID}, {exactCount}, 0, 0, 0, 0, 0, NULL);
            """;

        private static string InsertCandidateSql(SeedIdentifiers seed) =>
            $"""
            INSERT INTO AttachmentCandidate (
                RunID, AttachmentID, MediaAssetID, DirectionCompatibilityAnnotation)
            VALUES ({seed.RunID}, {seed.AttachmentID}, {seed.MediaAssetID}, 'Compatible')
            RETURNING AttachmentCandidateID;
            """;

        private static long InsertCandidate(SqliteConnection connection, SeedIdentifiers seed) =>
            ScalarLong(connection, InsertCandidateSql(seed));

        private static string InsertOccurrenceSql(
            long candidateID,
            long mediaFileID,
            int grammarSupported,
            string sequenceToken,
            string uniqueToPayload,
            string orderAnnotation) =>
            $"""
            INSERT INTO AttachmentCandidateOccurrenceEvidence (
                AttachmentCandidateID, MediaFileID, TokenGrammarSupported, SequenceToken,
                DateTokenKeyUniqueToPayload, OrderConsistencyAnnotation)
            VALUES (
                {candidateID}, {mediaFileID}, {grammarSupported}, {sequenceToken},
                {uniqueToPayload}, '{orderAnnotation}');
            """;

        private static string InsertAdjacentContextSql(SeedIdentifiers seed, int offset) =>
            $"""
            INSERT INTO AttachmentAdjacentDateContext (
                RunID, AttachmentID, MediaAssetID, AdjacentOffsetDays)
            VALUES ({seed.RunID}, {seed.AttachmentID}, {seed.MediaAssetID}, {offset});
            """;

        private static string InsertDispositionSql(long candidateID, string reason) =>
            $"""
            INSERT INTO AttachmentCandidateDispositionEvent (
                AttachmentCandidateID, Disposition, Reason, OccurredAtUtc)
            VALUES ({candidateID}, 'IndependentlyDisproven', '{reason}', '{ImportedDateTimeUtcText}');
            """;

        private static string InsertConfirmSql(
            SeedIdentifiers seed, string basisType = "HumanContentOrContextInspection") =>
            $"""
            INSERT INTO AttachmentResolutionEvent (
                AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                IndependentBasisDescription, OccurredAtUtc)
            VALUES (
                {seed.AttachmentID}, 'Confirm', {seed.MediaAssetID}, '{basisType}',
                'A person examined the content and can say why this is the one.',
                '{ImportedDateTimeUtcText}')
            RETURNING AttachmentResolutionEventID;
            """;

        private static long InsertConfirm(SqliteConnection connection, SeedIdentifiers seed) =>
            ScalarLong(connection, InsertConfirmSql(seed));

        private static void AssertRejected(SqliteConnection connection, string sql) =>
            Assert.Throws<SqliteException>(() => Execute(connection, sql));
    }
}
