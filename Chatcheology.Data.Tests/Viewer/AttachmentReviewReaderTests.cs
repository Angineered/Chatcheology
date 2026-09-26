using Chatcheology.Data.Media;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Matching;
using Chatcheology.Data.Tests.Phase7;
using Chatcheology.Data.Viewer;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Viewer
{
    /// <summary>
    /// What the review read-model gathers for one attachment, and what it refuses to hide.
    /// </summary>
    /// <remarks>
    /// Phase 7's own rules are not retested here; its suite remains the authority for what a tier or
    /// a support value means. What is tested is the gathering: that the run is the attachment's own,
    /// that every current candidate survives the merge, that a candidate row's identity and its
    /// disproof state stay bound to that exact row, and that nothing here forms an opinion of its
    /// own.
    /// </remarks>
    public class AttachmentReviewReaderTests
    {
        private static DateOnly Day => Phase7TestFixture.FirstDate;

        // -----------------------------------------------------------------------------------
        // The current evidence run.
        // -----------------------------------------------------------------------------------

        /// <remarks>
        /// The run must be the one that evaluated <em>this</em> attachment, never the workspace's
        /// latest. A later run covering a different conversation is not this attachment's evidence.
        /// </remarks>
        [Fact]
        public void CurrentEvidenceRun_IsAttachmentScopedRatherThanWorkspaceLatest()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Day);
            fixture.AddTokenAsset(Day, isSent: true, 1);

            // An attachment in the second conversation, built before either run because a run
            // closes the building connection.
            fixture.AddOutgoingInOtherConversation(Day);

            var first = fixture.Run();

            // A second run scoped to the other conversation only. It becomes the workspace's
            // latest completed run without evaluating this attachment at all.
            var second = fixture.Run(
                localParticipantID: null,
                conversationID: MatchingTestWorkspace.OtherConversationID);

            Assert.NotEqual(first.RunID, second.RunID);

            using var connection = Open(fixture);

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.Equal(first.RunID, state.CurrentEvidenceRunID);
        }

        [Fact]
        public void AnAttachmentNoCompletedRunEvaluated_HasNoRunAndNoCandidates()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Day);

            fixture.CloseBuilding();

            using var connection = Open(fixture);

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.Null(state.CurrentEvidenceRunID);
            Assert.Empty(state.Candidates);
            Assert.Equal(0, state.TierCount);
            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.NeverEvaluated,
                state.Reconstruction.PlaceholderReason);
        }

        /// <remarks>
        /// <see cref="Phase7ReconstructionState.EvidenceRunID"/> is deliberately null for a resolved
        /// attachment, so the review panel would lose its evidence context if it read the run from
        /// there. This is the test that pins the separate lookup.
        /// </remarks>
        [Fact]
        public void AResolvedAttachment_StillReportsItsCurrentEvidenceRun()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var asset = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);

            var run = fixture.Evidence.Run();
            fixture.Confirm(attachment, asset);

            using var connection = fixture.OpenReadOnly();

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.True(state.IsResolved);
            Assert.Null(state.Reconstruction.EvidenceRunID);
            Assert.Equal(run.RunID, state.CurrentEvidenceRunID);
            Assert.NotEmpty(state.Candidates);
        }

        // -----------------------------------------------------------------------------------
        // Every candidate stays visible.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void EveryCurrentExactDateCandidate_IsReturned()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Day);

            var assets = new[]
            {
                fixture.AddTokenAsset(Day, isSent: true, 1),
                fixture.AddTokenAsset(Day, isSent: true, 2),
                fixture.AddTokenAsset(Day, isSent: true, 3),
            };

            fixture.Run();

            using var connection = Open(fixture);

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.Equal(assets.Order(), state.Candidates.Select(c => c.MediaAssetID));

            Assert.Equal(
                state.Reconstruction.ExactDateCandidateCount,
                state.Candidates.Count);
        }

        /// <remarks>
        /// Copies whose direction the workspace never recorded produce
        /// <see cref="Phase7DirectionSupport.Unavailable"/>. The candidate is still a candidate, and
        /// a review surface that dropped it would quietly convert evidence availability into
        /// candidate eligibility.
        /// </remarks>
        [Fact]
        public void ACandidateWhoseSupportIsUnavailable_IsStillReturned()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Day);

            var supported = fixture.AddTokenAsset(Day, isSent: true, 1);
            var unknown = fixture.AddNamedAsset(Day, isSent: null, Phase7TestFixture.Token(2));

            fixture.Run();

            using var connection = Open(fixture);

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.Equal(2, state.Candidates.Count);

            var weak = state.Candidates.Single(c => c.MediaAssetID == unknown);

            Assert.Equal(Phase7DirectionSupport.Unavailable, weak.Direction);
            Assert.Contains(state.Candidates, c => c.MediaAssetID == supported);
        }

        [Fact]
        public void ADisprovenCandidate_RemainsVisibleAndIsMarked()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var first = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(Day, isSent: true, 2);

            var run = fixture.Evidence.Run();

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, first));

            using var connection = fixture.OpenReadOnly();

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.Equal(2, state.Candidates.Count);
            Assert.True(state.Candidates.Single(c => c.MediaAssetID == first).IsIndependentlyDisproven);
            Assert.False(state.Candidates.Single(c => c.MediaAssetID == second).IsIndependentlyDisproven);
        }

        /// <remarks>
        /// Adjacent-date context is contextual evidence the reconstruction summary counts. A chooser
        /// is exactly where it must not appear, because appearing there would promote it into a
        /// candidate set the run never recorded.
        /// </remarks>
        [Fact]
        public void AdjacentDateContext_NeverEntersTheCandidateList()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Day);

            // Nothing on the message's own date; one asset the day before.
            fixture.AddTokenAsset(Day.AddDays(-1), isSent: true, 1);

            fixture.Run();

            using var connection = Open(fixture);

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.Empty(state.Candidates);
            Assert.True(state.Reconstruction.AdjacentDateContextCount > 0);

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.NoExactDateCandidates,
                state.Reconstruction.PlaceholderReason);
        }

        // -----------------------------------------------------------------------------------
        // Identity, support and disproof come from Phase 7.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void CandidateRowIdentity_MatchesTheStoredRowForThatAsset()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var first = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(Day, isSent: true, 2);

            var run = fixture.Evidence.Run();

            using var connection = fixture.OpenReadOnly();

            var state = new AttachmentReviewReader().Read(connection, attachment);

            foreach (var asset in new[] { first, second })
            {
                var candidate = state.Candidates.Single(c => c.MediaAssetID == asset);

                Assert.Equal(fixture.CandidateID(run.RunID, attachment, asset), candidate.AttachmentCandidateID);
                Assert.Equal(run.RunID, candidate.RunID);
            }
        }

        /// <remarks>
        /// Tier and support must be Phase 7's, not a second reading of the same rows. Compared
        /// directly against the ranking API's own output for the same run.
        /// </remarks>
        [Fact]
        public void TierAndSupport_EqualThePhase7RankingApiOutput()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Day);
            fixture.AddTokenAsset(Day, isSent: true, 1);
            fixture.AddTokenAsset(Day, isSent: true, 2);
            fixture.AddNamedAsset(Day, isSent: null, Phase7TestFixture.Token(3));

            var run = fixture.Run();

            using var connection = Open(fixture);

            var state = new AttachmentReviewReader().Read(connection, attachment);
            var ranked = new Phase7SupportReader().ReadRankedCandidateSet(
                connection, run.RunID, attachment);

            Assert.Equal(ranked.Tiers.Count, state.TierCount);
            Assert.Equal(ranked.CandidateCount, state.Candidates.Count);

            for (var tier = 0; tier < ranked.Tiers.Count; tier++)
            {
                foreach (var support in ranked.Tiers[tier])
                {
                    var candidate = state.Candidates.Single(
                        c => c.MediaAssetID == support.MediaAssetID);

                    Assert.Equal(tier + 1, candidate.TierIndex);
                    Assert.Equal(support.Direction, candidate.Direction);
                    Assert.Equal(support.OrderSupport, candidate.OrderSupport);
                }
            }
        }

        /// <remarks>
        /// <strong>The test that keeps a disproof from spreading.</strong> A new evidence run creates
        /// new candidate rows for the same assets. A disproof recorded against the old run named the
        /// old row, and must not mark the new one — which it cannot, because the identity it names is
        /// the row rather than the asset.
        /// </remarks>
        [Fact]
        public void ADispositionAgainstAnOlderRun_DoesNotMarkTheNewRunsCandidateRow()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var asset = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);

            var first = fixture.Evidence.Run();
            var oldCandidate = fixture.CandidateID(first.RunID, attachment, asset);

            fixture.Disprove(oldCandidate);

            var second = fixture.Evidence.Run();
            var newCandidate = fixture.CandidateID(second.RunID, attachment, asset);

            Assert.NotEqual(oldCandidate, newCandidate);

            using var connection = fixture.OpenReadOnly();

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.Equal(second.RunID, state.CurrentEvidenceRunID);

            var candidate = Assert.Single(state.Candidates);

            Assert.Equal(newCandidate, candidate.AttachmentCandidateID);
            Assert.False(candidate.IsIndependentlyDisproven);
        }

        // -----------------------------------------------------------------------------------
        // Media metadata.
        // -----------------------------------------------------------------------------------

        /// <remarks>
        /// Typed inside the data layer. A presentation layer comparing the stored text is the drift
        /// the storage-text owner exists to prevent.
        /// </remarks>
        [Fact]
        public void CandidateMediaType_IsTypedRatherThanStoredText()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var asset = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);

            fixture.Evidence.Run();

            fixture.ExecuteDirect(
                $"""
                UPDATE MediaAsset
                SET MediaType = 'Video', SizeBytes = 4096, DurationMS = 12000,
                    Width = 1920, Height = 1080
                WHERE MediaAssetID = {asset};
                """);

            using var connection = fixture.OpenReadOnly();

            var candidate = Assert.Single(
                new AttachmentReviewReader().Read(connection, attachment).Candidates);

            Assert.Equal(MediaType.Video, candidate.MediaType);
            Assert.Equal(4096, candidate.SizeBytes);
            Assert.Equal(12000, candidate.DurationMS);
            Assert.Equal(1920, candidate.Width);
            Assert.Equal(1080, candidate.Height);
        }

        // -----------------------------------------------------------------------------------
        // Resolution history.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void ResolutionHistoryAndEffectiveEvent_MatchPhase7()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var first = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(Day, isSent: true, 2);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);
            var replaced = fixture.Replace(attachment, second, confirmed.AttachmentResolutionEventID);

            using var connection = fixture.OpenReadOnly();

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.Equal(
                fixture.History(attachment).Select(entry => entry.AttachmentResolutionEventID),
                state.ResolutionHistory.Select(entry => entry.AttachmentResolutionEventID));

            Assert.Equal(
                [Phase7ResolutionEventType.Confirm, Phase7ResolutionEventType.ConfirmReplacement],
                state.ResolutionHistory.Select(entry => entry.EventType));

            Assert.Equal(replaced.AttachmentResolutionEventID, state.CurrentEffectiveEventID);
            Assert.Equal(fixture.Replayed(attachment).CurrentEffectiveEventID, state.CurrentEffectiveEventID);
        }

        [Fact]
        public void AnAttachmentWithNoHistory_ReportsNoEffectiveEvent()
        {
            using var fixture = new Phase7TestFixture();

            var attachment = fixture.AddOutgoing(Day);
            fixture.AddTokenAsset(Day, isSent: true, 1);
            fixture.Run();

            using var connection = Open(fixture);

            var state = new AttachmentReviewReader().Read(connection, attachment);

            Assert.Empty(state.ResolutionHistory);
            Assert.Null(state.CurrentEffectiveEventID);
            Assert.False(state.IsResolved);
        }

        // -----------------------------------------------------------------------------------
        // Review state, refusals and read-only behaviour.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void CurrentReviewState_ComesFromPhase7()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);

            var run = fixture.Evidence.Run();

            using (var before = fixture.OpenReadOnly())
            {
                Assert.False(
                    new AttachmentReviewReader()
                        .Read(before, attachment)
                        .HasCurrentReviewedInsufficientEvidence);
            }

            fixture.Review(attachment, run.RunID);

            using var after = fixture.OpenReadOnly();

            Assert.True(
                new AttachmentReviewReader()
                    .Read(after, attachment)
                    .HasCurrentReviewedInsufficientEvidence);
        }

        [Fact]
        public void AnAttachmentThatDoesNotExist_IsRefused()
        {
            using var fixture = new Phase7TestFixture();

            fixture.AddOutgoing(Day);
            fixture.Run();

            using var connection = Open(fixture);

            Assert.Throws<InvalidOperationException>(
                () => new AttachmentReviewReader().Read(connection, attachmentID: 9_999));
        }

        [Fact]
        public void Reading_ChangesNothingAndWorksOnAReadOnlyConnection()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var asset = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);

            var run = fixture.Evidence.Run();

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, asset));

            var before = fixture.DecisionRowCounts();

            using (var connection = fixture.OpenReadOnly())
            {
                var state = new AttachmentReviewReader().Read(connection, attachment);

                Assert.NotEmpty(state.Candidates);
            }

            Assert.Equal(before, fixture.DecisionRowCounts());
        }

        /// <remarks>
        /// A source-level check as well as a behavioural one: the reader's own statements must be
        /// reads, and must not name a decision table. The read-model is a consumer of Phase 7, and
        /// the moment it writes it stops being one.
        /// </remarks>
        [Fact]
        public void EveryStatement_IsAReadAndNamesNoDecisionTable()
        {
            string[] forbidden =
            [
                "INSERT", "UPDATE", "DELETE",
                "AttachmentReviewEvent",
                "AttachmentCandidateDispositionEvent",
                "AttachmentResolutionEvent",
            ];

            Assert.NotEmpty(AttachmentReviewReader.Statements);

            foreach (var statement in AttachmentReviewReader.Statements)
            {
                Assert.StartsWith("SELECT", statement.TrimStart(), StringComparison.Ordinal);

                foreach (var word in forbidden)
                {
                    Assert.DoesNotContain(word, statement, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

        private static SqliteConnection Open(Phase7TestFixture fixture)
        {
            fixture.CloseBuilding();

            return WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);
        }
    }
}
