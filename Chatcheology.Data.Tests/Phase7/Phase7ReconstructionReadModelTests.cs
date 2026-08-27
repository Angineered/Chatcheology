using System.Reflection;
using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// What the minimal reconstruction model reports, in every category and where categories overlap.
    /// </summary>
    public class Phase7ReconstructionReadModelTests
    {
        [Fact]
        public void Read_AResolvedAttachment_ReturnsTheStoredAssetDescriptor()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            fixture.ExecuteDirect(
                $"""
                UPDATE MediaAsset
                SET MediaType = 'Video', SizeBytes = 4096, DurationMS = 12000,
                    Width = 1920, Height = 1080
                WHERE MediaAssetID = {asset};
                """);

            fixture.Confirm(attachment, asset);

            var state = fixture.Reconstruct(attachment);

            Assert.Equal(Phase7ResolutionStatus.Resolved, state.Status);
            Assert.Null(state.PlaceholderReason);

            var descriptor = Assert.IsType<Phase7ResolvedAssetDescriptor>(state.ResolvedAsset);

            Assert.Equal(asset, descriptor.MediaAssetID);
            Assert.Equal("Video", descriptor.MediaType);
            Assert.Equal(4096, descriptor.SizeBytes);
            Assert.Equal(12000, descriptor.DurationMS);
            Assert.Equal(1920, descriptor.Width);
            Assert.Equal(1080, descriptor.Height);
        }

        [Fact]
        public void Read_AResolvedAttachmentWithNoDimensions_ReportsThemAsAbsent()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            fixture.Confirm(attachment, asset);

            var descriptor = fixture.Reconstruct(attachment).ResolvedAsset!;

            Assert.Null(descriptor.DurationMS);
            Assert.Null(descriptor.Width);
            Assert.Null(descriptor.Height);
        }

        /// <remarks>
        /// The descriptor exposes exactly six fields. No hash, no path, no file name — the asset table
        /// carries no path or name to expose, and the content hash is deliberately left out.
        /// </remarks>
        [Fact]
        public void ResolvedAssetDescriptor_ExposesNoHashPathOrFileName()
        {
            var properties = typeof(Phase7ResolvedAssetDescriptor)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order()
                .ToList();

            Assert.Equal(
                ["DurationMS", "Height", "MediaAssetID", "MediaType", "SizeBytes", "Width"],
                properties);
        }

        [Fact]
        public void Read_NeverEvaluated()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            var outsider = fixture.Evidence.AddOutgoingInOtherConversation(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var state = fixture.Reconstruct(outsider);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.NeverEvaluated, state.PlaceholderReason);

            Assert.Null(state.EvidenceRunID);
            Assert.Null(state.ExactDateCandidateCount);
            Assert.Null(state.AdjacentDateContextCount);
            Assert.Null(state.IsWithinDirectionImpossibleGroup);
            Assert.Null(state.CandidateTierCount);
            Assert.False(state.HasCurrentReviewedInsufficientEvidence);
            Assert.False(state.AllCurrentCandidatesIndependentlyDisproven);
        }

        [Fact]
        public void Read_NoCandidates_WhenThereIsNoContextEither()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(Phase7TestFixture.Day(10), isSent: true, 1);

            var run = fixture.Evidence.Run();

            var state = fixture.Reconstruct(attachment);

            Assert.Equal(Phase7UnresolvedPlaceholderReason.NoCandidates, state.PlaceholderReason);
            Assert.Equal(run.RunID, state.EvidenceRunID);
            Assert.Equal(0, state.ExactDateCandidateCount);
            Assert.Equal(0, state.AdjacentDateContextCount);
            Assert.Equal(0, state.CandidateTierCount);
        }

        /// <remarks>
        /// Adjacent-date context is reported as context. It is never promoted into a candidate set, so
        /// this is deliberately not a candidate-bearing reason and the exact count stays zero.
        /// </remarks>
        [Fact]
        public void Read_NoExactDateCandidates_WhenAdjacentContextExists()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(Phase7TestFixture.Day(1), isSent: true, 1);

            fixture.Evidence.Run();

            var state = fixture.Reconstruct(attachment);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.NoExactDateCandidates, state.PlaceholderReason);

            Assert.Equal(0, state.ExactDateCandidateCount);
            Assert.Equal(1, state.AdjacentDateContextCount);
            Assert.Equal(0, state.CandidateTierCount);
        }

        [Fact]
        public void Read_ImpossibleGroupFlagged()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, _) =
                fixture.Evidence.AddOutgoingGroup(date, memberCount: 3, positionCount: 2);

            fixture.Evidence.Run();

            var state = fixture.Reconstruct(attachments[0]);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.ImpossibleGroupFlagged, state.PlaceholderReason);

            Assert.True(state.IsWithinDirectionImpossibleGroup);

            // The candidate facts are still reported alongside the headline.
            Assert.Equal(2, state.ExactDateCandidateCount);
        }

        /// <remarks>
        /// A group of two over four positions: the first attachment cannot reach the last position, so
        /// that candidate reduces to less supportive while the reachable ones reduce to supportive. The
        /// partial order therefore discriminates, which is what ranked means.
        /// </remarks>
        [Fact]
        public void Read_CandidateSetRanked_WhenThePartialOrderDiscriminates()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, _) =
                fixture.Evidence.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var run = fixture.Evidence.Run();

            var state = fixture.Reconstruct(attachments[0]);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.CandidateSetRanked, state.PlaceholderReason);

            Assert.True(state.CandidateTierCount > 1);

            // The same tier count the frozen ranking reader produces for the same run.
            Assert.Equal(
                fixture.Evidence.Rank(run.RunID, attachments[0]).Tiers.Count,
                state.CandidateTierCount);
        }

        /// <remarks>
        /// A single candidate is one tier, and therefore unranked. One candidate is not evidence of
        /// anything, and reporting it as ranked would invite exactly the inference the project rule
        /// forbids.
        /// </remarks>
        [Fact]
        public void Read_CandidateSetUnranked_ForASingleCandidate()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var state = fixture.Reconstruct(attachment);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.CandidateSetUnranked, state.PlaceholderReason);

            Assert.Equal(1, state.ExactDateCandidateCount);
            Assert.Equal(1, state.CandidateTierCount);
        }

        [Fact]
        public void Read_CandidateSetUnranked_ForMutuallyIncomparableCandidates()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.Evidence.AddOutgoingGroup(date, memberCount: 1, positionCount: 2);

            var run = fixture.Evidence.Run();

            var state = fixture.Reconstruct(attachments[0]);

            Assert.Equal(2, assets.Length);
            Assert.Equal(2, state.ExactDateCandidateCount);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.CandidateSetUnranked, state.PlaceholderReason);

            Assert.Equal(1, state.CandidateTierCount);
            Assert.Equal(2, Assert.Single(fixture.Evidence.Rank(run.RunID, attachments[0]).Tiers).Count);
        }

        [Fact]
        public void Read_ReviewedInsufficientEvidence()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            fixture.Review(attachment, run.RunID);

            var state = fixture.Reconstruct(attachment);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence,
                state.PlaceholderReason);

            Assert.True(state.HasCurrentReviewedInsufficientEvidence);

            // The candidate-set shape it outranked is still reported.
            Assert.Equal(1, state.CandidateTierCount);
        }

        /// <remarks>
        /// A review of a superseded snapshot remains valid history, but it is not a statement about the
        /// evidence as it now stands, so it does not become the current headline.
        /// </remarks>
        [Fact]
        public void Read_AReviewOfAnOlderRun_DoesNotBecomeTheCurrentReason()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var first = fixture.Evidence.Run();

            fixture.Review(attachment, first.RunID);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence,
                fixture.Reconstruct(attachment).PlaceholderReason);

            var second = fixture.Evidence.Run();

            var state = fixture.Reconstruct(attachment);

            Assert.Equal(second.RunID, state.EvidenceRunID);
            Assert.False(state.HasCurrentReviewedInsufficientEvidence);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.CandidateSetUnranked, state.PlaceholderReason);

            // And the review row itself is untouched.
            Assert.Equal(1, fixture.EventCount("AttachmentReviewEvent"));
        }

        [Fact]
        public void Read_ReviewedAllCandidatesIndependentlyDisproven_FromCurrentRunCandidatesOnly()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            var run = fixture.Evidence.Run();

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, first));

            Assert.False(fixture.Reconstruct(attachment).AllCurrentCandidatesIndependentlyDisproven);

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, second));

            var state = fixture.Reconstruct(attachment);

            Assert.True(state.AllCurrentCandidatesIndependentlyDisproven);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.ReviewedAllCandidatesIndependentlyDisproven,
                state.PlaceholderReason);
        }

        /// <remarks>
        /// Disproofs recorded against an older run's candidate rows are different candidate identities.
        /// They do not make the current run's set all-disproven, and the headline reverts accordingly.
        /// </remarks>
        [Fact]
        public void Read_StaleDispositions_DoNotTriggerTheCurrentAllDisprovenReason()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var first = fixture.Evidence.Run();

            fixture.Disprove(fixture.CandidateID(first.RunID, attachment, asset));

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.ReviewedAllCandidatesIndependentlyDisproven,
                fixture.Reconstruct(attachment).PlaceholderReason);

            fixture.Evidence.Run();

            var state = fixture.Reconstruct(attachment);

            Assert.False(state.AllCurrentCandidatesIndependentlyDisproven);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.CandidateSetUnranked, state.PlaceholderReason);
        }

        /// <remarks>
        /// Zero candidates is never vacuously all-disproven. The run evaluated the attachment and found
        /// nothing, which is what it says instead.
        /// </remarks>
        [Fact]
        public void Read_ZeroCandidates_IsNeverAllDisproven()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(Phase7TestFixture.Day(10), isSent: true, 1);

            fixture.Evidence.Run();

            var state = fixture.Reconstruct(attachment);

            Assert.False(state.AllCurrentCandidatesIndependentlyDisproven);
            Assert.Equal(Phase7UnresolvedPlaceholderReason.NoCandidates, state.PlaceholderReason);
        }

        /// <remarks>
        /// Four conditions true at once — an impossible group, a ranked candidate set, a current review,
        /// and every current candidate disproven — and the precedence is deterministic. Every one of the
        /// conditions it passed over is still reported on the state.
        /// </remarks>
        [Fact]
        public void Read_WhenSeveralReasonsApply_ThePrecedenceIsDeterministicAndLosesNoFact()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var (attachments, assets) =
                fixture.Evidence.AddOutgoingGroup(date, memberCount: 3, positionCount: 2);

            var run = fixture.Evidence.Run();

            var attachment = attachments[0];

            fixture.Review(attachment, run.RunID);

            var impossibleThenReviewed = fixture.Reconstruct(attachment);

            Assert.True(impossibleThenReviewed.IsWithinDirectionImpossibleGroup);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.ReviewedInsufficientEvidence,
                impossibleThenReviewed.PlaceholderReason);

            foreach (var asset in assets)
            {
                fixture.Disprove(fixture.CandidateID(run.RunID, attachment, asset));
            }

            var allFour = fixture.Reconstruct(attachment);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.ReviewedAllCandidatesIndependentlyDisproven,
                allFour.PlaceholderReason);

            // Nothing the headline outranked has been erased.
            Assert.True(allFour.IsWithinDirectionImpossibleGroup);
            Assert.True(allFour.HasCurrentReviewedInsufficientEvidence);
            Assert.True(allFour.AllCurrentCandidatesIndependentlyDisproven);
            Assert.Equal(2, allFour.ExactDateCandidateCount);
            Assert.Equal(run.RunID, allFour.EvidenceRunID);
            Assert.NotNull(allFour.CandidateTierCount);
        }

        [Fact]
        public void Read_IsReadOnlyAndDeterministic()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var resolved = fixture.Evidence.AddIncoming(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);

            fixture.Evidence.Run();

            fixture.Confirm(resolved, asset);

            var checkpoint = fixture.Checkpoint("before-reads.db");

            var first = fixture.Reconstruct(attachment);
            var second = fixture.Reconstruct(attachment);

            Assert.Equal(first.PlaceholderReason, second.PlaceholderReason);
            Assert.Equal(first.EvidenceRunID, second.EvidenceRunID);
            Assert.Equal(first.CandidateTierCount, second.CandidateTierCount);

            fixture.RequireDecisionTablesIdentical(checkpoint);

            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
        }

        [Fact]
        public void Read_Batched_MatchesTheSingleAttachmentReadForEveryCategory()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var (grouped, groupAssets) =
                fixture.Evidence.AddOutgoingGroup(date, memberCount: 2, positionCount: 4);

            var reviewed = fixture.Evidence.AddIncoming(date, time: "12:00:00");
            var resolvable = fixture.Evidence.AddIncoming(date, time: "12:05:00");
            var noCandidates = fixture.Evidence.AddIncoming(Phase7TestFixture.Day(20));
            var outsider = fixture.Evidence.AddOutgoingInOtherConversation(date);

            var run = fixture.Evidence.Run();

            fixture.Review(reviewed, run.RunID);
            fixture.Confirm(resolvable, groupAssets[0]);

            long[] requested =
                [grouped[0], grouped[1], reviewed, resolvable, noCandidates, outsider];

            var (states, statements) = fixture.ReconstructBatch(requested);

            Assert.Equal(requested.Length, states.Count);

            for (var index = 0; index < requested.Length; index++)
            {
                var single = fixture.Reconstruct(requested[index]);

                Assert.Equal(requested[index], states[index].AttachmentID);
                Assert.Equal(single.Status, states[index].Status);
                Assert.Equal(single.PlaceholderReason, states[index].PlaceholderReason);
                Assert.Equal(single.EvidenceRunID, states[index].EvidenceRunID);
                Assert.Equal(single.CandidateTierCount, states[index].CandidateTierCount);

                Assert.Equal(
                    single.ResolvedAsset?.MediaAssetID, states[index].ResolvedAsset?.MediaAssetID);

                Assert.Equal(
                    single.AllCurrentCandidatesIndependentlyDisproven,
                    states[index].AllCurrentCandidatesIndependentlyDisproven);
            }

            // Every category is genuinely represented, so the equivalence above is not vacuous.
            Assert.Contains(Phase7ResolutionStatus.Resolved, states.Select(state => state.Status));

            Assert.Contains(
                Phase7UnresolvedPlaceholderReason.NeverEvaluated,
                states.Select(state => state.PlaceholderReason));

            // Six categories read in one batch, and still a fixed statement count.
            Assert.Equal(Phase7ReconstructionReader.StatementsPerRead, statements);
        }

        /// <remarks>
        /// The statement count does not grow with the batch size. Without this, "no per-attachment
        /// querying" would only be assertable by reading the implementation.
        /// </remarks>
        [Fact]
        public void Read_Batched_IssuesAFixedNumberOfStatementsWhateverTheBatchSize()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachments = new List<long>();

            for (var index = 0; index < 12; index++)
            {
                attachments.Add(fixture.Evidence.AddOutgoing(date, $"09:{index:D2}:00"));
            }

            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            fixture.Confirm(attachments[0], asset);

            var one = fixture.ReconstructBatch([attachments[0]]);
            var all = fixture.ReconstructBatch(attachments);

            Assert.Single(one.States);
            Assert.Equal(12, all.States.Count);
            Assert.Equal(one.Statements, all.Statements);
            Assert.Equal(Phase7ReconstructionReader.StatementsPerRead, all.Statements);
        }

        [Fact]
        public void Read_WhenTheAttachmentDoesNotExist_IsReported()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(() => fixture.Reconstruct(9999));

            Assert.Contains("no attachment 9999", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Read_WithNoAttachmentsOrADuplicate_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.Run();

            Assert.Throws<ArgumentException>(() => fixture.ReconstructBatch([]));

            Assert.Throws<ArgumentException>(
                () => fixture.ReconstructBatch([attachment, attachment]));
        }
    }
}
