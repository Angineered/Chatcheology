using Chatcheology.Data.Sequence;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// Tests what the census refuses to run on at all.
    /// </summary>
    /// <remarks>
    /// Two kinds of refusal appear here. Some conditions are caught by the frozen matching analysis the
    /// census starts from, because that analysis validates the same completed-Phase-5 media state; the
    /// test asserts the refusal, not which layer produced it. The name-and-date conditions are the
    /// census's own, because the matching engine never reads a file name.
    /// <para>
    /// Several of the census's checks are assertions about the frozen rules rather than reachable
    /// states, and are deliberately not tested here because no workspace this fixture can build reaches
    /// them: a cohort key resolving to a zero-byte asset, a direction-contradicting copy inside a
    /// compatible relation, one <c>(date, direction)</c> group naming two assets, one asset uniquely
    /// compatible in both directions on one date, two cohort relations sharing a message sequence
    /// number, and a cohort relation with no qualifying copy. Each is unreachable because the frozen
    /// analysis or the schema forbids it, which is precisely why the assertion is there.
    /// </para>
    /// </remarks>
    public class CrossDirectionSequenceIntegrityTests
    {
        // ---------------------------------------------------------------------------------------
        // The census's own name-and-date rules.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// A dated row whose name holds no marker means this census and the committed classifier
        /// disagree about which characters are a date, and every token rests on that agreement.
        /// </remarks>
        [Fact]
        public void DatedCopyWithNoLocatableMarker_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            // The matching fixture's neutral default name carries no marker at all.
            fixture.Workspace.AddAssetWithCopy(
                fixture.SourceID, MatchingTestData.Hash(90), date, isSent: true);

            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Analyse());

            Assert.Contains("no locatable", failure.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MarkerDateDisagreeingWithThePersistedFileDate_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                fileName: SequenceTestFixture.Name(date.AddDays(-3), "0001") + ".jpg",
                extension: ".jpg");

            var failure = Assert.Throws<InvalidOperationException>(() => fixture.Analyse());

            Assert.Contains("not the date its own name", failure.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// An undated row is not required to hold a marker or to lack one: Stage B1 settled the
        /// archive-wide converse, and re-deriving it here would need source-type semantics this census
        /// has no other use for.
        /// </remarks>
        [Fact]
        public void UndatedCopyCarryingAMarkerName_IsAccepted()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddCleanDate(date, outgoingToken: 4, incomingToken: 9);

            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                fileDate: null,
                isSent: true,
                fileName: SequenceTestFixture.Name(date, "0055") + ".jpg",
                extension: ".jpg");

            Assert.Equal(1, fixture.Analyse().PrimaryOrder.ObservationCount);
        }

        // ---------------------------------------------------------------------------------------
        // Completed-Phase-5 media state.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void UnhashedMediaFile_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                storedSHA256: string.Empty,
                fileName: SequenceTestFixture.Name(date, "0001") + ".jpg",
                extension: ".jpg");

            Assert.Throws<InvalidOperationException>(() => fixture.Analyse());
        }

        [Fact]
        public void HashedFileLinkedToNoAsset_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                link: false,
                fileName: SequenceTestFixture.Name(date, "0001") + ".jpg",
                extension: ".jpg");

            Assert.Throws<InvalidOperationException>(() => fixture.Analyse());
        }

        [Fact]
        public void FileAndAssetRecordingDifferentHashes_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);

            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                storedSHA256: MatchingTestData.Hash(91),
                fileName: SequenceTestFixture.Name(date, "0001") + ".jpg",
                extension: ".jpg");

            Assert.Throws<InvalidOperationException>(() => fixture.Analyse());
        }

        /// <remarks>
        /// Both hash columns are declared <c>COLLATE NOCASE</c>, so one hash written in each case is one
        /// value to the database and must be one value here.
        /// </remarks>
        [Fact]
        public void HashesDifferingOnlyByLetterCase_AreTheSameHash()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddIncoming(date);

            var sha = MatchingTestData.Hash(90);
            var assetID = fixture.Workspace.AddMediaAsset(sha);

            fixture.Workspace.AddMediaFile(
                fixture.SourceID,
                assetID,
                sha,
                date,
                isSent: true,
                storedSHA256: sha.ToLowerInvariant(),
                fileName: SequenceTestFixture.Name(date, "0001") + ".jpg",
                extension: ".jpg");

            fixture.AddTokenAsset(date, isSent: false, 9);

            Assert.Equal(1, fixture.Analyse().PrimaryOrder.ObservationCount);
        }

        [Fact]
        public void FileDateNotInTheFormatTheWorkspaceWrites_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            var date = SequenceTestFixture.FirstDate;

            fixture.AddOutgoing(date);
            fixture.AddTokenAsset(date, isSent: true, 1);

            fixture.Workspace.Execute("UPDATE MediaFile SET FileDate = '02/03/2026';");

            Assert.Throws<InvalidOperationException>(() => fixture.Analyse());
        }

        // ---------------------------------------------------------------------------------------
        // The request itself.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void UnknownConversation_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, 1, 2);

            var service = new CrossDirectionSequenceCensusService();

            Assert.Throws<InvalidOperationException>(
                () => service.Analyse(
                    new CrossDirectionSequenceCensusRequest
                    {
                        DatabasePath = fixture.Workspace.DatabasePath,
                        ConversationID = 99,
                        LocalParticipantID = MatchingTestWorkspace.LocalParticipantID,
                    }));
        }

        [Fact]
        public void LocalParticipantFromAnotherConversation_IsRefused()
        {
            using var fixture = new SequenceTestFixture();
            fixture.AddCleanDate(SequenceTestFixture.FirstDate, 1, 2);

            var service = new CrossDirectionSequenceCensusService();

            Assert.Throws<InvalidOperationException>(
                () => service.Analyse(
                    new CrossDirectionSequenceCensusRequest
                    {
                        DatabasePath = fixture.Workspace.DatabasePath,
                        ConversationID = MatchingTestWorkspace.ConversationID,
                        LocalParticipantID = MatchingTestWorkspace.OutsiderParticipantID,
                    }));
        }

        [Fact]
        public void MissingWorkspaceFile_IsRefusedWithoutCreatingOne()
        {
            var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.db");

            var service = new CrossDirectionSequenceCensusService();

            Assert.Throws<FileNotFoundException>(
                () => service.Analyse(
                    new CrossDirectionSequenceCensusRequest
                    {
                        DatabasePath = path,
                        ConversationID = 1,
                        LocalParticipantID = 2,
                    }));

            Assert.False(File.Exists(path));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public void BlankDatabasePath_IsRefused(string path)
        {
            var service = new CrossDirectionSequenceCensusService();

            Assert.Throws<ArgumentException>(
                () => service.Analyse(
                    new CrossDirectionSequenceCensusRequest
                    {
                        DatabasePath = path,
                        ConversationID = 1,
                        LocalParticipantID = 2,
                    }));
        }

        [Fact]
        public void NullRequest_IsRefused() =>
            Assert.Throws<ArgumentNullException>(
                () => new CrossDirectionSequenceCensusService().Analyse(null!));
    }
}
