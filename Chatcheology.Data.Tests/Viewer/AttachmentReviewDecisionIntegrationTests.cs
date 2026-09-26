using Chatcheology.Data.Phase7;
using Chatcheology.Data.Tests.Phase7;
using Chatcheology.Data.Viewer;

namespace Chatcheology.Data.Tests.Viewer
{
    /// <summary>
    /// The requests the review panel builds, run against the real Phase 7 services.
    /// </summary>
    /// <remarks>
    /// <para>Phase 7's suite remains the authority for the decision services' internal rules. What is
    /// pinned here is what Phase 8.1 is responsible for: that the panel supplies the identities and
    /// provenance Phase 7 expects, that the two different meanings of a source run are not blurred,
    /// that a review or a disproof never resolves anything, and that no arrangement of candidates
    /// makes a confirmation happen on its own.</para>
    /// <para>Every request here is built the way the view-model builds it, so a change that broke the
    /// wiring would fail here rather than in front of the operator.</para>
    /// </remarks>
    public class AttachmentReviewDecisionIntegrationTests
    {
        private static DateOnly Day => Phase7TestFixture.FirstDate;

        private const string Basis =
            "Opened the recovered copy and recognised the room from the surrounding conversation.";

        private const string Reason =
            "The recovered copy shows a different room from the one this message describes.";

        // -----------------------------------------------------------------------------------
        // Review: the source run must be current.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Review_WithTheCurrentRunFromTheReviewState_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out _);

            var state = ReadState(fixture, attachment);

            var result = new Phase7ReviewService().Record(
                new Phase7ReviewRequest
                {
                    DatabasePath = fixture.DatabasePath,
                    AttachmentID = state.AttachmentID,
                    SourceRunID = state.CurrentEvidenceRunID!.Value,
                    ReviewNotes = null,
                    ActorLabel = null,
                });

            Assert.Equal(
                Phase7ReviewOutcome.ReviewedInsufficientEvidence, result.Outcome);

            var after = ReadState(fixture, attachment);

            Assert.True(after.HasCurrentReviewedInsufficientEvidence);
            Assert.False(after.IsResolved);
        }

        /// <remarks>
        /// A review names the snapshot it reviewed, so naming a superseded one is refused. This is
        /// the refusal the panel must present as "the evidence changed — refresh", never retry.
        /// </remarks>
        [Fact]
        public void Review_WithASupersededRun_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out var firstRun);

            fixture.Evidence.Run();

            Assert.Throws<InvalidOperationException>(
                () => new Phase7ReviewService().Record(
                    new Phase7ReviewRequest
                    {
                        DatabasePath = fixture.DatabasePath,
                        AttachmentID = attachment,
                        SourceRunID = firstRun,
                        ReviewNotes = null,
                        ActorLabel = null,
                    }));

            Assert.False(ReadState(fixture, attachment).HasCurrentReviewedInsufficientEvidence);
        }

        [Fact]
        public void Review_DoesNotResolveTheAttachment()
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out var run);

            fixture.Review(attachment, run, notes: null, actorLabel: null);

            var state = ReadState(fixture, attachment);

            Assert.False(state.IsResolved);
            Assert.Equal(Phase7ResolutionStatus.Unresolved, state.Reconstruction.Status);
            Assert.Null(state.CurrentEffectiveEventID);
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        // -----------------------------------------------------------------------------------
        // Disposition: the candidate row carries the run.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Disposition_UsesTheCandidateRowIdentityAndLeavesTheAttachmentUnresolved()
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out _);

            var candidate = ReadState(fixture, attachment).Candidates[0];

            var result = new Phase7CandidateDispositionService().Record(
                new Phase7CandidateDispositionRequest
                {
                    DatabasePath = fixture.DatabasePath,
                    AttachmentCandidateID = candidate.AttachmentCandidateID,
                    Reason = Reason,
                    ActorLabel = null,
                });

            // Phase 7 read the row's own identity rather than trusting a caller-supplied tuple.
            Assert.Equal(attachment, result.AttachmentID);
            Assert.Equal(candidate.RunID, result.RunID);
            Assert.Equal(candidate.MediaAssetID, result.MediaAssetID);

            var after = ReadState(fixture, attachment);

            Assert.False(after.IsResolved);
            Assert.True(
                after.Candidates
                    .Single(c => c.AttachmentCandidateID == candidate.AttachmentCandidateID)
                    .IsIndependentlyDisproven);
        }

        [Fact]
        public void Disposition_AgainstASupersededCandidateRow_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out _);

            var stale = ReadState(fixture, attachment).Candidates[0].AttachmentCandidateID;

            fixture.Evidence.Run();

            Assert.Throws<InvalidOperationException>(
                () => new Phase7CandidateDispositionService().Record(
                    new Phase7CandidateDispositionRequest
                    {
                        DatabasePath = fixture.DatabasePath,
                        AttachmentCandidateID = stale,
                        Reason = Reason,
                        ActorLabel = null,
                    }));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Disposition_WithoutAStatedReason_IsRefused(string reason)
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out _);

            var candidate = ReadState(fixture, attachment).Candidates[0];

            Assert.Throws<ArgumentException>(
                () => new Phase7CandidateDispositionService().Record(
                    new Phase7CandidateDispositionRequest
                    {
                        DatabasePath = fixture.DatabasePath,
                        AttachmentCandidateID = candidate.AttachmentCandidateID,
                        Reason = reason,
                        ActorLabel = null,
                    }));
        }

        // -----------------------------------------------------------------------------------
        // Nothing resolves on its own.
        // -----------------------------------------------------------------------------------

        /// <remarks>
        /// <strong>The rule the whole milestone exists to protect.</strong> Disproving all but one
        /// candidate changes nothing about the remaining one. It is still unresolved, still requires
        /// an explicit independent basis, and no code path anywhere turns "only one left" into an
        /// answer.
        /// </remarks>
        [Fact]
        public void DisprovingAllButOneCandidate_ResolvesNothing()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var first = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(Day, isSent: true, 2);
            var third = fixture.Evidence.AddTokenAsset(Day, isSent: true, 3);

            var run = fixture.Evidence.Run();

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, first), Reason, null);
            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, third), Reason, null);

            var state = ReadState(fixture, attachment);

            Assert.False(state.IsResolved);
            Assert.Equal(3, state.Candidates.Count);
            Assert.Equal(
                1,
                state.Candidates.Count(candidate => !candidate.IsIndependentlyDisproven));

            Assert.Equal(second, state.Candidates.Single(c => !c.IsIndependentlyDisproven).MediaAssetID);
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        [Fact]
        public void DisprovingEveryCandidate_ResolvesNothing()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var first = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(Day, isSent: true, 2);

            var run = fixture.Evidence.Run();

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, first), Reason, null);
            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, second), Reason, null);

            var state = ReadState(fixture, attachment);

            Assert.False(state.IsResolved);
            Assert.All(state.Candidates, candidate => Assert.True(candidate.IsIndependentlyDisproven));
            Assert.True(state.Reconstruction.AllCurrentCandidatesIndependentlyDisproven);
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        // -----------------------------------------------------------------------------------
        // Confirm.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Confirm_FromACandidate_PassesTheAssetAndTheRunAsProvenance()
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out _);

            var candidate = ReadState(fixture, attachment).Candidates[0];

            var result = new Phase7ResolutionService().Confirm(
                new Phase7ConfirmRequest
                {
                    DatabasePath = fixture.DatabasePath,
                    AttachmentID = attachment,
                    MediaAssetID = candidate.MediaAssetID,
                    IndependentBasisType =
                        Phase7IndependentBasisType.HumanContentOrContextInspection,
                    IndependentBasisDescription = Basis,
                    SourceRunID = candidate.RunID,
                    ActorLabel = null,
                });

            Assert.Equal(Phase7ResolutionEventType.Confirm, result.EventType);
            Assert.Equal(candidate.RunID, result.SourceRunID);

            var after = ReadState(fixture, attachment);

            Assert.True(after.IsResolved);
            Assert.Equal(candidate.MediaAssetID, after.Reconstruction.ResolvedAsset!.MediaAssetID);
            Assert.Equal(result.AttachmentResolutionEventID, after.CurrentEffectiveEventID);
        }

        /// <remarks>
        /// <strong>The distinction the brief insists on.</strong> A source run on a confirmation is
        /// truthful provenance, not authority: it must have completed and covered this attachment,
        /// but it is <em>not</em> required to be current. A review naming the same run would be
        /// refused, and the two failures must not be conflated.
        /// </remarks>
        [Fact]
        public void Confirm_CitingASupersededButTruthfulRun_IsNotRefusedForStaleness()
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out var firstRun);

            var second = fixture.Evidence.Run();

            Assert.NotEqual(firstRun, second.RunID);

            var candidate = ReadState(fixture, attachment).Candidates[0];

            var result = new Phase7ResolutionService().Confirm(
                new Phase7ConfirmRequest
                {
                    DatabasePath = fixture.DatabasePath,
                    AttachmentID = attachment,
                    MediaAssetID = candidate.MediaAssetID,
                    IndependentBasisType =
                        Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth,
                    IndependentBasisDescription = Basis,
                    SourceRunID = firstRun,          // superseded, and still truthful
                    ActorLabel = null,
                });

            Assert.Equal(firstRun, result.SourceRunID);
            Assert.True(ReadState(fixture, attachment).IsResolved);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Confirm_WithoutAStatedIndependentBasis_IsRefused(string description)
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out _);

            var candidate = ReadState(fixture, attachment).Candidates[0];

            Assert.Throws<ArgumentException>(
                () => new Phase7ResolutionService().Confirm(
                    new Phase7ConfirmRequest
                    {
                        DatabasePath = fixture.DatabasePath,
                        AttachmentID = attachment,
                        MediaAssetID = candidate.MediaAssetID,
                        IndependentBasisType =
                            Phase7IndependentBasisType.HumanContentOrContextInspection,
                        IndependentBasisDescription = description,
                        SourceRunID = candidate.RunID,
                        ActorLabel = null,
                    }));

            Assert.False(ReadState(fixture, attachment).IsResolved);
        }

        // -----------------------------------------------------------------------------------
        // Replace and revoke.
        // -----------------------------------------------------------------------------------

        [Fact]
        public void Replace_UsesTheCurrentEffectiveEventFromTheReviewState()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var first = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(Day, isSent: true, 2);

            fixture.Evidence.Run();
            fixture.Confirm(attachment, first, sourceRunID: null, actorLabel: null);

            var state = ReadState(fixture, attachment);
            var target = state.Candidates.Single(candidate => candidate.MediaAssetID == second);

            var result = new Phase7ResolutionService().ConfirmReplacement(
                new Phase7ConfirmReplacementRequest
                {
                    DatabasePath = fixture.DatabasePath,
                    AttachmentID = attachment,
                    MediaAssetID = target.MediaAssetID,
                    SupersedesEventID = state.CurrentEffectiveEventID!.Value,
                    IndependentBasisType =
                        Phase7IndependentBasisType.HumanContentOrContextInspection,
                    IndependentBasisDescription = Basis,
                    SourceRunID = target.RunID,
                    ActorLabel = null,
                });

            var after = ReadState(fixture, attachment);

            Assert.Equal(second, after.Reconstruction.ResolvedAsset!.MediaAssetID);
            Assert.Equal(result.AttachmentResolutionEventID, after.CurrentEffectiveEventID);
            Assert.Equal(2, after.ResolutionHistory.Count);
        }

        [Fact]
        public void Replace_WithTheAssetAlreadyResolved_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();

            var attachment = fixture.Evidence.AddOutgoing(Day);
            var asset = fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);

            fixture.Evidence.Run();
            fixture.Confirm(attachment, asset, sourceRunID: null, actorLabel: null);

            var state = ReadState(fixture, attachment);

            Assert.Throws<InvalidOperationException>(
                () => new Phase7ResolutionService().ConfirmReplacement(
                    new Phase7ConfirmReplacementRequest
                    {
                        DatabasePath = fixture.DatabasePath,
                        AttachmentID = attachment,
                        MediaAssetID = asset,
                        SupersedesEventID = state.CurrentEffectiveEventID!.Value,
                        IndependentBasisType =
                            Phase7IndependentBasisType.HumanContentOrContextInspection,
                        IndependentBasisDescription = Basis,
                        SourceRunID = null,
                        ActorLabel = null,
                    }));
        }

        [Fact]
        public void Revoke_UsesTheCurrentEffectiveEventAndReturnsToUnresolved()
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out _);

            var candidate = ReadState(fixture, attachment).Candidates[0];

            fixture.Confirm(attachment, candidate.MediaAssetID, sourceRunID: null, actorLabel: null);

            var resolved = ReadState(fixture, attachment);

            new Phase7ResolutionService().Revoke(
                new Phase7RevokeRequest
                {
                    DatabasePath = fixture.DatabasePath,
                    AttachmentID = attachment,
                    SupersedesEventID = resolved.CurrentEffectiveEventID!.Value,
                    RevocationReason = "Looked again and the room does not match.",
                    SourceRunID = null,
                    ActorLabel = null,
                });

            var after = ReadState(fixture, attachment);

            Assert.False(after.IsResolved);
            Assert.Null(after.Reconstruction.ResolvedAsset);

            // The prior resolution is not deleted; it is superseded.
            Assert.Equal(2, after.ResolutionHistory.Count);
            Assert.Equal(
                Phase7ResolutionEventType.Revoke,
                after.ResolutionHistory[^1].EventType);

            // And the candidate that was confirmed is a candidate again, not a disproven one.
            Assert.Contains(after.Candidates, c => c.MediaAssetID == candidate.MediaAssetID);
            Assert.All(after.Candidates, c => Assert.False(c.IsIndependentlyDisproven));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Revoke_WithoutAStatedReason_IsRefused(string reason)
        {
            using var fixture = new Phase7DecisionFixture();
            var attachment = Prepare(fixture, out _);

            var candidate = ReadState(fixture, attachment).Candidates[0];

            fixture.Confirm(attachment, candidate.MediaAssetID, sourceRunID: null, actorLabel: null);

            var resolved = ReadState(fixture, attachment);

            Assert.Throws<ArgumentException>(
                () => new Phase7ResolutionService().Revoke(
                    new Phase7RevokeRequest
                    {
                        DatabasePath = fixture.DatabasePath,
                        AttachmentID = attachment,
                        SupersedesEventID = resolved.CurrentEffectiveEventID!.Value,
                        RevocationReason = reason,
                        SourceRunID = null,
                        ActorLabel = null,
                    }));

            Assert.True(ReadState(fixture, attachment).IsResolved);
        }

        // -----------------------------------------------------------------------------------
        // Helpers.
        // -----------------------------------------------------------------------------------

        /// <summary>One outgoing attachment with two same-date candidates, and one completed run.</summary>
        private static long Prepare(Phase7DecisionFixture fixture, out long runID)
        {
            var attachment = fixture.Evidence.AddOutgoing(Day);

            fixture.Evidence.AddTokenAsset(Day, isSent: true, 1);
            fixture.Evidence.AddTokenAsset(Day, isSent: true, 2);

            runID = fixture.Evidence.Run().RunID;

            return attachment;
        }

        /// <summary>
        /// The panel's own read, through a fresh read-only connection every time — which is what the
        /// viewer does after a write, and the reason a stale in-memory view cannot pass these tests.
        /// </summary>
        private static AttachmentReviewState ReadState(
            Phase7DecisionFixture fixture, long attachmentID)
        {
            using var connection = fixture.OpenReadOnly();

            return new AttachmentReviewReader().Read(connection, attachmentID);
        }
    }
}
