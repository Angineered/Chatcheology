using System.Reflection;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The three resolution transitions, exactly as the frozen matrix pins them.
    /// </summary>
    public class Phase7ResolutionTransitionTests
    {
        // -----------------------------------------------------------------------------------------
        // Confirm.
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Confirm_FromUnresolved_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var result = fixture.Confirm(attachment, asset);

            Assert.Equal(Phase7ResolutionEventType.Confirm, result.EventType);
            Assert.Equal(Phase7ResolutionStatus.Resolved, result.ResolutionStatus);
            Assert.Equal(asset, result.ResolvedMediaAssetID);
            Assert.Null(result.SupersedesEventID);
            Assert.Null(result.SourceRunID);

            Assert.Equal($"Resolved/{asset}", fixture.StoredProjection(attachment));
            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));

            var replayed = fixture.Replayed(attachment);

            Assert.Equal(Phase7ResolutionStatus.Resolved, replayed.Status);
            Assert.Equal(asset, replayed.ResolvedMediaAssetID);
            Assert.Equal(result.AttachmentResolutionEventID, replayed.CurrentEffectiveEventID);
        }

        /// <remarks>
        /// Neither half is visible from another connection until the commit, and both are visible after
        /// it. The event row and the projection change are one act.
        /// </remarks>
        [Fact]
        public void Confirm_WritesTheEventAndTheProjectionTogether()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var observedEvents = -1L;
            var observedProjection = "<not observed>";

            fixture.ConfirmWithHook(
                fixture.ConfirmRequest(attachment, asset),
                _ =>
                {
                    // A separate connection, so it sees only committed state.
                    using var outside = WorkspaceDatabase.OpenReadOnlyConnection(
                        fixture.DatabasePath);

                    using var command = outside.CreateCommand();

                    command.CommandText =
                        "SELECT (SELECT COUNT(*) FROM AttachmentResolutionEvent) || '/' || " +
                        $"(SELECT ResolutionStatus FROM Attachment WHERE AttachmentID = {attachment});";

                    observedProjection = (string)command.ExecuteScalar()!;
                    observedEvents = 0;
                });

            Assert.Equal(0, observedEvents);
            Assert.Equal("0/Unresolved", observedProjection);

            Assert.Equal($"Resolved/{asset}", fixture.StoredProjection(attachment));
            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Confirm_WithEmptyOrWhitespaceBasisDescription_IsRefused(string description)
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            Assert.Throws<ArgumentException>(
                () => fixture.Confirm(attachment, asset, description: description));

            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// A value cast into the enum from outside its members fails before anything is opened, so it
        /// can never reach the column whose CHECK would also reject it.
        /// </remarks>
        [Fact]
        public void Confirm_WithAnUnmappedBasisEnumValue_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            Assert.Throws<ArgumentOutOfRangeException>(
                () => fixture.Confirm(
                    attachment, asset, basisType: (Phase7IndependentBasisType)99));

            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// The database half of the same boundary: a basis string outside the closed vocabulary is
        /// rejected by the column itself, so there is no third value even for a hand-written statement.
        /// </remarks>
        [Fact]
        public void Confirm_WithAnUnsupportedStoredBasis_IsRejectedByTheDatabase()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            Assert.Throws<SqliteException>(
                () => fixture.ExecuteDirect(
                    $"""
                    INSERT INTO AttachmentResolutionEvent (
                        AttachmentID, EventType, MediaAssetID, IndependentBasisType,
                        IndependentBasisDescription, OccurredAtUtc)
                    VALUES (
                        {attachment}, 'Confirm', {asset}, 'RankingWasConvincing',
                        'Not a basis', '2026-08-27T09:00:00.0000000Z');
                    """));

            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        [Fact]
        public void Confirm_WithAnUnknownMediaAsset_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Confirm(attachment, mediaAssetID: 9999));

            Assert.Contains("no media asset 9999", error.Message, StringComparison.Ordinal);
            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));
        }

        [Fact]
        public void Confirm_WhenTheAttachmentIsAlreadyResolved_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            fixture.Confirm(attachment, first);

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Confirm(attachment, second));

            Assert.Contains(
                "valid only while attachment", error.Message, StringComparison.Ordinal);

            Assert.Contains("is a replacement", error.Message, StringComparison.Ordinal);

            Assert.Equal($"Resolved/{first}", fixture.StoredProjection(attachment));
            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// Unrepresentable through the service, and rejected by the schema for anything that bypasses it.
        /// </remarks>
        [Fact]
        public void Confirm_CannotCarryASupersedesReference()
        {
            var properties = typeof(Phase7ConfirmRequest)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name);

            Assert.DoesNotContain("SupersedesEventID", properties);

            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);
            fixture.Revoke(attachment, confirmed.AttachmentResolutionEventID);

            Assert.Throws<SqliteException>(
                () => fixture.InsertResolutionEventDirectly(
                    attachment,
                    "Confirm",
                    second,
                    confirmed.AttachmentResolutionEventID));

            Assert.Equal(2, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// The case <c>ExternalProvenanceOrGroundTruth</c> exists for. Requiring the target to be a
        /// Stage 7.1 candidate would make Phase 6 evidence a necessary condition for identity, which is
        /// the same closure boundary crossed from the other side.
        /// </remarks>
        [Fact]
        public void Confirm_ForAnAssetThatIsNoCandidateInAnyRun_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            // Dated ten days away, so it is neither an exact-date nor an adjacent-date candidate.
            var unrelated = fixture.Evidence.AddTokenAsset(Phase7TestFixture.Day(10), isSent: true, 5);

            var run = fixture.Evidence.Run();

            Assert.Equal(
                0,
                fixture.Evidence.ScalarLong(
                    $"SELECT COUNT(*) FROM AttachmentCandidate WHERE MediaAssetID = {unrelated};"));

            var result = fixture.Confirm(
                attachment,
                unrelated,
                Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth,
                Phase7DecisionFixture.ExternalBasis,
                sourceRunID: run.RunID);

            Assert.Equal(unrelated, result.ResolvedMediaAssetID);
            Assert.Equal($"Resolved/{unrelated}", fixture.StoredProjection(attachment));
        }

        [Fact]
        public void Confirm_WithNullSourceRunID_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var result = fixture.Confirm(attachment, asset, sourceRunID: null);

            Assert.Null(result.SourceRunID);

            Assert.Equal(
                "<null>",
                fixture.Evidence.ScalarText("SELECT SourceRunID FROM AttachmentResolutionEvent;"));
        }

        /// <remarks>
        /// Provenance has to be true when it is stated. A run that published nothing, and a run that
        /// never evaluated this attachment, are both refused — but only when cited, because null is
        /// always accepted.
        /// </remarks>
        [Fact]
        public void Confirm_WithASourceRunThatDidNotEvaluateTheAttachment_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.Evidence.AddOutgoing(date);
            var outsider = fixture.Evidence.AddOutgoingInOtherConversation(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var run = fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Confirm(outsider, asset, sourceRunID: run.RunID));

            Assert.Contains("no evidence snapshot row", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));

            // With no snapshot cited at all, the same confirmation is legitimate.
            Assert.Equal(
                asset, fixture.Confirm(outsider, asset, sourceRunID: null).ResolvedMediaAssetID);
        }

        /// <remarks>
        /// The deliberate exception to the currency rule. A resolution's source run records what the
        /// person was looking at, so a superseded snapshot is a true statement about that; requiring it
        /// to be current would fail a confirmation because an unrelated refresh had published.
        /// </remarks>
        [Fact]
        public void Confirm_WithASupersededButContainingSourceRunID_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            var first = fixture.Evidence.Run();
            var second = fixture.Evidence.Run();

            Assert.NotEqual(first.RunID, second.RunID);

            var result = fixture.Confirm(attachment, asset, sourceRunID: first.RunID);

            Assert.Equal(first.RunID, result.SourceRunID);
        }

        /// <remarks>
        /// No prior review, no disposition state, nothing. The axes are independent, so a confirmation
        /// on an untouched attachment is ordinary.
        /// </remarks>
        [Fact]
        public void Confirm_WithoutAnyReviewOrDisposition_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            Assert.Equal(0, fixture.EventCount("AttachmentReviewEvent"));
            Assert.Equal(0, fixture.EventCount("AttachmentCandidateDispositionEvent"));

            Assert.Equal(asset, fixture.Confirm(attachment, asset).ResolvedMediaAssetID);
        }

        [Fact]
        public void Confirm_WithNullActor_SucceedsAndWithWhitespaceActor_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var other = fixture.Evidence.AddIncoming(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);

            fixture.Evidence.Run();

            Assert.Throws<ArgumentException>(
                () => fixture.Confirm(attachment, asset, actorLabel: "  "));

            var result = fixture.Confirm(other, asset, actorLabel: null);

            Assert.Null(result.ActorLabel);
            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
        }

        // -----------------------------------------------------------------------------------------
        // ConfirmReplacement.
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Replace_FromResolvedWithTheCurrentEvent_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);

            var replaced = fixture.Replace(
                attachment, second, confirmed.AttachmentResolutionEventID);

            Assert.Equal(Phase7ResolutionEventType.ConfirmReplacement, replaced.EventType);
            Assert.Equal(Phase7ResolutionStatus.Resolved, replaced.ResolutionStatus);
            Assert.Equal(second, replaced.ResolvedMediaAssetID);

            Assert.Equal(
                confirmed.AttachmentResolutionEventID, replaced.SupersedesEventID);

            Assert.Equal($"Resolved/{second}", fixture.StoredProjection(attachment));
            Assert.Equal(2, fixture.EventCount("AttachmentResolutionEvent"));

            Assert.Equal(
                replaced.AttachmentResolutionEventID,
                fixture.Replayed(attachment).CurrentEffectiveEventID);
        }

        [Fact]
        public void Replace_WithAStaleEarlierEvent_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);
            var third = fixture.Evidence.AddTokenAsset(date, isSent: true, 3);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);
            fixture.Replace(attachment, second, confirmed.AttachmentResolutionEventID);

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Replace(attachment, third, confirmed.AttachmentResolutionEventID));

            Assert.Contains("current effective", error.Message, StringComparison.Ordinal);
            Assert.Equal($"Resolved/{second}", fixture.StoredProjection(attachment));
            Assert.Equal(2, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// Refused by the service, and independently forbidden by the schema's composite foreign key for
        /// anything that bypasses the service with foreign keys enabled.
        /// </remarks>
        [Fact]
        public void Replace_WithAnEventOfAnotherAttachment_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var one = fixture.Evidence.AddOutgoing(date);
            var two = fixture.Evidence.AddIncoming(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: null, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: null, 2);

            fixture.Evidence.Run();

            var onesConfirm = fixture.Confirm(one, first);
            fixture.Confirm(two, first);

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Replace(two, second, onesConfirm.AttachmentResolutionEventID));

            Assert.Contains("current effective", error.Message, StringComparison.Ordinal);

            Assert.Throws<SqliteException>(
                () => fixture.InsertResolutionEventDirectly(
                    two,
                    "ConfirmReplacement",
                    second,
                    onesConfirm.AttachmentResolutionEventID));

            Assert.Equal($"Resolved/{first}", fixture.StoredProjection(two));
            Assert.Equal(2, fixture.EventCount("AttachmentResolutionEvent"));
        }

        [Fact]
        public void Replace_WithANullSupersedesReference_IsRejectedByTheSchema()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            fixture.Confirm(attachment, first);

            Assert.Throws<SqliteException>(
                () => fixture.InsertResolutionEventDirectly(
                    attachment, "ConfirmReplacement", second, supersedesEventID: null));

            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
        }

        [Fact]
        public void Replace_WhenTheAttachmentIsUnresolved_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Replace(attachment, asset, supersedesEventID: 1));

            Assert.Contains("nothing to replace", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        [Theory]
        [InlineData("")]
        [InlineData(" ")]
        public void Replace_WithoutABasisDescription_IsRefused(string description)
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);

            Assert.Throws<ArgumentException>(
                () => fixture.Replace(
                    attachment,
                    second,
                    confirmed.AttachmentResolutionEventID,
                    description: description));

            Assert.Equal($"Resolved/{first}", fixture.StoredProjection(attachment));
        }

        /// <remarks>
        /// A replacement must actually replace. Each transition has exactly one job, so a replacement
        /// naming the asset already recorded would give one operation a second meaning — and there is
        /// deliberately no reaffirmation operation to serve that case.
        /// </remarks>
        [Fact]
        public void Replace_ToTheSameAsset_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, asset);
            var checkpoint = fixture.Checkpoint("before-same-asset.db");

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Replace(
                    attachment, asset, confirmed.AttachmentResolutionEventID));

            Assert.Contains("must change the asset", error.Message, StringComparison.Ordinal);
            Assert.Contains("no reaffirmation operation", error.Message, StringComparison.Ordinal);

            // No new event, an unchanged projection, and a history identical to before the attempt.
            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
            Assert.Equal($"Resolved/{asset}", fixture.StoredProjection(attachment));
            fixture.RequireDecisionTablesIdentical(checkpoint);

            Assert.Equal(
                confirmed.AttachmentResolutionEventID,
                fixture.Replayed(attachment).CurrentEffectiveEventID);
        }

        /// <remarks>
        /// The contrast case, so the refusal above is not vacuous, plus the rule's exact scope: the
        /// comparison is against the asset held <em>now</em>, not against the attachment's history.
        /// </remarks>
        [Fact]
        public void Replace_BackToAPreviouslyHeldAsset_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);

            var toSecond = fixture.Replace(
                attachment, second, confirmed.AttachmentResolutionEventID);

            var backToFirst = fixture.Replace(
                attachment, first, toSecond.AttachmentResolutionEventID);

            Assert.Equal(first, backToFirst.ResolvedMediaAssetID);
            Assert.Equal($"Resolved/{first}", fixture.StoredProjection(attachment));
            Assert.Equal(3, fixture.EventCount("AttachmentResolutionEvent"));

            fixture.VerifyAttachment(attachment);
        }

        // -----------------------------------------------------------------------------------------
        // Revoke.
        // -----------------------------------------------------------------------------------------

        [Fact]
        public void Revoke_AResolvedAttachment_Succeeds()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, asset);

            var revoked = fixture.Revoke(attachment, confirmed.AttachmentResolutionEventID);

            Assert.Equal(Phase7ResolutionEventType.Revoke, revoked.EventType);
            Assert.Equal(Phase7ResolutionStatus.Unresolved, revoked.ResolutionStatus);
            Assert.Null(revoked.ResolvedMediaAssetID);

            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));

            var replayed = fixture.Replayed(attachment);

            Assert.Equal(Phase7ResolutionStatus.Unresolved, replayed.Status);
            Assert.Null(replayed.ResolvedMediaAssetID);
            Assert.Null(replayed.CurrentEffectiveEventID);
        }

        [Fact]
        public void Revoke_WithoutTheCurrentEffectiveEvent_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);
            fixture.Replace(attachment, second, confirmed.AttachmentResolutionEventID);

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Revoke(attachment, confirmed.AttachmentResolutionEventID));

            Assert.Contains("current effective", error.Message, StringComparison.Ordinal);
            Assert.Equal($"Resolved/{second}", fixture.StoredProjection(attachment));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void Revoke_WithoutARevocationReason_IsRefused(string reason)
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, asset);

            Assert.Throws<ArgumentException>(
                () => fixture.Revoke(attachment, confirmed.AttachmentResolutionEventID, reason));

            Assert.Equal($"Resolved/{asset}", fixture.StoredProjection(attachment));
            Assert.Equal(1, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// The database also refuses an empty reason, so there is no path to a revocation that says
        /// nothing about why.
        /// </remarks>
        [Fact]
        public void Revoke_CarriesNoAssetOrBasisAndRequiresAReason()
        {
            var properties = typeof(Phase7RevokeRequest)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToList();

            Assert.DoesNotContain("MediaAssetID", properties);
            Assert.DoesNotContain("IndependentBasisType", properties);
            Assert.DoesNotContain("IndependentBasisDescription", properties);

            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var asset = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, asset);
            fixture.Revoke(attachment, confirmed.AttachmentResolutionEventID);

            Assert.Equal(
                "<null>,<null>,<null>",
                string.Join(
                    ",",
                    fixture.Evidence.Rows(
                        """
                        SELECT MediaAssetID, IndependentBasisType, IndependentBasisDescription
                        FROM AttachmentResolutionEvent
                        WHERE EventType = 'Revoke';
                        """)));

            Assert.Throws<SqliteException>(
                () => fixture.ExecuteDirect(
                    $"""
                    UPDATE AttachmentResolutionEvent
                    SET RevocationReason = ''
                    WHERE EventType = 'Revoke';
                    """));
        }

        [Fact]
        public void Revoke_WhenTheAttachmentIsUnresolved_IsRefused()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            fixture.Evidence.AddTokenAsset(date, isSent: true, 1);

            fixture.Evidence.Run();

            var error = Assert.Throws<InvalidOperationException>(
                () => fixture.Revoke(attachment, supersedesEventID: 1));

            Assert.Contains("nothing to undo", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, fixture.EventCount("AttachmentResolutionEvent"));
        }

        /// <remarks>
        /// Revoking a replacement leaves the attachment unresolved, not resolved to the asset the
        /// replacement displaced. There is no chain-walking undo.
        /// </remarks>
        [Fact]
        public void Revoke_OfAReplacement_DoesNotReactivateTheOlderAsset()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);

            var replaced = fixture.Replace(
                attachment, second, confirmed.AttachmentResolutionEventID);

            fixture.Revoke(attachment, replaced.AttachmentResolutionEventID);

            Assert.Equal("Unresolved/none", fixture.StoredProjection(attachment));
            Assert.Null(fixture.Replayed(attachment).ResolvedMediaAssetID);
            Assert.Null(fixture.Replayed(attachment).CurrentEffectiveEventID);
        }

        [Fact]
        public void Revoke_ThenFreshConfirm_RestoresAnAssetWithItsOwnBasis()
        {
            using var fixture = new Phase7DecisionFixture();
            var date = Phase7TestFixture.FirstDate;

            var attachment = fixture.Evidence.AddOutgoing(date);
            var first = fixture.Evidence.AddTokenAsset(date, isSent: true, 1);
            var second = fixture.Evidence.AddTokenAsset(date, isSent: true, 2);

            fixture.Evidence.Run();

            var confirmed = fixture.Confirm(attachment, first);

            var replaced = fixture.Replace(
                attachment, second, confirmed.AttachmentResolutionEventID);

            fixture.Revoke(attachment, replaced.AttachmentResolutionEventID);

            var restored = fixture.Confirm(
                attachment,
                first,
                Phase7IndependentBasisType.ExternalProvenanceOrGroundTruth,
                Phase7DecisionFixture.ExternalBasis);

            Assert.Null(restored.SupersedesEventID);
            Assert.Equal(first, restored.ResolvedMediaAssetID);
            Assert.Equal(4, fixture.EventCount("AttachmentResolutionEvent"));
            Assert.Equal($"Resolved/{first}", fixture.StoredProjection(attachment));

            fixture.VerifyAttachment(attachment);
        }
    }
}
