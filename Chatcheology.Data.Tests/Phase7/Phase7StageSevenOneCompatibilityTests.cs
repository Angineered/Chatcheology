using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// That a Stage 7.1 evidence refresh still works once legitimate Stage 7.2 decision history exists,
    /// and that it leaves that history exactly as it found it.
    /// </summary>
    /// <remarks>
    /// This is the cross-stage property the removal of the obsolete global-emptiness publication check
    /// exists to permit. Its safety intent survives in three separated layers, and these tests keep them
    /// separate: the publication tripwire is four aggregates per decision table and is asserted as
    /// exactly that; the structural statement tests prove Stage 7.1 has no data-modifying statement
    /// naming those tables at all, which is what covers the columns the aggregates cannot see; and the
    /// whole-row comparison against a checkpoint below is what actually establishes preservation.
    /// </remarks>
    public class Phase7StageSevenOneCompatibilityTests
    {
        /// <remarks>
        /// The mandatory end-to-end test. Run, decide, checkpoint, run again — and prove the second run
        /// completed, changed no decision row in any cell, and neither acted on nor reinterpreted any
        /// decision.
        /// </remarks>
        [Fact]
        public void EvidenceRefresh_WithPreExistingDecisionHistory_PreservesItExactly()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var reviewed = fixture.Evidence.AddOutgoing(date);
            var disproven = fixture.Evidence.AddIncoming(date);
            var resolved = fixture.Evidence.AddSenderless(date);

            var first = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: null, 2);

            var runOne = fixture.Evidence.Run();

            Assert.Equal("Completed", fixture.Evidence.RunStatus(runOne.RunID));

            // Legitimate Stage 7.2 history, through the public services.
            fixture.Review(reviewed, runOne.RunID);

            var disprovenCandidate = fixture.CandidateID(runOne.RunID, disproven, first);
            fixture.Disprove(disprovenCandidate);

            fixture.Confirm(resolved, second);

            var checkpoint = fixture.Checkpoint();
            var decisionCounts = fixture.DecisionRowCounts();

            // The refresh that check 16 used to make impossible.
            var runTwo = fixture.Evidence.Run();

            Assert.Equal("Completed", fixture.Evidence.RunStatus(runTwo.RunID));
            Assert.NotEqual(runOne.RunID, runTwo.RunID);

            // Whole-row, both directions, schema objects included. This is the layer that proves it.
            fixture.RequireDecisionTablesIdentical(checkpoint);

            Assert.Equal(decisionCounts, fixture.DecisionRowCounts());

            // The resolved attachment is no longer in scope, because Stage 7.1 analyses unresolved ones.
            Assert.Equal(
                0,
                fixture.Evidence.ScalarLong(
                    $"""
                    SELECT COUNT(*) FROM AttachmentEvidenceSnapshot
                    WHERE RunID = {runTwo.RunID} AND AttachmentID = {resolved};
                    """));

            // The two still-unresolved ones are.
            foreach (var attachment in new[] { reviewed, disproven })
            {
                Assert.Equal(
                    1,
                    fixture.Evidence.ScalarLong(
                        $"""
                        SELECT COUNT(*) FROM AttachmentEvidenceSnapshot
                        WHERE RunID = {runTwo.RunID} AND AttachmentID = {attachment};
                        """));
            }

            // The new run's candidate rows are new identities, and inherit no disposition.
            var refreshedCandidate = fixture.CandidateID(runTwo.RunID, disproven, first);

            Assert.NotEqual(disprovenCandidate, refreshedCandidate);
            Assert.Empty(fixture.DisprovenCandidateIDs(runTwo.RunID, disproven));
            Assert.False(fixture.AllDisproven(runTwo.RunID, disproven));

            // The first run's disposition is untouched history.
            Assert.Equal(
                [disprovenCandidate], fixture.DisprovenCandidateIDs(runOne.RunID, disproven));

            // The review did not change what the refresh recorded about that attachment.
            Assert.Equal(
                fixture.Evidence.Snapshot(runOne.RunID, reviewed),
                fixture.Evidence.Snapshot(runTwo.RunID, reviewed));

            // And the resolution still reconciles with its history after the refresh.
            fixture.VerifyWorkspace();
        }

        /// <remarks>
        /// The tripwire is not vacuous: inserting a decision row changes the baseline signature, and a
        /// no-op does not. What this proves is that the aggregates move at all — not that they are a
        /// fingerprint, which they are not.
        /// </remarks>
        [Fact]
        public void Baseline_ChangesWhenADecisionRowIsInserted()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            var before = fixture.BaselineSignature();

            Assert.Equal(before, fixture.BaselineSignature());

            Assert.Contains("AttachmentReviewEvent[n=0,min=0,max=0,sum=0]", before, StringComparison.Ordinal);

            fixture.Review(attachment, run.RunID);

            var afterReview = fixture.BaselineSignature();

            Assert.NotEqual(before, afterReview);
            Assert.Contains("AttachmentReviewEvent[n=1,", afterReview, StringComparison.Ordinal);

            fixture.Confirm(attachment, asset);

            var afterConfirm = fixture.BaselineSignature();

            Assert.NotEqual(afterReview, afterConfirm);

            // The Attachment resolution distribution moves too, which is what check 15 already covered.
            Assert.Contains("Attachment[Resolved,unlinked=0]=1", afterConfirm, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The count-preserving case a bare row count would miss: one row deleted and reinserted at a
        /// fresh primary key. <strong>This proves that chosen mutation is detected, not that every
        /// count-preserving mutation is</strong> — four aggregates can collide, and the brief and the
        /// production doc comment both say so.
        /// </remarks>
        [Fact]
        public void Baseline_ChangesWhenADecisionRowIsDeletedAndReinserted()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            fixture.Review(attachment, run.RunID);

            var before = fixture.BaselineSignature();

            fixture.ExecuteDirect("DELETE FROM AttachmentReviewEvent;");

            fixture.ExecuteDirect(
                $"""
                INSERT INTO AttachmentReviewEvent (
                    AttachmentReviewEventID, AttachmentID, SourceRunID, ReviewOutcome, OccurredAtUtc)
                VALUES (
                    500, {attachment}, {run.RunID}, 'ReviewedInsufficientEvidence',
                    '2026-08-27T09:00:00.0000000Z');
                """);

            Assert.Equal(1, fixture.EventCount("AttachmentReviewEvent"));

            var after = fixture.BaselineSignature();

            Assert.NotEqual(before, after);
            Assert.Contains("AttachmentReviewEvent[n=1,min=500,max=500,sum=500]", after, StringComparison.Ordinal);
        }

        /// <remarks>
        /// An attachment resolved through Stage 7.2 drops out of a later run's scope, and the
        /// attachment-scoped currency reader still names the run that did evaluate it. That is what stops
        /// a later run from being mistaken for its evidence, and what makes a subsequent revocation read
        /// as its earlier evidence rather than as never evaluated.
        /// </remarks>
        [Fact]
        public void AttachmentScopedCurrency_RemainsCorrectWhenALaterRunOmitsAResolvedAttachment()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var other = fixture.Evidence.AddIncoming(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);

            var runOne = fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, asset);

            var runTwo = fixture.Evidence.Run();

            using (var connection = fixture.OpenReadOnly())
            {
                var reader = new Phase7SupportReader();

                // Workspace-global and scope-blind: the latest run, whatever it covered.
                Assert.Equal(runTwo.RunID, reader.ReadLatestCompletedRunID(connection));

                // Attachment-scoped: the latest run that actually evaluated this attachment.
                Assert.Equal(
                    runOne.RunID, reader.ReadLatestCompletedRunContaining(connection, attachment));

                Assert.Equal(
                    runTwo.RunID, reader.ReadLatestCompletedRunContaining(connection, other));
            }

            // Resolved, so reconstruction answers from the projection and consults no snapshot.
            var resolvedState = fixture.Reconstruct(attachment);

            Assert.Equal(Phase7ResolutionStatus.Resolved, resolvedState.Status);
            Assert.Null(resolvedState.EvidenceRunID);

            // Revoked, so it is unresolved again — and it reads as its run-one evidence, not as never
            // evaluated, even though the newest run omitted it.
            fixture.Revoke(attachment, confirmed.AttachmentResolutionEventID);

            var revokedState = fixture.Reconstruct(attachment);

            Assert.Equal(Phase7ResolutionStatus.Unresolved, revokedState.Status);
            Assert.Equal(runOne.RunID, revokedState.EvidenceRunID);

            Assert.NotEqual(
                Phase7UnresolvedPlaceholderReason.NeverEvaluated, revokedState.PlaceholderReason);
        }

        /// <remarks>
        /// A refresh after some attachments are resolved still covers every remaining unresolved one, and
        /// the two independent derivations of that count agree — which is what the publication check that
        /// counts them proves, and it keeps proving it once resolutions exist.
        /// </remarks>
        [Fact]
        public void EvidenceRefresh_AfterSomeAttachmentsAreResolved_CoversTheRemainingUnresolvedOnes()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var resolvedOne = fixture.Evidence.AddOutgoing(date);
            var resolvedTwo = fixture.Evidence.AddIncoming(date);
            var stillUnresolved = fixture.Evidence.AddSenderless(date);
            var alsoUnresolved = fixture.Evidence.AddOutgoing(date, time: "11:00:00");

            var asset = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);

            fixture.Evidence.Run();

            fixture.Confirm(resolvedOne, asset);
            fixture.Confirm(resolvedTwo, asset);

            var runTwo = fixture.Evidence.Run();

            Assert.Equal("Completed", fixture.Evidence.RunStatus(runTwo.RunID));
            Assert.Equal(2, runTwo.SnapshotRowCount);

            Assert.Equal(
                [
                    stillUnresolved.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    alsoUnresolved.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ],
                fixture.Evidence.Rows(
                    $"""
                    SELECT AttachmentID FROM AttachmentEvidenceSnapshot
                    WHERE RunID = {runTwo.RunID}
                    ORDER BY AttachmentID;
                    """));
        }
    }
}
