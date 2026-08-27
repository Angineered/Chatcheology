using System.Reflection;
using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// What a candidate disproof may record, which candidate identity it is tied to, and what it must
    /// not touch.
    /// </summary>
    public class Phase7CandidateDispositionTests
    {
        [Fact]
        public void Record_AgainstACurrentCandidate_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();
            var candidate = fixture.CandidateID(run.RunID, attachment, asset);

            var result = fixture.Disprove(candidate);

            Assert.Equal(candidate, result.AttachmentCandidateID);
            Assert.Equal(attachment, result.AttachmentID);
            Assert.Equal(run.RunID, result.RunID);
            Assert.Equal(asset, result.MediaAssetID);

            Assert.Equal(
                Phase7CandidateDisposition.IndependentlyDisproven, result.Disposition);

            Assert.Equal(1, fixture.EventCount("AttachmentCandidateDispositionEvent"));
            Assert.True(fixture.AllDisproven(run.RunID, attachment));
        }

        [Fact]
        public void Record_WhenTheCandidateDoesNotExist_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Disprove(candidateID: 9999));

            Assert.Contains("no attachment candidate 9999", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.EventCount("AttachmentCandidateDispositionEvent"));
        }

        [Fact]
        public void Record_AgainstACandidateOfAStaleRun_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var first = fixture.Evidence.Run();
            var second = fixture.Evidence.Run();

            var stale = fixture.CandidateID(first.RunID, attachment, asset);

            var error = Assert.Throws<InvalidOperationException>(() => fixture.Disprove(stale));

            Assert.Contains("no longer attachment", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.EventCount("AttachmentCandidateDispositionEvent"));

            // The contrast, so the refusal above is not vacuous.
            var current = fixture.CandidateID(second.RunID, attachment, asset);

            Assert.Equal(current, fixture.Disprove(current).AttachmentCandidateID);
        }

        /// <remarks>
        /// The frozen rule that keeps a disproof from going quietly stale: a later run's candidate row
        /// for the same asset is a new candidate identity, and has to be ruled out on its own.
        /// </remarks>
        [Fact]
        public void Record_TheSameAssetInALaterRun_RemainsUndisprovenUntilDisprovedAgain()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var first = fixture.Evidence.Run();
            var firstCandidate = fixture.CandidateID(first.RunID, attachment, asset);

            fixture.Disprove(firstCandidate);

            Assert.True(fixture.AllDisproven(first.RunID, attachment));

            var second = fixture.Evidence.Run();
            var secondCandidate = fixture.CandidateID(second.RunID, attachment, asset);

            Assert.NotEqual(firstCandidate, secondCandidate);
            Assert.False(fixture.AllDisproven(second.RunID, attachment));
            Assert.Empty(fixture.DisprovenCandidateIDs(second.RunID, attachment));

            // The first run's history is untouched by any of this.
            Assert.Equal([firstCandidate], fixture.DisprovenCandidateIDs(first.RunID, attachment));

            fixture.Disprove(secondCandidate);

            Assert.True(fixture.AllDisproven(second.RunID, attachment));
        }

        /// <remarks>
        /// The relationship is unspoofable because there is no field in which to supply it: the request
        /// carries the candidate identifier and the human's words, and nothing else.
        /// </remarks>
        [Fact]
        public void Request_CannotSupplyTheAttachmentRunOrAssetRelationship()
        {
            var properties = typeof(Phase7CandidateDispositionRequest)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .Order()
                .ToList();

            Assert.Equal(
                ["ActorLabel", "AttachmentCandidateID", "DatabasePath", "Reason"], properties);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n")]
        public void Record_WithEmptyOrWhitespaceReason_IsRefused(string reason)
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();
            var candidate = fixture.CandidateID(run.RunID, attachment, asset);

            Assert.Throws<ArgumentException>(() => fixture.Disprove(candidate, reason));

            Assert.Equal(0, fixture.EventCount("AttachmentCandidateDispositionEvent"));
        }

        [Fact]
        public void Record_Twice_AppendsASecondEventAndChangesNoDerivedAnswer()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();
            var candidate = fixture.CandidateID(run.RunID, attachment, asset);

            var one = fixture.Disprove(candidate, actorLabel: "Reviewer A");
            var two = fixture.Disprove(candidate, actorLabel: "Reviewer B");

            Assert.NotEqual(
                one.AttachmentCandidateDispositionEventID,
                two.AttachmentCandidateDispositionEventID);

            Assert.Equal(2, fixture.EventCount("AttachmentCandidateDispositionEvent"));

            // The derived read asks only whether at least one disproof exists.
            Assert.Equal([candidate], fixture.DisprovenCandidateIDs(run.RunID, attachment));
            Assert.True(fixture.AllDisproven(run.RunID, attachment));
        }

        [Fact]
        public void Record_OnAResolvedAttachment_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            var run = fixture.Evidence.Run();

            fixture.Confirm(attachment, first);

            var candidate = fixture.CandidateID(run.RunID, attachment, second);

            Assert.Equal(candidate, fixture.Disprove(candidate).AttachmentCandidateID);
            Assert.Equal($"Resolved/{first}", fixture.StoredProjection(attachment));
        }

        /// <remarks>
        /// The important negative. Disproving the asset an attachment is currently resolved to is a
        /// legitimate record, and wiring it to an automatic revocation would be exactly the kind of
        /// inferred state change the frozen design forbids — in this direction as much as the other.
        /// </remarks>
        [Fact]
        public void Record_DisprovingTheResolvedAsset_DoesNotRevokeTheResolution()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();
            var candidate = fixture.CandidateID(run.RunID, attachment, asset);

            fixture.Confirm(attachment, asset);

            fixture.Disprove(candidate);

            Assert.Equal($"Resolved/{asset}", fixture.StoredProjection(attachment));
            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
            Assert.True(fixture.AllDisproven(run.RunID, attachment));
        }

        [Fact]
        public void AllDisproven_WhenOneCandidateHasNoDisproof_IsFalse()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            var run = fixture.Evidence.Run();

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, first));

            Assert.False(fixture.AllDisproven(run.RunID, attachment));

            fixture.Disprove(fixture.CandidateID(run.RunID, attachment, second));

            Assert.True(fixture.AllDisproven(run.RunID, attachment));
        }

        /// <remarks>
        /// <strong>The vacuous-truth edge case.</strong> An attachment nobody has ruled anything out for
        /// is not one where everything has been ruled out. The run evaluated it and found no candidate,
        /// which is an accurate and different thing to say — and the reconstruction model says exactly
        /// that instead.
        /// </remarks>
        [Fact]
        public void AllDisproven_WhenTheRunHoldsZeroCandidates_IsFalse()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);

            var run = fixture.Evidence.Run();

            Assert.Equal(
                0,
                fixture.Evidence.ScalarLong(
                    $"SELECT COUNT(*) FROM AttachmentCandidate WHERE AttachmentID = {attachment};"));

            Assert.False(fixture.AllDisproven(run.RunID, attachment));

            Assert.Equal(
                Phase7UnresolvedPlaceholderReason.NoCandidates,
                fixture.Reconstruct(attachment).PlaceholderReason);
        }

        /// <remarks>
        /// The most persuasive evidence shape the model can produce writes no disposition of its own,
        /// and running the analysis again writes none either.
        /// </remarks>
        [Fact]
        public void Ranking_NeverCreatesADisposition()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            var support = Assert.Single(fixture.Evidence.Support(run.RunID, attachment));

            Assert.Equal(asset, support.MediaAssetID);
            Assert.Equal(Phase7DirectionSupport.Supportive, support.Direction);
            Assert.Equal(Phase7OrderSupport.Supportive, support.OrderSupport);

            Assert.Equal(0, fixture.EventCount("AttachmentCandidateDispositionEvent"));
            Assert.False(fixture.AllDisproven(run.RunID, attachment));
        }

        [Fact]
        public void Record_ChangesNoAttachmentProjectionOrEvidenceRow()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();
            var candidate = fixture.CandidateID(run.RunID, attachment, asset);

            var distribution = fixture.ResolutionDistribution();
            var evidence = fixture.Evidence.EvidenceSignature(run.RunID);

            fixture.Disprove(candidate);

            Assert.Equal(distribution, fixture.ResolutionDistribution());
            Assert.Equal(evidence, fixture.Evidence.EvidenceSignature(run.RunID));
        }
    }
}
