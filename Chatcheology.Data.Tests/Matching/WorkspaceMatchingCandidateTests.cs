using Chatcheology.Data.Matching;
using static Chatcheology.Data.Tests.Matching.MatchingTestData;

namespace Chatcheology.Data.Tests.Matching
{
    /// <summary>
    /// Tests which assets become candidates for an attachment, and on what date evidence.
    /// </summary>
    /// <remarks>
    /// The rules being exercised are the conservative half of the engine: one payload is one
    /// candidate however many copies of it survived, an asset with no payload is no candidate at
    /// all, and evidence a day either side of the message is kept apart from evidence on the day
    /// itself rather than quietly merged into it.
    /// </remarks>
    public class WorkspaceMatchingCandidateTests
    {
        // ---------------------------------------------------------------------------------------
        // Candidate identity and deduplication.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void AssetWithOneFile_IsOneCandidate()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);

            var analysis = AnalyseOne(workspace);

            var candidate = Assert.Single(analysis.ExactDateCandidates);

            Assert.Equal(mediaAssetID, candidate.MediaAssetID);
            Assert.Equal(1, candidate.PhysicalCopyCount);
            Assert.Equal(1, candidate.DistinctMediaSourceCount);
        }

        [Fact]
        public void AssetWithThreePhysicalCopies_IsStillOneCandidate()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var mediaAssetID = AddAssetOnThreeSources(workspace);

            var analysis = AnalyseOne(workspace);

            var candidate = Assert.Single(analysis.ExactDateCandidates);

            Assert.Equal(mediaAssetID, candidate.MediaAssetID);
        }

        [Fact]
        public void AssetWithThreePhysicalCopies_ReportsCopyAndSourceCounts()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            AddAssetOnThreeSources(workspace);

            var candidate = Assert.Single(AnalyseOne(workspace).ExactDateCandidates);

            Assert.Equal(3, candidate.PhysicalCopyCount);
            Assert.Equal(3, candidate.DistinctMediaSourceCount);
            Assert.Equal(3, candidate.SupportingPhysicalCopyCount);
            Assert.Equal(3, candidate.SupportingMediaSourceCount);
        }

        [Fact]
        public void DuplicateCopies_DoNotInflateTheCandidateCount()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            AddAssetOnThreeSources(workspace);

            var census = Analyse(workspace).Census;

            Assert.Equal(1, census.ExactCandidateRelationsUnknown);
            Assert.Equal(1, census.DistinctCandidateMediaAssetsOverall);
            Assert.Equal(1, census.UniqueExactDateCandidateCount);
        }

        [Fact]
        public void MultipleCopiesOnTheSameDate_AreOneCandidate()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);
            workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), MessageDate);

            var candidate = Assert.Single(AnalyseOne(workspace).ExactDateCandidates);

            Assert.Equal(2, candidate.PhysicalCopyCount);
            Assert.Equal(1, candidate.DistinctMediaSourceCount);
        }

        // ---------------------------------------------------------------------------------------
        // Zero-byte assets.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void ZeroByteAsset_IsNotACandidate()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithCopy(
                sourceID, Hash(1), MessageDate, mediaType: "Unknown", sizeBytes: 0);

            var analysis = AnalyseOne(workspace);

            Assert.Empty(analysis.ExactDateCandidates);
            Assert.Empty(analysis.AdjacentDateCandidates);
        }

        [Fact]
        public void ZeroByteAsset_IsReportedAsExcludedWithItsPhysicalCopies()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();

            var mediaAssetID = workspace.AddAssetWithCopy(
                sourceID, Hash(1), MessageDate, mediaType: "Unknown", sizeBytes: 0);

            workspace.AddMediaFile(
                sourceID, mediaAssetID, Hash(1), MessageDate, mediaType: "Unknown", sizeBytes: 0);

            var census = Analyse(workspace).Census;

            Assert.Equal(1, census.ZeroByteAssetsExcluded);
            Assert.Equal(2, census.ZeroBytePhysicalFilesRepresentedByExcludedAsset);
        }

        [Fact]
        public void NonZeroUnknownAsset_RemainsEligible()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();

            var mediaAssetID = workspace.AddAssetWithCopy(
                sourceID, Hash(1), MessageDate, mediaType: "Unknown", sizeBytes: 12);

            var candidate = Assert.Single(AnalyseOne(workspace).ExactDateCandidates);

            Assert.Equal(mediaAssetID, candidate.MediaAssetID);
            Assert.Equal(Chatcheology.Data.Media.MediaType.Unknown, candidate.MediaType);
        }

        // ---------------------------------------------------------------------------------------
        // Date evidence.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void CopyOnTheMessageDate_IsAnExactDateCandidate()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);

            var analysis = AnalyseOne(workspace);

            var candidate = Assert.Single(analysis.ExactDateCandidates);

            Assert.True(candidate.HasExactMessageDateCopy);
            Assert.False(candidate.HasPreviousDayCopy);
            Assert.False(candidate.HasNextDayCopy);
            Assert.Empty(analysis.AdjacentDateCandidates);
        }

        [Fact]
        public void AssetDatedOnSeveralDays_AggregatesItsFactsAgainstTheMessageDate()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);
            workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), PreviousDay);
            workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), UnrelatedDate);

            var candidate = Assert.Single(AnalyseOne(workspace).ExactDateCandidates);

            Assert.True(candidate.HasExactMessageDateCopy);
            Assert.True(candidate.HasPreviousDayCopy);
            Assert.False(candidate.HasNextDayCopy);
            Assert.Equal(3, candidate.PhysicalCopyCount);

            // Only the copy on the message's own date supports this relationship.
            Assert.Equal(1, candidate.SupportingPhysicalCopyCount);
        }

        [Fact]
        public void CopyWithNoFileDate_GivesNoDateEvidence()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), fileDate: null);

            var analysis = AnalyseOne(workspace);

            Assert.Empty(analysis.ExactDateCandidates);
            Assert.Empty(analysis.AdjacentDateCandidates);

            var census = Analyse(workspace).Census;

            Assert.Equal(1, census.NoDateEvidenceAssetPoolCount);
            Assert.Equal(1, census.MediaFileWithNullFileDateCount);
            Assert.Equal(0, census.MediaFileWithFileDateCount);
        }

        [Fact]
        public void CopyOnThePreviousDayOnly_GoesToTheAdjacentSet()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddAssetWithCopy(sourceID, Hash(1), PreviousDay);

            var analysis = AnalyseOne(workspace);

            Assert.Empty(analysis.ExactDateCandidates);

            var candidate = Assert.Single(analysis.AdjacentDateCandidates);

            Assert.Equal(mediaAssetID, candidate.MediaAssetID);
            Assert.True(candidate.HasPreviousDayCopy);
            Assert.False(candidate.HasNextDayCopy);
            Assert.False(candidate.HasExactMessageDateCopy);
        }

        [Fact]
        public void CopyOnTheNextDayOnly_GoesToTheAdjacentSet()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), NextDay);

            var analysis = AnalyseOne(workspace);

            Assert.Empty(analysis.ExactDateCandidates);

            var candidate = Assert.Single(analysis.AdjacentDateCandidates);

            Assert.True(candidate.HasNextDayCopy);
            Assert.False(candidate.HasPreviousDayCopy);
        }

        [Fact]
        public void AssetWithAnExactDateCopy_IsNeverAlsoAnAdjacentCandidate()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);
            workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), NextDay);

            var analysis = AnalyseOne(workspace);

            Assert.Equal(mediaAssetID, Assert.Single(analysis.ExactDateCandidates).MediaAssetID);
            Assert.Empty(analysis.AdjacentDateCandidates);
        }

        [Fact]
        public void AdjacentEvidence_IsNotPromotedWhenTheExactSetIsEmpty()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), PreviousDay);

            var run = Analyse(workspace);

            Assert.Equal(0, run.Census.AttachmentsWithExactDateCandidates);
            Assert.Equal(1, run.Census.AttachmentsWithNoExactDateCandidates);
            Assert.Equal(1, run.Census.AdjacentDateOnlyAttachmentCount);
            Assert.Empty(Assert.Single(run.Analyses).ExactDateCandidates);
        }

        [Fact]
        public void AssetOnBothAdjacentDays_IsOneCandidateSupportedByBoth()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddAssetWithCopy(sourceID, Hash(1), PreviousDay);
            workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), NextDay);

            var candidate = Assert.Single(AnalyseOne(workspace).AdjacentDateCandidates);

            Assert.True(candidate.HasPreviousDayCopy);
            Assert.True(candidate.HasNextDayCopy);
            Assert.Equal(2, candidate.SupportingPhysicalCopyCount);
        }

        // ---------------------------------------------------------------------------------------
        // Ordering and determinism.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Attachments_AreAnalysedInMessageThenOrdinalOrder()
        {
            using var workspace = new MatchingTestWorkspace();

            var firstMessageOrdinalOne =
                workspace.AddMediaAttachment(MessageDate, time: "09:00:00");

            var secondMessageOrdinalOne =
                workspace.AddMediaAttachment(MessageDate, time: "10:00:00");

            // Added last and carrying the highest identifier, but it belongs to the earlier
            // message: attachment order follows the conversation, not the insertion.
            var firstMessageOrdinalTwo = AddSecondAttachmentToFirstMessage(workspace);

            var analyses = Analyse(workspace).Analyses;

            Assert.Equal(
                [firstMessageOrdinalOne, firstMessageOrdinalTwo, secondMessageOrdinalOne],
                analyses.Select(analysis => analysis.AttachmentID).ToArray());

            Assert.Equal([1, 2, 1], analyses.Select(analysis => analysis.Ordinal).ToArray());
        }

        [Fact]
        public void ExactDateCandidates_AreOrderedByMediaAssetIDAscending()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();

            var third = workspace.AddAssetWithCopy(sourceID, Hash(3), MessageDate);
            var first = workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);
            var second = workspace.AddAssetWithCopy(sourceID, Hash(2), MessageDate);

            var ordered = new[] { third, first, second }.Order().ToArray();

            var analysis = AnalyseOne(workspace);

            Assert.Equal(
                ordered,
                analysis.ExactDateCandidates.Select(candidate => candidate.MediaAssetID).ToArray());
        }

        [Fact]
        public void AdjacentDateCandidates_AreOrderedByMediaAssetIDAscending()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();

            var fromNextDay = workspace.AddAssetWithCopy(sourceID, Hash(3), NextDay);
            var fromPreviousDay = workspace.AddAssetWithCopy(sourceID, Hash(1), PreviousDay);
            var alsoNextDay = workspace.AddAssetWithCopy(sourceID, Hash(2), NextDay);

            var ordered = new[] { fromNextDay, fromPreviousDay, alsoNextDay }.Order().ToArray();

            var analysis = AnalyseOne(workspace);

            Assert.Equal(
                ordered,
                analysis.AdjacentDateCandidates
                    .Select(candidate => candidate.MediaAssetID)
                    .ToArray());
        }

        [Fact]
        public void SameLogicalDataInsertedInADifferentOrder_ProducesIdenticalOutput()
        {
            var forwards = DescribeRun(insertAscending: true);
            var backwards = DescribeRun(insertAscending: false);

            Assert.Equal(forwards, backwards);
        }

        /// <summary>
        /// Builds the same logical workspace with its media rows inserted in opposite orders and
        /// renders what the analysis produced, so the two runs can be compared as text.
        /// </summary>
        private static string DescribeRun(bool insertAscending)
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();

            var hashes = insertAscending ? new[] { 1, 2, 3 } : [3, 2, 1];

            foreach (var hash in hashes)
            {
                workspace.AddAssetWithCopy(sourceID, Hash(hash), MessageDate);
            }

            var analysis = AnalyseOne(workspace);

            return string.Join(
                "|",
                analysis.ExactDateCandidates.Select(
                    candidate =>
                        $"{candidate.SizeBytes}:{candidate.PhysicalCopyCount}:" +
                        $"{candidate.SupportingMediaSourceCount}:{candidate.DirectionCompatibility}"));
        }

        /// <summary>
        /// One payload surviving as three physical copies across three sources, all dated to the
        /// message's own date.
        /// </summary>
        private static long AddAssetOnThreeSources(MatchingTestWorkspace workspace)
        {
            var currentPhone = workspace.AddMediaSource("Current phone");
            var previousPhone = workspace.AddMediaSource("Previous phone");
            var recovered = workspace.AddMediaSource("Recovered copy");

            var mediaAssetID = workspace.AddAssetWithCopy(currentPhone, Hash(1), MessageDate);

            workspace.AddMediaFile(previousPhone, mediaAssetID, Hash(1), MessageDate);
            workspace.AddMediaFile(recovered, mediaAssetID, Hash(1), MessageDate);

            return mediaAssetID;
        }

        /// <summary>
        /// Adds a second attachment to the first message, which the supported export format never
        /// produces but the schema deliberately allows.
        /// </summary>
        private static long AddSecondAttachmentToFirstMessage(MatchingTestWorkspace workspace)
        {
            workspace.Execute(
                """
                INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                SELECT MIN(MessageID), 2, 'Unresolved' FROM Message;
                """);

            return workspace.ScalarLong("SELECT MAX(AttachmentID) FROM Attachment;");
        }
    }
}
