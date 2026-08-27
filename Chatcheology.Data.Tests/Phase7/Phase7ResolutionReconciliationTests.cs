using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// What the verifier detects, what it deliberately does not, and that a failed transition commits
    /// nothing even when it had already written inside its transaction.
    /// </summary>
    public class Phase7ResolutionReconciliationTests
    {
        [Fact]
        public void VerifyWorkspace_OnAConsistentWorkspaceWithMixedHistories_Passes()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var confirmed = fixture.Evidence.AddOutgoing(date);
            var replaced = fixture.Evidence.AddIncoming(date);
            var revoked = fixture.Evidence.AddSenderless(date);
            var untouched = fixture.Evidence.AddOutgoing(date, time: "10:00:00");

            var first = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: null, 2);

            fixture.Evidence.Run();

            fixture.Confirm(confirmed, first);

            var replacedConfirm = fixture.Confirm(replaced, first);
            fixture.Replace(replaced, second, replacedConfirm.AttachmentResolutionEventID);

            var revokedConfirm = fixture.Confirm(revoked, second);
            fixture.Revoke(revoked, revokedConfirm.AttachmentResolutionEventID);

            fixture.VerifyWorkspace();

            Assert.Equal($"Resolved/{first}", fixture.StoredProjection(confirmed));
            Assert.Equal($"Resolved/{second}", fixture.StoredProjection(replaced));
            Assert.Equal("Unresolved/none", fixture.StoredProjection(revoked));
            Assert.Equal("Unresolved/none", fixture.StoredProjection(untouched));
        }

        /// <remarks>
        /// Several attachments' events interleave in the global identifier order, so replay has to group
        /// by attachment rather than assume one attachment's events are contiguous.
        /// </remarks>
        [Fact]
        public void VerifyWorkspace_WithInterleavedAttachmentHistories_ReplaysEachSeparately()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var one = fixture.Evidence.AddOutgoing(date);
            var two = fixture.Evidence.AddIncoming(date);
            var three = fixture.Evidence.AddSenderless(date);

            var first = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: null, 2);

            fixture.Evidence.Run();

            var oneConfirm = fixture.Confirm(one, first);
            var twoConfirm = fixture.Confirm(two, first);
            fixture.Replace(one, second, oneConfirm.AttachmentResolutionEventID);
            var threeConfirm = fixture.Confirm(three, second);
            fixture.Revoke(two, twoConfirm.AttachmentResolutionEventID);
            fixture.Replace(three, first, threeConfirm.AttachmentResolutionEventID);

            fixture.VerifyWorkspace();

            Assert.Equal($"Resolved/{second}", fixture.StoredProjection(one));
            Assert.Equal("Unresolved/none", fixture.StoredProjection(two));
            Assert.Equal($"Resolved/{first}", fixture.StoredProjection(three));
        }

        [Fact]
        public void VerifyWorkspace_DetectsAnInvalidHistoricalSequence()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            fixture.Confirm(attachment, asset);

            // A second confirmation, which the service refuses and only a direct write can produce.
            fixture.InsertResolutionEventDirectly(attachment, "Confirm", asset, null);

            var error = Assert.Throws<InvalidOperationException>(fixture.VerifyWorkspace);

            Assert.Contains("impossible resolution history", error.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The same-asset replacement rule enforced on read as well as on write. The service can never
        /// create such a row, so the only way one exists is a hand-written or third-party insert, and the
        /// verifier reports it rather than accepting it as a no-op.
        /// </remarks>
        [Fact]
        public void VerifyWorkspace_DetectsAHandInsertedSameAssetReplacement()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, asset);

            fixture.InsertResolutionEventDirectly(
                attachment,
                "ConfirmReplacement",
                asset,
                confirmed.AttachmentResolutionEventID);

            var error = Assert.Throws<InvalidOperationException>(fixture.VerifyWorkspace);

            Assert.Contains("must change the asset", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void VerifyWorkspace_DetectsADeliberateOneCellProjectionCorruption()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var confirmedAsset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var otherAsset = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            fixture.Confirm(attachment, confirmedAsset);

            // Not something any Phase 7 service does — something the verifier is proved to notice.
            fixture.ExecuteDirect(
                $"""
                UPDATE Attachment
                SET ResolvedMediaAssetID = {otherAsset}
                WHERE AttachmentID = {attachment};
                """);

            var error = Assert.Throws<InvalidOperationException>(fixture.VerifyWorkspace);

            Assert.Contains("disagree", error.Message, StringComparison.Ordinal);
            Assert.Contains($"asset {confirmedAsset}", error.Message, StringComparison.Ordinal);
            Assert.Contains($"asset {otherAsset}", error.Message, StringComparison.Ordinal);

            // And it repaired nothing: the corrupted cell is still corrupted.
            Assert.Equal($"Resolved/{otherAsset}", fixture.StoredProjection(attachment));
        }

        [Fact]
        public void VerifyWorkspace_DetectsAResolvedProjectionWithNoHistory()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            fixture.ExecuteDirect(
                $"""
                UPDATE Attachment
                SET ResolutionStatus = 'Resolved', ResolvedMediaAssetID = {asset}
                WHERE AttachmentID = {attachment};
                """);

            var error = Assert.Throws<InvalidOperationException>(fixture.VerifyWorkspace);

            Assert.Contains("resolution history is empty", error.Message, StringComparison.Ordinal);
            Assert.Contains("Nothing has been repaired", error.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// <para>The scope of the verifier, pinned in both directions in one test.</para>
        /// <para>Mutating a <strong>replay-significant</strong> field is detected, because it changes the
        /// state the history implies. Mutating a descriptive field on the same row is <strong>not</strong>
        /// detected, and that is stated rather than implied: replay does not consume it, so nothing here
        /// can see it. Append-only is a service and structural-test invariant, and reconciliation is an
        /// additional semantic-consistency detector rather than a general audit-integrity checker.</para>
        /// </remarks>
        [Fact]
        public void VerifyWorkspace_DetectsAReplaySignificantEventMutation_ButNotADescriptiveOne()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var confirmedAsset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var otherAsset = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            fixture.Confirm(attachment, confirmedAsset);

            // A descriptive field replay never reads. Not detected, deliberately.
            fixture.ExecuteDirect(
                "UPDATE AttachmentResolutionEvent SET ActorLabel = 'Someone else';");

            fixture.VerifyWorkspace();

            Assert.Equal(
                "Someone else",
                fixture.Evidence.ScalarText("SELECT ActorLabel FROM AttachmentResolutionEvent;"));

            // A replay-significant field. Detected.
            fixture.ExecuteDirect(
                $"UPDATE AttachmentResolutionEvent SET MediaAssetID = {otherAsset};");

            var error = Assert.Throws<InvalidOperationException>(fixture.VerifyWorkspace);

            Assert.Contains("disagree", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void VerifyAttachment_WhenTheAttachmentDoesNotExist_IsReported()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.VerifyAttachment(9999));

            Assert.Contains("no attachment 9999", error.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The precondition. A transition is never written on top of a projection that already disagrees
        /// with its own history, because doing so would build a second wrong answer on top of a first.
        /// </remarks>
        [Fact]
        public void Transition_WhenTheProjectionAlreadyDivergesFromHistory_IsRefusedBeforeAnyWrite()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, asset);

            fixture.ExecuteDirect(
                $"""
                UPDATE Attachment
                SET ResolutionStatus = 'Unresolved', ResolvedMediaAssetID = NULL
                WHERE AttachmentID = {attachment};
                """);

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Revoke(attachment, confirmed.AttachmentResolutionEventID));

            Assert.Contains("disagree", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
        }

        // -----------------------------------------------------------------------------------------
        // After-DML/pre-commit rollback.
        // -----------------------------------------------------------------------------------------

        /// <remarks>
        /// <para>The three tests below are <strong>after-DML/pre-commit fault injections</strong>, and the
        /// name matches the mechanism: the internal seam throws after the event insert and the projection
        /// update have executed inside the transaction, and before the postcondition reconciliation.
        /// Reconciliation itself is not made to fail; what is proved is the rollback.</para>
        /// <para>Each asserts all four parts of the property: the hook fired; both data-modifying
        /// statements had already executed, observed through the session's own transaction; the failure
        /// happened before the commit; and nothing became committed. This is why the general guarantee is
        /// stated as "commits nothing" rather than "writes nothing".</para>
        /// </remarks>
        [Fact]
        public void Confirm_FailingAfterItsWritesAndBeforeCommit_CommitsNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var checkpoint = fixture.Checkpoint("before-confirm-fault.db");

            var fired = 0;
            var eventsInside = -1L;
            var projectionInside = "<not observed>";

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.ConfirmWithHook(
                    fixture.ConfirmRequest(attachment, asset),
                    session =>
                    {
                        fired++;

                        eventsInside = Phase7DecisionFixture.ScalarLongInSession(
                            session, "SELECT COUNT(*) FROM AttachmentResolutionEvent;");

                        projectionInside = Phase7DecisionFixture.ScalarLongInSession(
                            session,
                            $"SELECT COUNT(*) FROM Attachment WHERE AttachmentID = {attachment} " +
                            $"AND ResolutionStatus = 'Resolved';")
                            .ToString();

                        throw new InvalidOperationException("Injected after-DML fault.");
                    }));

            Assert.Equal("Injected after-DML fault.", error.Message);

            // The hook fired, and both writes had executed inside the transaction before it did.
            Assert.Equal(1, fired);
            Assert.Equal(1, eventsInside);
            Assert.Equal("1", projectionInside);

            // Nothing committed.
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));
            fixture.RequireDecisionTablesIdentical(checkpoint);
        }

        [Fact]
        public void Replace_FailingAfterItsWritesAndBeforeCommit_CommitsNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);
            var checkpoint = fixture.Checkpoint("before-replace-fault.db");

            var fired = 0;
            var eventsInside = -1L;

            Assert.Throws<InvalidOperationException>(
                () => fixture.ReplaceWithHook(
                    fixture.ReplaceRequest(
                        attachment, second, confirmed.AttachmentResolutionEventID),
                    session =>
                    {
                        fired++;

                        eventsInside = Phase7DecisionFixture.ScalarLongInSession(
                            session,
                            $"SELECT COUNT(*) FROM Attachment WHERE AttachmentID = {attachment} " +
                            $"AND ResolvedMediaAssetID = {second};");

                        throw new InvalidOperationException("Injected after-DML fault.");
                    }));

            Assert.Equal(1, fired);
            Assert.Equal(1, eventsInside);

            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
            Assert.Equal($"Resolved/{first}", fixture.StoredProjection(attachment));
            fixture.RequireDecisionTablesIdentical(checkpoint);
        }

        [Fact]
        public void Revoke_FailingAfterItsWritesAndBeforeCommit_CommitsNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, asset);
            var checkpoint = fixture.Checkpoint("before-revoke-fault.db");

            var fired = 0;
            var unresolvedInside = -1L;

            Assert.Throws<InvalidOperationException>(
                () => fixture.RevokeWithHook(
                    fixture.RevokeRequest(attachment, confirmed.AttachmentResolutionEventID),
                    session =>
                    {
                        fired++;

                        unresolvedInside = Phase7DecisionFixture.ScalarLongInSession(
                            session,
                            $"SELECT COUNT(*) FROM Attachment WHERE AttachmentID = {attachment} " +
                            $"AND ResolutionStatus = 'Unresolved';");

                        throw new InvalidOperationException("Injected after-DML fault.");
                    }));

            Assert.Equal(1, fired);
            Assert.Equal(1, unresolvedInside);

            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
            Assert.Equal($"Resolved/{asset}", fixture.StoredProjection(attachment));
            fixture.RequireDecisionTablesIdentical(checkpoint);
        }

        /// <remarks>
        /// The early-refusal counterpart, for contrast: a transition that fails before its validation
        /// completes reaches no data-modifying statement at all. Both shapes commit nothing, which is why
        /// the guarantee is stated in terms of committed state.
        /// </remarks>
        [Fact]
        public void FailedTransition_RefusedBeforeAnyWrite_CommitsNothing()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, asset);
            var checkpoint = fixture.Checkpoint("before-early-refusal.db");

            Assert.Throws<InvalidOperationException>(
                () => fixture.Revoke(attachment, supersedesEventID: confirmed
                    .AttachmentResolutionEventID + 500));

            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
            Assert.Equal($"Resolved/{asset}", fixture.StoredProjection(attachment));
            fixture.RequireDecisionTablesIdentical(checkpoint);
        }
    }
}
