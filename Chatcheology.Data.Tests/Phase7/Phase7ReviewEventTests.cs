using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// What a review may and may not record, and what it must leave alone.
    /// </summary>
    public class Phase7ReviewEventTests
    {
        [Fact]
        public void Record_AgainstACurrentCompletedRunContainingTheAttachment_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            var result = fixture.Review(attachment, run.RunID);

            Assert.Equal(attachment, result.AttachmentID);
            Assert.Equal(run.RunID, result.SourceRunID);
            Assert.Equal(Phase7ReviewOutcome.ReviewedInsufficientEvidence, result.Outcome);
            Assert.Equal("Reviewer A", result.ActorLabel);
            Assert.Equal(1, fixture.EventCount("AttachmentReviewEvent"));

            Assert.Equal(
                "ReviewedInsufficientEvidence",
                fixture.Evidence.ScalarText(
                    "SELECT ReviewOutcome FROM AttachmentReviewEvent;"));

            // The timestamp is the service's, stored in the workspace's round-trip format.
            Assert.Equal(
                result.OccurredAtUtc.ToString("O"),
                fixture.Evidence.ScalarText("SELECT OccurredAtUtc FROM AttachmentReviewEvent;"));
        }

        [Fact]
        public void Record_WhenTheAttachmentDoesNotExist_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Review(attachmentID: 9999, run.RunID));

            Assert.Contains("no attachment 9999", error.Message, StringComparison.Ordinal);
            Assert.Equal(
                "AttachmentReviewEvent=0|AttachmentCandidateDispositionEvent=0|" +
                "AttachmentResolutionEvent=0",
                fixture.DecisionRowCounts());
        }

        [Fact]
        public void Record_WhenTheRunDoesNotExist_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Review(attachment, sourceRunID: 9999));

            Assert.Contains("no Phase 7 run 9999", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.EventCount("AttachmentReviewEvent"));
        }

        /// <remarks>
        /// A run that has not published is not a snapshot anybody could have reviewed. Both unfinished
        /// states are refused, and the status check runs before the scope check so the diagnostic names
        /// the real problem.
        /// </remarks>
        [Theory]
        [InlineData("Running")]
        [InlineData("Failed")]
        public void Record_WhenTheRunHasNotPublished_IsRefused(string status)
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            fixture.ExecuteDirect(
                $"""
                INSERT INTO Phase7Run (
                    StartedAtUtc, Status, PreRunWorkspaceFileSha256, RankingDimensionSetVersion)
                VALUES (
                    '2026-08-27T09:00:00.0000000Z', '{status}',
                    'AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA', 'test');
                """);

            var unfinished = fixture.Evidence.ScalarLong("SELECT MAX(RunID) FROM Phase7Run;");

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Review(attachment, unfinished));

            Assert.Contains(status, error.Message, StringComparison.Ordinal);
            Assert.Contains("published no evidence", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.EventCount("AttachmentReviewEvent"));
        }

        /// <remarks>
        /// Two conversations, one run each in scope. A review naming a completed run that never
        /// evaluated the attachment is refused on run scope, which is a different fact from the
        /// attachment having no evidence.
        /// </remarks>
        [Fact]
        public void Record_WhenTheCompletedRunDidNotEvaluateTheAttachment_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            var outsider = fixture.Evidence.AddOutgoingInOtherConversation(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var firstConversationRun = fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Review(outsider, firstConversationRun.RunID));

            Assert.Contains("no evidence snapshot row", error.Message, StringComparison.Ordinal);
            Assert.Contains("run's scope", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.EventCount("AttachmentReviewEvent"));
        }

        [Fact]
        public void Record_WhenANewerRunHasSupersededTheNamedRun_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var first = fixture.Evidence.Run();
            var second = fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Review(attachment, first.RunID));

            Assert.Contains("no longer attachment", error.Message, StringComparison.Ordinal);
            Assert.Contains("remains valid history", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.EventCount("AttachmentReviewEvent"));

            // The contrast, so the refusal above is not vacuous.
            var accepted = fixture.Review(attachment, second.RunID);

            Assert.Equal(second.RunID, accepted.SourceRunID);
            Assert.Equal(1, fixture.EventCount("AttachmentReviewEvent"));
        }

        [Fact]
        public void Record_ChangesNoAttachmentProjection()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var reviewed = fixture.Evidence.AddOutgoing(date);
            var resolvable = fixture.Evidence.AddIncoming(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);

            var run = fixture.Evidence.Run();

            fixture.Confirm(resolvable, asset);

            var before = fixture.ResolutionDistribution();

            fixture.Review(reviewed, run.RunID);

            // A logical row-set comparison over the distribution, not a workspace-file hash.
            Assert.Equal(before, fixture.ResolutionDistribution());
            Assert.Equal("Unresolved/none", fixture.StoredProjection(reviewed));
        }

        [Fact]
        public void Record_ChangesNoEvidenceOrCandidateRow()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            var before = fixture.Evidence.EvidenceSignature(run.RunID);
            var beforeTotal = fixture.Evidence.TotalEvidenceRows();

            fixture.Review(attachment, run.RunID);

            Assert.Equal(before, fixture.Evidence.EvidenceSignature(run.RunID));
            Assert.Equal(beforeTotal, fixture.Evidence.TotalEvidenceRows());
        }

        /// <remarks>
        /// The four axes are independent by construction. An attachment resolved on an independent
        /// basis does not make "this snapshot's evidence was insufficient" false, and no gate is
        /// invented here.
        /// </remarks>
        [Fact]
        public void Record_OnAResolvedAttachment_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            fixture.Confirm(attachment, asset);

            var result = fixture.Review(attachment, run.RunID);

            Assert.Equal(Phase7ReviewOutcome.ReviewedInsufficientEvidence, result.Outcome);
            Assert.Equal($"Resolved/{asset}", fixture.StoredProjection(attachment));
        }

        /// <remarks>
        /// Append-only history, and no uniqueness rule is invented: two reviews of one snapshot are two
        /// facts, whether the same person or two people recorded them.
        /// </remarks>
        [Theory]
        [InlineData("Reviewer A", "Reviewer A")]
        [InlineData("Reviewer A", "Reviewer B")]
        public void Record_Twice_AppendsASecondEvent(string first, string second)
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            var one = fixture.Review(attachment, run.RunID, actorLabel: first);
            var two = fixture.Review(attachment, run.RunID, actorLabel: second);

            Assert.NotEqual(one.AttachmentReviewEventID, two.AttachmentReviewEventID);
            Assert.Equal(2, fixture.EventCount("AttachmentReviewEvent"));
        }

        [Fact]
        public void Record_WithNullNotesAndNullActor_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            var result = fixture.Review(attachment, run.RunID, notes: null, actorLabel: null);

            Assert.Null(result.ActorLabel);

            Assert.Equal(
                "<null>",
                fixture.Evidence.ScalarText("SELECT ReviewNotes FROM AttachmentReviewEvent;"));

            Assert.Equal(
                "<null>",
                fixture.Evidence.ScalarText("SELECT ActorLabel FROM AttachmentReviewEvent;"));
        }

        /// <remarks>
        /// Whitespace-only is neither an absence nor a value. The column would accept a single space,
        /// which is why the service refuses it rather than relying on the constraint.
        /// </remarks>
        [Theory]
        [InlineData("", null)]
        [InlineData("   ", null)]
        [InlineData(null, "")]
        [InlineData(null, "\t")]
        public void Record_WithWhitespaceOnlyNotesOrActor_IsRefused(string? notes, string? actor)
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            Assert.Throws<ArgumentException>(
                () => fixture.Review(attachment, run.RunID, notes, actor));

            Assert.Equal(0, fixture.EventCount("AttachmentReviewEvent"));
        }

        /// <remarks>
        /// Stored verbatim. Trimming would alter what a person wrote, which an audit record must not do.
        /// </remarks>
        [Fact]
        public void Record_StoresNotesAndActorVerbatim()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            fixture.Review(attachment, run.RunID, notes: "  spaced note  ", actorLabel: " A ");

            Assert.Equal(
                "  spaced note  ",
                fixture.Evidence.ScalarText("SELECT ReviewNotes FROM AttachmentReviewEvent;"));

            Assert.Equal(
                " A ",
                fixture.Evidence.ScalarText("SELECT ActorLabel FROM AttachmentReviewEvent;"));
        }
    }
}
