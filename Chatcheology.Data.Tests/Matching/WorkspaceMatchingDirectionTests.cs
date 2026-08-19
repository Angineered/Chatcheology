using Chatcheology.Data.Matching;
using static Chatcheology.Data.Tests.Matching.MatchingTestData;

namespace Chatcheology.Data.Tests.Matching
{
    /// <summary>
    /// Tests how folder direction is read, and how narrowly it is allowed to speak.
    /// </summary>
    /// <remarks>
    /// Two rules carry most of the weight here. Direction exists at all only when the caller states
    /// which participant is the local user — it is never inferred from a name or from the shape of
    /// the conversation. And a verdict is reached from the supporting copies alone: a copy of the
    /// same payload sitting on an unrelated date has nothing to say about this message.
    /// <para>
    /// <c>IsSent = 0</c> is never read as "received". It means the source has <c>Sent</c> structure
    /// and this copy was not beneath it, which is weaker, and the vocabulary here keeps it weaker.
    /// </para>
    /// </remarks>
    public class WorkspaceMatchingDirectionTests
    {
        [Fact]
        public void OutgoingMessageWithAnExactDateSentFolderCopy_IsCompatible()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: true);

            var analysis = AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID);

            Assert.Equal(MessageDirection.Outgoing, analysis.MessageDirection);

            var candidate = Assert.Single(analysis.ExactDateCandidates);

            Assert.Equal(DirectionCompatibility.Compatible, candidate.DirectionCompatibility);
            Assert.True(candidate.HasSupportingSentFolderCopy);
            Assert.False(candidate.HasSupportingNotUnderSentFolderCopy);
        }

        [Fact]
        public void OutgoingMessageWithOnlyANotUnderSentCopy_IsContradictoryOnly()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: false);

            var candidate = Assert.Single(
                AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID)
                    .ExactDateCandidates);

            Assert.Equal(
                DirectionCompatibility.ContradictoryOnly, candidate.DirectionCompatibility);
        }

        [Fact]
        public void IncomingMessageWithANotUnderSentCopy_IsCompatible()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.OtherParticipantID);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: false);

            var analysis = AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID);

            Assert.Equal(MessageDirection.Incoming, analysis.MessageDirection);

            Assert.Equal(
                DirectionCompatibility.Compatible,
                Assert.Single(analysis.ExactDateCandidates).DirectionCompatibility);
        }

        [Fact]
        public void IncomingMessageWithOnlyASentFolderCopy_IsContradictoryOnly()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.OtherParticipantID);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: true);

            var candidate = Assert.Single(
                AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID)
                    .ExactDateCandidates);

            Assert.Equal(
                DirectionCompatibility.ContradictoryOnly, candidate.DirectionCompatibility);
        }

        [Fact]
        public void AgreeingAndDisagreeingSupportingCopies_AreMixed()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID =
                workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: true);

            workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), MessageDate, isSent: false);

            var candidate = Assert.Single(
                AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID)
                    .ExactDateCandidates);

            Assert.Equal(DirectionCompatibility.Mixed, candidate.DirectionCompatibility);
            Assert.True(candidate.HasSupportingSentFolderCopy);
            Assert.True(candidate.HasSupportingNotUnderSentFolderCopy);
        }

        /// <remarks>
        /// The rule that a copy on an unrelated date cannot make a relationship mixed. The payload
        /// survives twice: beneath <c>Sent</c> on the message's own date, and outside <c>Sent</c>
        /// months earlier. Only the first supports this attachment.
        /// </remarks>
        [Fact]
        public void ContradictoryCopyOnAnUnrelatedDate_DoesNotMakeTheRelationshipMixed()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID =
                workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: true);

            workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), UnrelatedDate, isSent: false);

            var candidate = Assert.Single(
                AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID)
                    .ExactDateCandidates);

            Assert.Equal(DirectionCompatibility.Compatible, candidate.DirectionCompatibility);
            Assert.False(candidate.HasSupportingNotUnderSentFolderCopy);
            Assert.Equal(2, candidate.PhysicalCopyCount);
            Assert.Equal(1, candidate.SupportingPhysicalCopyCount);
        }

        /// <remarks>
        /// The same rule from the other side: an adjacent-date candidate is judged by its
        /// qualifying adjacent-day copies, not by copies of the payload elsewhere in the archive.
        /// </remarks>
        [Fact]
        public void AdjacentCandidate_IsJudgedByItsQualifyingAdjacentDayCopiesOnly()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();

            var mediaAssetID =
                workspace.AddAssetWithCopy(sourceID, Hash(1), PreviousDay, isSent: true);

            workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), UnrelatedDate, isSent: false);

            var candidate = Assert.Single(
                AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID)
                    .AdjacentDateCandidates);

            Assert.Equal(DirectionCompatibility.Compatible, candidate.DirectionCompatibility);
            Assert.Equal(1, candidate.SupportingPhysicalCopyCount);
        }

        [Fact]
        public void SupportingCopiesThatRecordNoDirection_AreUnknown()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: null);

            var candidate = Assert.Single(
                AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID)
                    .ExactDateCandidates);

            Assert.Equal(DirectionCompatibility.Unknown, candidate.DirectionCompatibility);
            Assert.True(candidate.HasSupportingDirectionUnknownCopy);
        }

        /// <remarks>
        /// A source recovered without any <c>Sent</c> structure records null for every file. That
        /// must never be read as evidence of anything, least of all as "not sent": for an incoming
        /// message it would otherwise look like agreement.
        /// </remarks>
        [Fact]
        public void SourceRecordingNoDirection_NeverBecomesNotUnderSentEvidence()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.OtherParticipantID);

            var legacySourceID = workspace.AddMediaSource("Recovered legacy copy");
            workspace.AddAssetWithCopy(legacySourceID, Hash(1), MessageDate, isSent: null);

            var analysis = AnalyseOne(workspace, MatchingTestWorkspace.LocalParticipantID);

            Assert.Equal(MessageDirection.Incoming, analysis.MessageDirection);

            var candidate = Assert.Single(analysis.ExactDateCandidates);

            Assert.False(candidate.HasSupportingNotUnderSentFolderCopy);
            Assert.Equal(DirectionCompatibility.Unknown, candidate.DirectionCompatibility);
        }

        [Fact]
        public void NoLocalParticipant_LeavesEveryDirectionUnknown()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: true);

            var run = Analyse(workspace);
            var analysis = Assert.Single(run.Analyses);

            Assert.False(run.Census.LocalParticipantIDSupplied);
            Assert.Equal(MessageDirection.Unknown, analysis.MessageDirection);

            Assert.Equal(
                DirectionCompatibility.Unknown,
                Assert.Single(analysis.ExactDateCandidates).DirectionCompatibility);
        }

        [Fact]
        public void MessageWithNoSender_IsUnknownAndIsNotAnError()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, senderParticipantID: null);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: true);

            var run = Analyse(workspace, MatchingTestWorkspace.LocalParticipantID);
            var analysis = Assert.Single(run.Analyses);

            Assert.Null(analysis.SenderParticipantID);
            Assert.Equal(MessageDirection.Unknown, analysis.MessageDirection);
            Assert.Equal(1, run.Census.AttachmentsOnMessagesWithNullSender);

            Assert.Equal(
                DirectionCompatibility.Unknown,
                Assert.Single(analysis.ExactDateCandidates).DirectionCompatibility);
        }

        /// <remarks>
        /// One attachment can reach several direction states at once, and the census counts it in
        /// each. Reporting only the best state an attachment reached would hide the contradictory
        /// candidate sitting beside the compatible one.
        /// </remarks>
        [Fact]
        public void AttachmentDirectionStateCounts_AreNotMutuallyExclusive()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var sourceID = workspace.AddMediaSource();

            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate, isSent: true);
            workspace.AddAssetWithCopy(sourceID, Hash(2), MessageDate, isSent: false);
            workspace.AddAssetWithCopy(sourceID, Hash(3), MessageDate, isSent: null);

            var mixed = workspace.AddAssetWithCopy(sourceID, Hash(4), MessageDate, isSent: true);
            workspace.AddMediaFile(sourceID, mixed, Hash(4), MessageDate, isSent: false);

            var census = Analyse(workspace, MatchingTestWorkspace.LocalParticipantID).Census;

            Assert.Equal(1, census.AttachmentsWithAtLeastOneCompatibleExactCandidate);
            Assert.Equal(1, census.AttachmentsWithAtLeastOneMixedExactCandidate);
            Assert.Equal(1, census.AttachmentsWithAtLeastOneUnknownDirectionExactCandidate);
            Assert.Equal(1, census.AttachmentsWithAtLeastOneContradictoryOnlyExactCandidate);

            Assert.Equal(1, census.ExactCandidateRelationsCompatible);
            Assert.Equal(1, census.ExactCandidateRelationsMixed);
            Assert.Equal(1, census.ExactCandidateRelationsUnknown);
            Assert.Equal(1, census.ExactCandidateRelationsContradictoryOnly);
        }

        // ---------------------------------------------------------------------------------------
        // The local participant the caller supplies.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void LocalParticipantOfTheConversation_IsAccepted()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate, MatchingTestWorkspace.LocalParticipantID);

            var run = Analyse(workspace, MatchingTestWorkspace.LocalParticipantID);

            Assert.True(run.Census.LocalParticipantIDSupplied);
            Assert.Equal(MessageDirection.Outgoing, Assert.Single(run.Analyses).MessageDirection);
        }

        [Fact]
        public void ParticipantOfAnotherConversation_IsRejected()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var error = Assert.Throws<InvalidOperationException>(
                () => Analyse(workspace, MatchingTestWorkspace.OutsiderParticipantID));

            Assert.Contains("does not belong to conversation", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ParticipantThatDoesNotExist_IsRejected()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            Assert.Throws<InvalidOperationException>(() => Analyse(workspace, 9999));
        }

        [Fact]
        public void NullLocalParticipant_IsValidAndSimplyDisablesDirection()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var run = Analyse(workspace, localParticipantID: null);

            Assert.False(run.Census.LocalParticipantIDSupplied);
            Assert.Equal(MessageDirection.Unknown, Assert.Single(run.Analyses).MessageDirection);
        }

        [Fact]
        public void ConversationThatDoesNotExist_IsRejected()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var error = Assert.Throws<InvalidOperationException>(
                () => Analyse(workspace, conversationID: 99));

            Assert.Contains("no conversation", error.Message, StringComparison.Ordinal);
        }
    }
}
