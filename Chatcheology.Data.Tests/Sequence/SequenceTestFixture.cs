using System.Globalization;
using Chatcheology.Data.Sequence;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// A synthetic workspace shaped for the cross-direction sequence census.
    /// </summary>
    /// <remarks>
    /// Built on the matching fixture, because this census starts from the frozen matching analysis and
    /// therefore needs real messages, attachments and participants as well as media.
    /// <para>
    /// Every dated copy is given a name carrying the WhatsApp marker, which the census requires of any
    /// dated row it meets. The matching fixture's neutral default name has no marker, so a dated copy
    /// left with it is a deliberate failure case rather than a convenience.
    /// </para>
    /// <para>
    /// Everything here is invented: two fictional participants, fictional hashes, and names that name
    /// nothing real.
    /// </para>
    /// </remarks>
    internal sealed class SequenceTestFixture : IDisposable
    {
        /// <summary>The date most fixtures put their first cross-direction pair on.</summary>
        internal static readonly DateOnly FirstDate = new(2026, 3, 2);

        private const string DefaultExtension = ".jpg";

        private readonly MatchingTestWorkspace _workspace = new();
        private readonly long _sourceID;

        private int _hashNumber;

        internal SequenceTestFixture() => _sourceID = _workspace.AddMediaSource();

        internal MatchingTestWorkspace Workspace => _workspace;

        internal long SourceID => _sourceID;

        /// <summary>A date a fixed number of days after <see cref="FirstDate"/>.</summary>
        internal static DateOnly Day(int offset) => FirstDate.AddDays(offset);

        /// <summary>A recovered name carrying the marker for <paramref name="date"/>.</summary>
        internal static string Name(DateOnly date, string suffix, string prefix = "IMG") =>
            $"{prefix}-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-WA{suffix}";

        /// <summary>A supported four-digit suffix.</summary>
        internal static string Token(int token) =>
            token.ToString("D4", CultureInfo.InvariantCulture);

        /// <summary>A message from the local participant, with its unresolved attachment.</summary>
        internal long AddOutgoing(DateOnly date, string time = "09:00:00") =>
            _workspace.AddMediaAttachment(
                date, MatchingTestWorkspace.LocalParticipantID, time: time);

        /// <summary>A message from the other participant, with its unresolved attachment.</summary>
        internal long AddIncoming(DateOnly date, string time = "09:05:00") =>
            _workspace.AddMediaAttachment(
                date, MatchingTestWorkspace.OtherParticipantID, time: time);

        /// <summary>An asset with one dated copy per supported token.</summary>
        internal long AddTokenAsset(DateOnly date, bool? isSent, params int[] tokens) =>
            AddNamedAsset(date, isSent, [.. tokens.Select(Token)]);

        /// <summary>An asset with one dated copy per suffix, supported or not.</summary>
        internal long AddNamedAsset(DateOnly date, bool? isSent, params string[] suffixes)
        {
            var sha = MatchingTestData.Hash(++_hashNumber);
            var mediaAssetID = _workspace.AddMediaAsset(sha);

            foreach (var suffix in suffixes)
            {
                AddCopy(mediaAssetID, sha, date, isSent, suffix);
            }

            return mediaAssetID;
        }

        /// <summary>One more copy of an asset already in the workspace.</summary>
        /// <param name="mediaSourceID">
        /// Which acquisition store the copy was recovered from, for a test that needs the same payload
        /// and the same recovered position found twice.
        /// </param>
        internal void AddCopy(
            long mediaAssetID,
            string sha256,
            DateOnly date,
            bool? isSent,
            string suffix,
            string extension = DefaultExtension,
            long? mediaSourceID = null) =>
            _workspace.AddMediaFile(
                mediaSourceID ?? _sourceID,
                mediaAssetID,
                sha256,
                date,
                isSent,
                fileName: Name(date, suffix) + extension,
                extension: extension);

        /// <summary>A second acquisition store, for duplicate-collapse tests.</summary>
        internal long AddSource(string displayName = "Second synthetic source") =>
            _workspace.AddMediaSource(displayName);

        /// <summary>An asset with one dated copy per token, all in one acquisition store.</summary>
        internal long AddAssetAtTokens(DateOnly date, bool? isSent, params int[] tokens) =>
            AddTokenAsset(date, isSent, tokens);

        /// <summary>The hash an asset added through this fixture was given.</summary>
        internal static string HashOf(int assetNumber) => MatchingTestData.Hash(assetNumber);

        /// <summary>How many assets this fixture has added.</summary>
        internal int AssetCount => _hashNumber;

        /// <summary>
        /// One date carrying exactly one outgoing and one incoming relation, each resting on its own
        /// single-token asset: the only shape the strict order test uses.
        /// </summary>
        internal void AddCleanDate(
            DateOnly date, int outgoingToken, int incomingToken, bool outgoingFirst = true)
        {
            if (outgoingFirst)
            {
                AddOutgoing(date);
                AddIncoming(date);
            }
            else
            {
                AddIncoming(date);
                AddOutgoing(date);
            }

            AddTokenAsset(date, isSent: true, outgoingToken);
            AddTokenAsset(date, isSent: false, incomingToken);
        }

        internal CrossDirectionSequenceCensus Analyse(
            CancellationToken cancellationToken = default) =>
            new CrossDirectionSequenceCensusService().Analyse(
                new CrossDirectionSequenceCensusRequest
                {
                    DatabasePath = _workspace.DatabasePath,
                    ConversationID = MatchingTestWorkspace.ConversationID,
                    LocalParticipantID = MatchingTestWorkspace.LocalParticipantID,
                },
                cancellationToken);

        /// <summary>Runs the within-direction assignment census over this workspace.</summary>
        internal WithinDirectionAssignmentCensus AnalyseAssignments(
            CancellationToken cancellationToken = default) =>
            new WithinDirectionAssignmentCensusService().Analyse(
                new WithinDirectionAssignmentCensusRequest
                {
                    DatabasePath = _workspace.DatabasePath,
                    ConversationID = MatchingTestWorkspace.ConversationID,
                    LocalParticipantID = MatchingTestWorkspace.LocalParticipantID,
                },
                cancellationToken);

        public void Dispose() => _workspace.Dispose();
    }
}
