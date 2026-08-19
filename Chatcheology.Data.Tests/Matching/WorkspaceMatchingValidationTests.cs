using static Chatcheology.Data.Tests.Matching.MatchingTestData;

namespace Chatcheology.Data.Tests.Matching
{
    /// <summary>
    /// Tests what the analysis refuses to run against.
    /// </summary>
    /// <remarks>
    /// Schema version 2 permits a workspace whose media has been discovered but not yet hashed,
    /// because discovery legitimately precedes hashing. That is a valid workspace and an invalid
    /// input to matching: an unhashed file belongs to no asset, so analysing one would describe
    /// whatever fraction of the archive happened to be hashed as though it were the archive.
    /// <para>
    /// The same principle covers stored values that cannot be read under the format the workspace
    /// writes. Nothing is guessed at under a second format and nothing is skipped, because a date
    /// read the wrong way produces candidates for the wrong day without ever looking wrong.
    /// </para>
    /// </remarks>
    public class WorkspaceMatchingValidationTests
    {
        [Fact]
        public void FullyHashedAndLinkedMedia_IsAccepted()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);

            Assert.Single(AnalyseOne(workspace).ExactDateCandidates);
        }

        [Fact]
        public void UnhashedMediaFile_IsRefusedAsIncompleteHashing()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            workspace.AddMediaFile(
                sourceID, mediaAssetID: null, Hash(1), MessageDate, storedSHA256: "", link: false);

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("media hashing is incomplete", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void HashedFileWithNoAssetLink_IsRefused()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddMediaAsset(Hash(1));

            workspace.AddMediaFile(sourceID, mediaAssetID, Hash(1), MessageDate, link: false);

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("linked to no MediaAsset", error.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The foreign key forbids this, so the fixture has to write it with enforcement off — which
        /// is exactly how such a row could reach a real workspace.
        /// </remarks>
        [Fact]
        public void LinkPointingAtAnAssetThatDoesNotExist_IsRefused()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaFileID =
                workspace.AddMediaFile(sourceID, null, Hash(1), MessageDate, link: false);

            workspace.CloseBuildingConnection();

            workspace.ExecuteWithoutForeignKeys(
                $"""
                INSERT INTO MediaAssetFile (MediaAssetID, MediaFileID)
                VALUES (4242, {mediaFileID});
                """);

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// Both hash columns are declared <c>COLLATE NOCASE</c>, so to the database these are one
        /// value. A case-sensitive comparison would report a perfectly sound workspace as corrupt.
        /// </remarks>
        [Fact]
        public void HashesDifferingOnlyByLetterCase_AreTheSameHash()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddMediaAsset(Hash(1));

            workspace.AddMediaFile(
                sourceID,
                mediaAssetID,
                Hash(1),
                MessageDate,
                storedSHA256: Hash(1).ToLowerInvariant());

            Assert.Single(AnalyseOne(workspace).ExactDateCandidates);
        }

        [Fact]
        public void FileAndAssetRecordingDifferentHashes_IsRefused()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddMediaAsset(Hash(1));

            workspace.AddMediaFile(
                sourceID, mediaAssetID, Hash(1), MessageDate, storedSHA256: Hash(2));

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("different SHA-256", error.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The unhashed file here is undated and could never become a candidate for anything, which
        /// is precisely why it has to be checked: validating only the rows the analysis would go on
        /// to use would let an incomplete archive pass as a complete one.
        /// </remarks>
        [Fact]
        public void ValidationCoversRowsThatCouldNeverBecomeCandidates()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);

            workspace.AddMediaFile(
                sourceID,
                mediaAssetID: null,
                Hash(2),
                fileDate: null,
                storedSHA256: "",
                link: false);

            Assert.Throws<InvalidOperationException>(() => Analyse(workspace));
        }

        [Fact]
        public void FileDateThatIsNotTheStoredFormat_IsRefused()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);

            var sourceID = workspace.AddMediaSource();
            var mediaAssetID = workspace.AddAssetWithCopy(sourceID, Hash(1), MessageDate);

            workspace.Execute(
                $"""
                UPDATE MediaFile SET FileDate = '05/01/2026' WHERE MediaFileID =
                    (SELECT MediaFileID FROM MediaAssetFile WHERE MediaAssetID = {mediaAssetID});
                """);

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("calendar date format", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void MessageTimestampThatIsNotTheStoredFormat_IsRefused()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMessageWithRawTimestamp("2026-01-05 14:03:00");

            var error = Assert.Throws<InvalidOperationException>(() => Analyse(workspace));

            Assert.Contains("local wall-clock format", error.Message, StringComparison.Ordinal);
        }

        /// <remarks>
        /// The conversation's dates are counted by parsing every message, not by slicing the stored
        /// text in SQL, so a malformed timestamp is found even on a message that carries no
        /// attachment and would otherwise never be read.
        /// </remarks>
        [Fact]
        public void MalformedTimestampOnAMessageWithNoAttachment_IsAlsoRefused()
        {
            using var workspace = new MatchingTestWorkspace();
            workspace.AddMediaAttachment(MessageDate);
            workspace.AddMessageWithRawTimestamp("not a timestamp", withAttachment: false);

            Assert.Throws<InvalidOperationException>(() => Analyse(workspace));
        }

        [Fact]
        public void WorkspaceThatDoesNotExist_IsRefusedWithoutCreatingOne()
        {
            using var workspace = new MatchingTestWorkspace();

            var missingPath = Path.Combine(workspace.DirectoryPath, "absent.db");

            Assert.Throws<FileNotFoundException>(
                () => new Chatcheology.Data.Matching.WorkspaceMatchingService().Analyse(
                    missingPath,
                    new Chatcheology.Data.Matching.MatchAnalysisRequest(
                        MatchingTestWorkspace.ConversationID)));

            Assert.False(File.Exists(missingPath));
        }
    }
}
