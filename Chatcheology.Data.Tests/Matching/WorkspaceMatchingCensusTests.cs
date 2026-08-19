using static Chatcheology.Data.Tests.Matching.MatchingTestData;

namespace Chatcheology.Data.Tests.Matching
{
    /// <summary>
    /// Tests the aggregate figures the first real run will be read through.
    /// </summary>
    /// <remarks>
    /// The census is what makes a broad, unhelpful-looking result legible: how much of the
    /// conversation the archive can speak to at all, how thickly assets sit on the dates it can,
    /// and which source supplied what. None of it is a success rate, and a census reporting large
    /// candidate sets is a correct answer about the archive rather than a failure of the analysis.
    /// </remarks>
    public class WorkspaceMatchingCensusTests
    {
        [Fact]
        public void UnresolvedAttachmentsOutsideTheConversation_AreReportedSeparately()
        {
            using var workspace = new MatchingTestWorkspace();

            workspace.AddMediaAttachment(MessageDate);
            workspace.AddMediaAttachment(MessageDate);

            workspace.AddMediaAttachment(
                MessageDate,
                MatchingTestWorkspace.OutsiderParticipantID,
                MatchingTestWorkspace.OtherConversationID);

            var census = Analyse(workspace).Census;

            Assert.Equal(2, census.ConversationUnresolvedAttachmentCount);
            Assert.Equal(3, census.WorkspaceUnresolvedAttachmentCount);
            Assert.Equal(1, census.UnresolvedAttachmentsOutsideAnalysedConversation);
        }

        [Fact]
        public void CandidateCountBands_AccountForEveryAnalysedAttachment()
        {
            using var workspace = new MatchingTestWorkspace();

            workspace.AddMediaAttachment(MessageDate);
            workspace.AddMediaAttachment(NextDay);
            workspace.AddMediaAttachment(UnrelatedDate);

            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);
            workspace.AddAssetWithCopy(sourceID, Hash(2), NextDay);
            workspace.AddAssetWithCopy(sourceID, Hash(3), NextDay);
            workspace.AddAssetWithCopy(sourceID, Hash(4), NextDay);

            var census = Analyse(workspace).Census;
            var bands = census.ExactDateCandidateCountDistribution;

            Assert.Equal(3, bands.Total);
            Assert.Equal(1, bands.Zero);
            Assert.Equal(1, bands.One);
            Assert.Equal(1, bands.ThreeToFive);

            Assert.Equal(2, census.AttachmentsWithExactDateCandidates);
            Assert.Equal(1, census.AttachmentsWithNoExactDateCandidates);
            Assert.Equal(1, census.UniqueExactDateCandidateCount);
        }

        [Fact]
        public void MessageDatesAreCountedForTheConversationAndForItsAttachments()
        {
            using var workspace = new MatchingTestWorkspace();

            workspace.AddMediaAttachment(MessageDate);
            workspace.AddMediaAttachment(MessageDate, time: "18:00:00");
            workspace.AddMediaAttachment(NextDay);

            // A message on a third date that carries no attachment at all.
            workspace.AddMessage(UnrelatedDate);

            var census = Analyse(workspace).Census;

            Assert.Equal(3, census.DistinctConversationMessageDates);
            Assert.Equal(2, census.DistinctAttachmentMessageDates);
        }

        [Fact]
        public void AttachmentsOnDatesTheArchiveCannotSpeakTo_AreCounted()
        {
            using var workspace = new MatchingTestWorkspace();

            workspace.AddMediaAttachment(MessageDate);
            workspace.AddMediaAttachment(UnrelatedDate);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);

            var census = Analyse(workspace).Census;

            Assert.Equal(1, census.AttachmentCountOnDatesWithNoDatedEligibleMedia);
            Assert.Equal(
                census.AttachmentsWithNoExactDateCandidates,
                census.AttachmentCountOnDatesWithNoDatedEligibleMedia);
        }

        /// <remarks>
        /// Two sources holding the same payload on the same date make one relationship and credit
        /// both sources, so the per-source figures deliberately sum to more than the relationship
        /// count. Reading them as a partition would double-count the archive.
        /// </remarks>
        [Fact]
        public void EachSourceSupplyingASupportingCopy_IsCreditedForTheRelationship()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var currentPhone = workspace.AddMediaSource("Current phone");
            var previousPhone = workspace.AddMediaSource("Previous phone");

            var mediaAssetID = workspace.AddAssetWithCopy(currentPhone, Hash(1), MessageDate);
            workspace.AddMediaFile(previousPhone, mediaAssetID, Hash(1), MessageDate);

            // A copy on this source alone, dated where nothing asks about it.
            workspace.AddAssetWithCopy(previousPhone, Hash(2), UnrelatedDate);

            var census = Analyse(workspace).Census;

            Assert.Equal(1, census.ExactCandidateRelationsUnknown);

            var contributions = census.MediaSourceContributions;

            Assert.Equal([currentPhone, previousPhone], contributions.Select(c => c.MediaSourceID));

            Assert.Equal(1, contributions[0].MediaFileCount);
            Assert.Equal(1, contributions[0].MediaFileWithFileDateCount);
            Assert.Equal(1, contributions[0].DistinctNonZeroAssetsWithFileDate);
            Assert.Equal(1, contributions[0].ExactCandidateRelationsContributed);

            Assert.Equal(2, contributions[1].MediaFileCount);
            Assert.Equal(2, contributions[1].DistinctNonZeroAssetsWithFileDate);
            Assert.Equal(1, contributions[1].ExactCandidateRelationsContributed);
        }

        [Fact]
        public void ExactCandidateRelations_AreCountedByCandidateMediaType()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, mediaType: "Image");
            workspace.AddAssetWithCopy(sourceID, Hash(2), MessageDate, mediaType: "Image");
            workspace.AddAssetWithCopy(sourceID, Hash(3), MessageDate, mediaType: "Video");
            workspace.AddAssetWithCopy(sourceID, Hash(4), MessageDate, mediaType: "Audio");
            workspace.AddAssetWithCopy(sourceID, Hash(5), MessageDate, mediaType: "Document");
            workspace.AddAssetWithCopy(sourceID, Hash(6), MessageDate, mediaType: "Unknown");

            var byType = Analyse(workspace).Census.ExactCandidateRelationsByMediaType;

            Assert.Equal(2, byType.Image);
            Assert.Equal(1, byType.Video);
            Assert.Equal(1, byType.Audio);
            Assert.Equal(1, byType.Document);
            Assert.Equal(1, byType.Unknown);
            Assert.Equal(6, byType.Total);
        }

        /// <remarks>
        /// Four dates carrying one, two, three and four eligible assets. The median of an even
        /// number of dates is the lower of the two middle values, which is stated so two runs of
        /// this census can be compared.
        /// </remarks>
        [Fact]
        public void AssetsPerDate_ReportsTheSpreadWithALowerMedian()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var hash = 0;

            for (var assetsOnDate = 1; assetsOnDate <= 4; assetsOnDate++)
            {
                var date = MessageDate.AddDays(assetsOnDate * 10);

                for (var asset = 0; asset < assetsOnDate; asset++)
                {
                    workspace.AddAssetWithCopy(sourceID, Hash(++hash), date);
                }
            }

            var density = Analyse(workspace).Census.AssetsPerDate;

            Assert.Equal(4, density.DatedEligibleMediaDateCount);
            Assert.Equal(1, density.Minimum);
            Assert.Equal(2, density.Median);
            Assert.Equal(4, density.Maximum);
        }

        [Fact]
        public void UndatedAndZeroByteAssets_AreReportedRatherThanOffered()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);
            workspace.AddAssetWithCopy(sourceID, Hash(2), fileDate: null);
            workspace.AddAssetWithCopy(sourceID, Hash(3), fileDate: null);

            workspace.AddAssetWithCopy(
                sourceID, Hash(4), MessageDate, mediaType: "Unknown", sizeBytes: 0);

            var census = Analyse(workspace).Census;

            Assert.Equal(2, census.NoDateEvidenceAssetPoolCount);
            Assert.Equal(2, census.MediaFileWithFileDateCount);
            Assert.Equal(2, census.MediaFileWithNullFileDateCount);
            Assert.Equal(1, census.ZeroByteAssetsExcluded);
            Assert.Equal(1, census.DistinctCandidateMediaAssetsOverall);
        }
    }
}
