using Chatcheology.Data.Phase7;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The transition rules themselves, over hand-built sequences and without a database.
    /// </summary>
    /// <remarks>
    /// Replay is the specification the resolution service derives its validation from, so it is tested
    /// directly — including over sequences the service can never produce, which is the only way to show
    /// that an impossible history is reported rather than smoothed over.
    /// </remarks>
    public class Phase7ResolutionReplayTests
    {
        private const long Attachment = 7;

        [Fact]
        public void Apply_AnEmptyHistory_IsUnresolvedWithNoCurrentEvent()
        {
            var projection = Phase7ResolutionReplay.Apply([]);

            Assert.Equal(Phase7ResolutionStatus.Unresolved, projection.Status);
            Assert.Null(projection.ResolvedMediaAssetID);
            Assert.Null(projection.CurrentEffectiveEventID);
        }

        [Fact]
        public void Apply_Confirm_IsResolvedToItsAssetWithItselfCurrent()
        {
            var projection = Phase7ResolutionReplay.Apply([Confirm(1, asset: 100)]);

            Assert.Equal(Phase7ResolutionStatus.Resolved, projection.Status);
            Assert.Equal(100, projection.ResolvedMediaAssetID);
            Assert.Equal(1, projection.CurrentEffectiveEventID);
        }

        [Fact]
        public void Apply_ConfirmThenRevoke_IsUnresolvedWithNoCurrentEvent()
        {
            var projection = Phase7ResolutionReplay.Apply(
                [Confirm(1, asset: 100), Revoke(2, supersedes: 1)]);

            Assert.Equal(Phase7ResolutionStatus.Unresolved, projection.Status);
            Assert.Null(projection.ResolvedMediaAssetID);
            Assert.Null(projection.CurrentEffectiveEventID);
        }

        [Fact]
        public void Apply_ConfirmThenReplacement_IsResolvedToTheNewAsset()
        {
            var projection = Phase7ResolutionReplay.Apply(
                [Confirm(1, asset: 100), Replacement(2, asset: 200, supersedes: 1)]);

            Assert.Equal(Phase7ResolutionStatus.Resolved, projection.Status);
            Assert.Equal(200, projection.ResolvedMediaAssetID);
            Assert.Equal(2, projection.CurrentEffectiveEventID);
        }

        [Fact]
        public void Apply_ConfirmReplacementThenRevoke_DoesNotReactivateTheOlderAsset()
        {
            var projection = Phase7ResolutionReplay.Apply(
                [
                    Confirm(1, asset: 100),
                    Replacement(2, asset: 200, supersedes: 1),
                    Revoke(3, supersedes: 2),
                ]);

            Assert.Equal(Phase7ResolutionStatus.Unresolved, projection.Status);
            Assert.Null(projection.ResolvedMediaAssetID);
            Assert.Null(projection.CurrentEffectiveEventID);
        }

        [Fact]
        public void Apply_ConfirmReplacementRevokeThenFreshConfirm_IsResolvedToTheNewest()
        {
            var projection = Phase7ResolutionReplay.Apply(
                [
                    Confirm(1, asset: 100),
                    Replacement(2, asset: 200, supersedes: 1),
                    Revoke(3, supersedes: 2),
                    Confirm(4, asset: 100),
                ]);

            Assert.Equal(Phase7ResolutionStatus.Resolved, projection.Status);
            Assert.Equal(100, projection.ResolvedMediaAssetID);
            Assert.Equal(4, projection.CurrentEffectiveEventID);
        }

        /// <remarks>
        /// The frozen replay-ordering rule, made concrete. The entries carry descending timestamps, so a
        /// replay that sorted by <c>OccurredAtUtc</c> would apply them in the opposite order — and the
        /// test computes that wrong answer from the same rows, so the assertion is that identifier order
        /// gives a different and correct result rather than merely that this order works.
        /// </remarks>
        [Fact]
        public void Apply_WithTimestampsInReverseOrder_StillReplaysByEventIdentifier()
        {
            var byIdentifier = new List<Phase7ResolutionHistoryEntry>
            {
                Confirm(1, asset: 100, occurredAt: new DateTime(2026, 8, 27, 12, 0, 0, DateTimeKind.Utc)),
                Replacement(
                    2,
                    asset: 200,
                    supersedes: 1,
                    occurredAt: new DateTime(2026, 8, 27, 11, 0, 0, DateTimeKind.Utc)),
                Revoke(
                    3,
                    supersedes: 2,
                    occurredAt: new DateTime(2026, 8, 27, 10, 0, 0, DateTimeKind.Utc)),
            };

            var correct = Phase7ResolutionReplay.Apply(byIdentifier);

            Assert.Equal(Phase7ResolutionStatus.Unresolved, correct.Status);

            // What a timestamp-ordering implementation would have been handed instead.
            var byTimestamp = byIdentifier.OrderBy(entry => entry.OccurredAtUtc).ToList();

            Assert.NotEqual(
                byIdentifier.Select(entry => entry.AttachmentResolutionEventID),
                byTimestamp.Select(entry => entry.AttachmentResolutionEventID));

            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply(byTimestamp));

            Assert.Contains("impossible resolution history", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Apply_ConfirmWhileAlreadyResolved_IsReported()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply([Confirm(1, asset: 100), Confirm(2, asset: 200)]));

            Assert.Contains("Confirm applied to a Resolved state", error.Message, StringComparison.Ordinal);
            Assert.Contains("valid only while the attachment is unresolved", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Apply_ReplacementWhileUnresolved_IsReported()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply([Replacement(1, asset: 100, supersedes: 9)]));

            Assert.Contains("nothing to replace", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Apply_RevokeWhileUnresolved_IsReported()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply([Revoke(1, supersedes: 9)]));

            Assert.Contains("nothing to undo", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Apply_ReplacementNamingAStaleEarlierEvent_IsReported()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply(
                    [
                        Confirm(1, asset: 100),
                        Replacement(2, asset: 200, supersedes: 1),
                        Replacement(3, asset: 300, supersedes: 1),
                    ]));

            Assert.Contains("current effective event, which is 2", error.Message, StringComparison.Ordinal);
            Assert.Contains("it names 1", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Apply_RevokeNamingAStaleEarlierEvent_IsReported()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply(
                    [
                        Confirm(1, asset: 100),
                        Replacement(2, asset: 200, supersedes: 1),
                        Revoke(3, supersedes: 1),
                    ]));

            Assert.Contains("current effective event", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Apply_ConfirmCarryingASupersedesReference_IsReported()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply(
                    [
                        Confirm(1, asset: 100),
                        Revoke(2, supersedes: 1),
                        Confirm(3, asset: 100, supersedes: 1),
                    ]));

            Assert.Contains("supersedes nothing", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Apply_AnEventSupersedingItself_IsReported()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply(
                    [Confirm(1, asset: 100), Replacement(2, asset: 200, supersedes: 2)]));

            Assert.Contains("it names 2", error.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The rule the correction pass added to replay as well as to the service, so the two cannot
        /// drift: a replacement naming the asset the state already holds is an impossible history.
        /// </remarks>
        [Fact]
        public void Apply_AReplacementToTheSameAsset_IsReported()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply(
                    [Confirm(1, asset: 100), Replacement(2, asset: 100, supersedes: 1)]));

            Assert.Contains("must change the asset", error.Message, StringComparison.Ordinal);
            Assert.Contains("no reaffirmation operation", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Apply_AReplacementWithNoAsset_IsReported()
        {
            var error = Assert.Throws<InvalidOperationException>(
                () => Phase7ResolutionReplay.Apply(
                    [
                        Confirm(1, asset: 100),
                        new Phase7ResolutionHistoryEntry
                        {
                            AttachmentResolutionEventID = 2,
                            AttachmentID = Attachment,
                            EventType = Phase7ResolutionEventType.ConfirmReplacement,
                            MediaAssetID = null,
                            IndependentBasisType = null,
                            IndependentBasisDescription = null,
                            RevocationReason = null,
                            SupersedesEventID = 1,
                            SourceRunID = null,
                            ActorLabel = null,
                            OccurredAtUtc = Instant,
                        },
                    ]));

            Assert.Contains("with no asset", error.Message, StringComparison.Ordinal);
        }

        // -----------------------------------------------------------------------------------------
        // Builders.
        // -----------------------------------------------------------------------------------------

        private static readonly DateTime Instant =
            new(2026, 8, 27, 9, 0, 0, DateTimeKind.Utc);

        private static Phase7ResolutionHistoryEntry Confirm(
            long eventID, long asset, long? supersedes = null, DateTime? occurredAt = null) =>
            new()
            {
                AttachmentResolutionEventID = eventID,
                AttachmentID = Attachment,
                EventType = Phase7ResolutionEventType.Confirm,
                MediaAssetID = asset,
                IndependentBasisType =
                    Phase7IndependentBasisType.HumanContentOrContextInspection,
                IndependentBasisDescription = "Recognised the content.",
                RevocationReason = null,
                SupersedesEventID = supersedes,
                SourceRunID = null,
                ActorLabel = null,
                OccurredAtUtc = occurredAt ?? Instant,
            };

        private static Phase7ResolutionHistoryEntry Replacement(
            long eventID, long asset, long supersedes, DateTime? occurredAt = null) =>
            new()
            {
                AttachmentResolutionEventID = eventID,
                AttachmentID = Attachment,
                EventType = Phase7ResolutionEventType.ConfirmReplacement,
                MediaAssetID = asset,
                IndependentBasisType =
                    Phase7IndependentBasisType.HumanContentOrContextInspection,
                IndependentBasisDescription = "Recognised the content, differently.",
                RevocationReason = null,
                SupersedesEventID = supersedes,
                SourceRunID = null,
                ActorLabel = null,
                OccurredAtUtc = occurredAt ?? Instant,
            };

        private static Phase7ResolutionHistoryEntry Revoke(
            long eventID, long supersedes, DateTime? occurredAt = null) =>
            new()
            {
                AttachmentResolutionEventID = eventID,
                AttachmentID = Attachment,
                EventType = Phase7ResolutionEventType.Revoke,
                MediaAssetID = null,
                IndependentBasisType = null,
                IndependentBasisDescription = null,
                RevocationReason = "No longer believed.",
                SupersedesEventID = supersedes,
                SourceRunID = null,
                ActorLabel = null,
                OccurredAtUtc = occurredAt ?? Instant,
            };
    }
}
