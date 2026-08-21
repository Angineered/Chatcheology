using System.Globalization;
using Chatcheology.Data.Media;
using Chatcheology.Data.Sequence;
using Chatcheology.Data.Tests.Matching;

namespace Chatcheology.Data.Tests.Sequence
{
    /// <summary>
    /// A synthetic workspace shaped for the Stage B2C-0 direction-sequence gate.
    /// </summary>
    /// <remarks>
    /// Built on the matching fixture, because the gate reads real messages, attachments and
    /// participants as well as media — but not through the matching engine: the gate consumes no
    /// candidate, so nothing here sets one up.
    /// <para>
    /// Media is added by <c>(date, token)</c> rather than by asset, because a logical token position is
    /// what the gate measures. Each call adds its own payload unless told to reuse one, so a position
    /// observed on two sources can be built either as one copied file or as two coincidentally
    /// identically named ones; the gate is indifferent to which, and a test can say so.
    /// </para>
    /// <para>
    /// Everything here is invented: two fictional participants, fictional hashes, and names that name
    /// nothing real.
    /// </para>
    /// </remarks>
    internal sealed class DirectionSequenceGateFixture : IDisposable
    {
        /// <summary>The date most fixtures put their first pair on.</summary>
        internal static readonly DateOnly FirstDate = new(2026, 3, 2);

        /// <summary>The device group every source is assigned to unless a test says otherwise.</summary>
        internal const long DefaultDeviceGroupID = 500;

        private const string DefaultExtension = ".jpg";

        private readonly MatchingTestWorkspace _workspace = new();
        private readonly long _sourceID;

        private int _hashNumber;

        internal DirectionSequenceGateFixture() => _sourceID = _workspace.AddMediaSource();

        internal MatchingTestWorkspace Workspace => _workspace;

        /// <summary>The first acquisition source, which every default call writes to.</summary>
        internal long SourceID => _sourceID;

        /// <summary>A date a fixed number of days after <see cref="FirstDate"/>.</summary>
        internal static DateOnly Day(int offset) => FirstDate.AddDays(offset);

        /// <summary>A recovered name carrying the marker for <paramref name="date"/>.</summary>
        internal static string Name(DateOnly date, string suffix, string prefix = "IMG") =>
            $"{prefix}-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}-WA{suffix}";

        /// <summary>A supported four-digit suffix.</summary>
        internal static string Token(int token) =>
            token.ToString("D4", CultureInfo.InvariantCulture);

        /// <summary>A second acquisition source.</summary>
        internal long AddSource(string displayName = "Second synthetic source") =>
            _workspace.AddMediaSource(displayName);

        /// <summary>A message from the local participant, with its one unresolved attachment.</summary>
        internal long AddOutgoing(DateOnly date, string time = "09:00:00") =>
            _workspace.AddMediaAttachment(
                date, MatchingTestWorkspace.LocalParticipantID, time: time);

        /// <summary>A message from the other participant, with its one unresolved attachment.</summary>
        internal long AddIncoming(DateOnly date, string time = "09:05:00") =>
            _workspace.AddMediaAttachment(
                date, MatchingTestWorkspace.OtherParticipantID, time: time);

        /// <summary>A message with no sender at all, which carries no direction.</summary>
        internal long AddSenderlessAttachment(DateOnly date, string time = "09:30:00") =>
            _workspace.AddMediaAttachment(date, senderParticipantID: null, time: time);

        /// <summary>
        /// One message carrying several unresolved attachments, numbered from ordinal one.
        /// </summary>
        internal long AddMessageWithAttachments(
            DateOnly date, bool outgoing, int attachmentCount, string time = "09:00:00")
        {
            var messageID = _workspace.AddMessage(
                date,
                outgoing
                    ? MatchingTestWorkspace.LocalParticipantID
                    : MatchingTestWorkspace.OtherParticipantID,
                time: time);

            for (var ordinal = 1; ordinal <= attachmentCount; ordinal++)
            {
                _workspace.Execute(
                    $"""
                    INSERT INTO Attachment (MessageID, Ordinal, ResolutionStatus)
                    VALUES ({messageID}, {ordinal}, 'Unresolved');
                    """);
            }

            return messageID;
        }

        /// <summary>One recovered copy carrying a supported four-digit token.</summary>
        internal void AddToken(
            DateOnly date, int token, bool? isSent, long? mediaSourceID = null) =>
            AddNamed(date, Token(token), isSent, mediaSourceID);

        /// <summary>
        /// One recovered copy carrying whatever suffix is given, supported or not.
        /// </summary>
        internal void AddNamed(
            DateOnly date,
            string suffix,
            bool? isSent,
            long? mediaSourceID = null,
            string extension = DefaultExtension,
            string prefix = "IMG")
        {
            var sha = MatchingTestData.Hash(++_hashNumber);
            var mediaAssetID = _workspace.AddMediaAsset(sha);

            _workspace.AddMediaFile(
                mediaSourceID ?? _sourceID,
                mediaAssetID,
                sha,
                date,
                isSent,
                fileName: Name(date, suffix, prefix) + extension,
                extension: extension);
        }

        /// <summary>A copy with no naming-derived date at all.</summary>
        internal void AddUndated(bool? isSent = null, long? mediaSourceID = null)
        {
            var sha = MatchingTestData.Hash(++_hashNumber);

            _workspace.AddAssetWithCopy(
                mediaSourceID ?? _sourceID, sha, fileDate: null, isSent: isSent);
        }

        /// <summary>Runs the gate over this workspace with every source in one device group.</summary>
        internal DirectionSequenceGateCensus Analyse(
            IReadOnlyList<DeviceGroupAssignment>? deviceGroups = null,
            IReadOnlyList<StageATokenCoverageDeclaration>? stageACoverage = null,
            Action<DirectionSequencePairRow>? pairSink = null,
            CancellationToken cancellationToken = default) =>
            new DirectionSequenceGateService().Analyse(
                new DirectionSequenceGateRequest
                {
                    DatabasePath = _workspace.DatabasePath,
                    ConversationID = MatchingTestWorkspace.ConversationID,
                    LocalParticipantID = MatchingTestWorkspace.LocalParticipantID,
                    DeviceGroups = deviceGroups ?? OneGroupForEverySource(),
                    StageATokenCoverage = stageACoverage,
                },
                pairSink,
                cancellationToken);

        /// <summary>Every source in the workspace assigned to one device group.</summary>
        internal List<DeviceGroupAssignment> OneGroupForEverySource(
            long deviceGroupID = DefaultDeviceGroupID) =>
            [
                .. SourceIdentifiers()
                    .Select(
                        mediaSourceID => new DeviceGroupAssignment
                        {
                            MediaSourceID = mediaSourceID,
                            DeviceGroupID = deviceGroupID,
                        }),
            ];

        /// <summary>Every source in its own device group.</summary>
        internal List<DeviceGroupAssignment> OneGroupPerSource() =>
            [
                .. SourceIdentifiers()
                    .Select(
                        mediaSourceID => new DeviceGroupAssignment
                        {
                            MediaSourceID = mediaSourceID,
                            DeviceGroupID = DefaultDeviceGroupID + mediaSourceID,
                        }),
            ];

        /// <summary>The identifiers of every source the fixture has registered.</summary>
        internal List<long> SourceIdentifiers()
        {
            var identifiers = new List<long>();
            // Read through a fresh connection, so a test that has closed the building one to hash or
            // copy the file can still describe the grouping it wants.
            var highest = _workspace.ScalarLongReadOnly(
                "SELECT COALESCE(MAX(MediaSourceID), 0) FROM MediaSource;");

            for (var mediaSourceID = 1L; mediaSourceID <= highest; mediaSourceID++)
            {
                identifiers.Add(mediaSourceID);
            }

            return identifiers;
        }

        /// <summary>One scope's census, by scope level.</summary>
        internal static DirectionSequenceScopeCensus ScopeOf(
            DirectionSequenceGateCensus census, ScopeLevel scope) =>
            census.Scopes.Single(measured => measured.Scope == scope);

        public void Dispose() => _workspace.Dispose();
    }
}
