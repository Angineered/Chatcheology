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
            Action<DirectionSequencePairSnapshot>? snapshotSink = null,
            CancellationToken cancellationToken = default) =>
            new DirectionSequenceGateService().Analyse(
                Request(deviceGroups, stageACoverage),
                pairSink,
                snapshotSink,
                cancellationToken);

        /// <summary>The gate request this fixture describes, for a caller building on it.</summary>
        internal DirectionSequenceGateRequest Request(
            IReadOnlyList<DeviceGroupAssignment>? deviceGroups = null,
            IReadOnlyList<StageATokenCoverageDeclaration>? stageACoverage = null) =>
            new()
            {
                DatabasePath = _workspace.DatabasePath,
                ConversationID = MatchingTestWorkspace.ConversationID,
                LocalParticipantID = MatchingTestWorkspace.LocalParticipantID,
                DeviceGroups = deviceGroups ?? OneGroupForEverySource(),
                StageATokenCoverage = stageACoverage,
            };

        /// <summary>Every pair snapshot the gate offers, in the order it offered them.</summary>
        internal List<DirectionSequencePairSnapshot> Snapshots(
            IReadOnlyList<DeviceGroupAssignment>? deviceGroups = null)
        {
            var snapshots = new List<DirectionSequencePairSnapshot>();

            Analyse(deviceGroups, snapshotSink: snapshots.Add);

            return snapshots;
        }

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

        // ---------------------------------------------------------------------------------------
        // Stage B2C-1.
        // ---------------------------------------------------------------------------------------

        /// <summary>
        /// The Stage A coverage this workspace's own gate recount would produce.
        /// </summary>
        /// <remarks>
        /// Derived from a first census, because a synthetic fixture has no preserved Stage A report to
        /// carry forward. That is exactly what makes the alignment tests meaningful: the production code
        /// has no archive figure compiled into it, so every target a test reconciles is one the test
        /// supplied.
        /// </remarks>
        internal IReadOnlyList<StageATokenCoverageDeclaration> StageACoverage()
        {
            var census = Analyse();

            return
            [
                .. census.Sources.Select(
                    source => new StageATokenCoverageDeclaration
                    {
                        MediaSourceID = source.MediaSourceID,
                        PhysicalObservationCount = source.PhysicalObservationCount,
                        SupportedTokenObservationCount = source.SupportedTokenObservationCount,
                    }),
            ];
        }

        /// <summary>The Stage B1 declaration this workspace's own census reconciles against.</summary>
        internal static DirectionSequenceStageB1Declaration StageB1(
            DirectionSequenceGateCensus census, IReadOnlyList<DeviceGroupAssignment> deviceGroups) =>
            new()
            {
                SupportedTokenIsMonotoneWithinLocalDate = true,
                SupportedTokenDigitWidth = 4,
                DistinctLogicalPositionCount =
                    census.CrossSourceOverlap.DistinctLogicalPositionCount,

                // Carried forward, not recomputed: a synthetic fixture has no Stage B1 census, and the
                // gate does not track which assets share a key. Zero is the honest declaration for a
                // fixture that builds one asset per file.
                PositionsWithMoreThanOneAssetCount = 0,
                DeviceGroups = deviceGroups,
                DirectionBlindMediaSourceIDs =
                [
                    .. census.Sources
                        .Where(source => !source.RecordsAnyDirection)
                        .Select(source => source.MediaSourceID)
                        .Order(),
                ],
            };

        /// <summary>The frozen declaration this workspace's own census reconciles against.</summary>
        internal static DirectionSequenceFrozenGateDeclaration FrozenGate(
            DirectionSequenceGateCensus census,
            ScopeLevel primaryScope = ScopeLevel.DeviceGroupDate)
        {
            var primary = ScopeOf(census, primaryScope);
            var messages = census.MessagePopulation;

            return new DirectionSequenceFrozenGateDeclaration
            {
                PrimaryScope = primaryScope,
                Scopes =
                [
                    .. census.Scopes.Select(
                        scope => new DirectionSequenceFrozenScopeTarget
                        {
                            Scope = scope.Scope,
                            PairsWithMessageSymbols = scope.PairPopulation.PairsWithMessageSymbols,
                            ExcludedByDirectionConflictPairCount =
                                scope.PairPopulation.ExcludedByDirectionConflictPairCount,
                            SupplyAssessedPairCount = scope.Supply.AfterCollapse.Population,
                        }),
                ],
                ConsideredAttachmentCount = messages.ConsideredAttachmentCount,
                DistinctAttachmentDateCount = messages.DistinctAttachmentDateCount,
                OutgoingAttachmentCount = messages.OutgoingAttachmentCount,
                IncomingAttachmentCount = messages.IncomingAttachmentCount,
                MultiAttachmentMessageCount = messages.MultiAttachmentMessageCount,
                MaximumAttachmentsOnOneMessage = messages.MaximumAttachmentsOnOneMessage,
                SupplyInsufficientPairCountAfterCollapse =
                    primary.Supply.AfterCollapse.SupplyInsufficientPairCount,
                SupplySufficientPairCountAfterCollapse =
                    primary.Supply.AfterCollapse.SupplySufficientPairCount,
                PairsBecomingSufficientAfterCollapse =
                    primary.Supply.PairsBecomingSufficientAfterCollapse,
                DegeneratePairCount = primary.PairPopulation.Degenerate.DegeneratePairCount,
                NoTokenPositionPairCount =
                    primary.PairPopulation.Degenerate.NoTokenPositionPairCount,
                ClassifiedPairCount = primary.PairPopulation.ClassifiedPairCount,
                ClassifiedMessageObservationCount =
                    primary.PairPopulation.MessageObservationsClassified,
                BinaryDeterminatePairCount = primary.Determinacy.BinaryDeterminatePairCount,
                BinaryInformativePairCount = primary.Determinacy.BinaryInformativePairCount,
                GradedDeterminatePairCount = primary.Determinacy.GradedDeterminatePairCount,
                GradedInformativePairCount = primary.Determinacy.GradedInformativePairCount,
                BinaryInformativeAndGradedInformative =
                    primary.Determinacy.BinaryInformativeAndGradedInformative,
                BinaryDeterminateAndGradedInformative =
                    primary.Determinacy.BinaryDeterminateAndGradedInformative,
                BinaryInformativeAndGradedDeterminate =
                    primary.Determinacy.BinaryInformativeAndGradedDeterminate,
                BinaryDeterminateAndGradedDeterminate =
                    primary.Determinacy.BinaryDeterminateAndGradedDeterminate,
                ExactlyOneQrBandPairCount = BandCount(primary, DirectionSequenceQrBand.ExactlyOne),
                ExactlyZeroQrBandPairCount = BandCount(primary, DirectionSequenceQrBand.ExactlyZero),
                SumOfConditionalAdmissionProbability =
                    Render(primary.Reference.SumOfConditionalAdmissionProbability),
                SumOfConditionalAdmissionProbabilityOverInformative =
                    Render(primary.Reference.SumOfConditionalAdmissionProbabilityOverInformative),
                SumOfExpectedEmbeddingShare =
                    Render(primary.Reference.SumOfExpectedEmbeddingShare),
                SumOfExpectedEmbeddingShareOverInformative =
                    Render(primary.Reference.SumOfExpectedEmbeddingShareOverInformative),
            };
        }

        /// <summary>The alignment request for this workspace, reconciling against its own census.</summary>
        internal DirectionSequenceAlignmentRequest AlignmentRequest(
            IReadOnlyList<DeviceGroupAssignment>? deviceGroups = null,
            DirectionSequenceFrozenGateDeclaration? frozenGate = null,
            DirectionSequenceStageB1Declaration? stageB1 = null)
        {
            var groups = deviceGroups ?? OneGroupForEverySource();
            var coverage = StageACoverage();
            var census = Analyse(groups, coverage);

            return new DirectionSequenceAlignmentRequest
            {
                DatabasePath = _workspace.DatabasePath,
                ConversationID = MatchingTestWorkspace.ConversationID,
                LocalParticipantID = MatchingTestWorkspace.LocalParticipantID,
                DeviceGroups = groups,
                StageATokenCoverage = coverage,
                StageB1 = stageB1 ?? StageB1(census, groups),
                FrozenGate = frozenGate ?? FrozenGate(census),
            };
        }

        /// <summary>Runs the alignment stage over this workspace.</summary>
        internal DirectionSequenceAlignmentCensus AnalyseAlignment(
            IReadOnlyList<DeviceGroupAssignment>? deviceGroups = null,
            DirectionSequenceFrozenGateDeclaration? frozenGate = null,
            DirectionSequenceStageB1Declaration? stageB1 = null,
            CancellationToken cancellationToken = default) =>
            new DirectionSequenceAlignmentService().Analyse(
                AlignmentRequest(deviceGroups, frozenGate, stageB1),
                progress: null,
                cancellationToken);

        private static int BandCount(
            DirectionSequenceScopeCensus scope, DirectionSequenceQrBand band) =>
            scope.Reference.BandRows.Single(row => row.Band == band).PairCount;

        private static string Render(double reading) =>
            reading.ToString("F6", CultureInfo.InvariantCulture);

        public void Dispose() => _workspace.Dispose();
    }
}
