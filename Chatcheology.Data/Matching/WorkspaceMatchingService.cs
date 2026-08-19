using Chatcheology.Data.Media;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// Analyses a conversation's unresolved attachments against the deduplicated media archive and
    /// reports what evidence exists for each of them.
    /// </summary>
    /// <remarks>
    /// Read-only, in the sense the database itself enforces: the connection is opened
    /// <c>Mode=ReadOnly</c>, so a stray write fails rather than succeeding quietly. Nothing here
    /// resolves an attachment, persists a candidate, or writes matching state of any kind. Phase 6
    /// produces evidence; deciding what to do with it belongs to a later phase and a schema that
    /// does not exist yet.
    /// <para>
    /// The rule the whole design serves is that missing is better than wrong. Candidates are offered
    /// at asset level, ordered by identifier rather than by any notion of likelihood, and carry no
    /// score, rank or confidence label — because a plausible wrong match, once accepted, is
    /// indistinguishable afterwards from a real one.
    /// </para>
    /// <para>
    /// No media file is opened, no hash is recomputed and no path or file name takes part in the
    /// evidence. Everything comes from what the workspace already committed.
    /// </para>
    /// </remarks>
    public sealed class WorkspaceMatchingService
    {
        /// <summary>
        /// Analyses every unresolved attachment in one conversation.
        /// </summary>
        /// <param name="databasePath">
        /// An existing workspace at schema version <see cref="WorkspaceDatabase.SchemaVersion"/>.
        /// It is opened read-only, never created and never migrated.
        /// </param>
        /// <param name="request">Which conversation, and who the local participant is.</param>
        /// <param name="attachmentSink">
        /// Receives each attachment's analysis as it is produced, in deterministic attachment order,
        /// or <see langword="null"/> when only the census is wanted.
        /// <para>
        /// Streamed rather than returned as a whole, because a real conversation's attachments and
        /// their candidates together are hundreds of thousands of relationships. The census carries
        /// only aggregates, so what this method returns stays the same size whatever the archive
        /// holds. An exception thrown by the sink propagates and stops the analysis.
        /// </para>
        /// </param>
        /// <returns>The aggregate census. Nothing is truncated to produce it.</returns>
        /// <exception cref="FileNotFoundException">
        /// There is no workspace at <paramref name="databasePath"/>. No file is created.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version, the conversation does not exist, the
        /// supplied local participant does not belong to it, media hashing is incomplete, or a
        /// stored value cannot be read under the format the workspace writes. Nothing is repaired.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was signalled. No census is returned, and whatever
        /// the sink already received is a partial prefix that must not be read as a complete
        /// analysis.
        /// </exception>
        public MatchAnalysisCensus Analyse(
            string databasePath,
            MatchAnalysisRequest request,
            Action<AttachmentMatchAnalysis>? attachmentSink = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
            ArgumentNullException.ThrowIfNull(request);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(databasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(connection, "a matching analysis");

            RequireConversation(connection, request.ConversationID);
            RequireLocalParticipantInConversation(connection, request);

            var media = MediaEvidenceIndex.Read(connection, cancellationToken);

            var census = new CensusAccumulator(
                request,
                CountWorkspaceUnresolvedAttachments(connection),
                CountDistinctConversationMessageDates(
                    connection, request.ConversationID, cancellationToken));

            AnalyseAttachments(connection, request, media, census, attachmentSink, cancellationToken);

            return census.Build(media);
        }

        /// <summary>
        /// Walks the conversation's unresolved attachments in order, analysing each one.
        /// </summary>
        /// <remarks>
        /// The order is stated in SQL rather than left to the engine, so the same workspace produces
        /// the same sequence every time: message order first, then position within the message, then
        /// the attachment's own identifier as a final tie-break that cannot itself tie.
        /// </remarks>
        private static void AnalyseAttachments(
            SqliteConnection connection,
            MatchAnalysisRequest request,
            MediaEvidenceIndex media,
            CensusAccumulator census,
            Action<AttachmentMatchAnalysis>? attachmentSink,
            CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    a.AttachmentID,
                    a.MessageID,
                    a.Ordinal,
                    m.SequenceNumber,
                    m.MessageDateTimeLocal,
                    m.SenderParticipantID
                FROM Attachment AS a
                INNER JOIN Message AS m ON m.MessageID = a.MessageID
                WHERE a.ResolutionStatus = $unresolved
                  AND m.ConversationID = $conversationID
                ORDER BY m.SequenceNumber ASC, a.Ordinal ASC, a.AttachmentID ASC;
                """;

            command.Parameters.AddWithValue(
                "$unresolved", WorkspaceDatabase.UnresolvedAttachmentStatus);

            command.Parameters.AddWithValue("$conversationID", request.ConversationID);

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var analysis = AnalyseAttachment(reader, request, media);

                census.Add(analysis, media);

                // Checked again immediately before the sink, so a cancelled run cannot hand out one
                // more attachment on its way through the door.
                cancellationToken.ThrowIfCancellationRequested();

                attachmentSink?.Invoke(analysis);
            }
        }

        private static AttachmentMatchAnalysis AnalyseAttachment(
            SqliteDataReader reader, MatchAnalysisRequest request, MediaEvidenceIndex media)
        {
            var attachmentID = reader.GetInt64(0);
            var messageID = reader.GetInt64(1);
            var ordinal = reader.GetInt32(2);
            var sequenceNumber = reader.GetInt32(3);
            var messageDateTimeLocal = ReadMessageDateTimeLocal(reader, messageID);
            var senderParticipantID = reader.IsDBNull(5) ? null : (long?)reader.GetInt64(5);

            var messageDate = DateOnly.FromDateTime(messageDateTimeLocal);
            var direction = ResolveDirection(request.LocalParticipantID, senderParticipantID);

            var exactDateCandidates = BuildExactDateCandidates(media, messageDate, direction);

            var adjacentDateCandidates =
                BuildAdjacentDateCandidates(media, messageDate, direction, exactDateCandidates);

            return new AttachmentMatchAnalysis
            {
                AttachmentID = attachmentID,
                MessageID = messageID,
                Ordinal = ordinal,
                MessageSequenceNumber = sequenceNumber,
                MessageDateTimeLocal = messageDateTimeLocal,
                MessageDate = messageDate,
                SenderParticipantID = senderParticipantID,
                MessageDirection = direction,
                ExactDateCandidates = exactDateCandidates,
                AdjacentDateCandidates = adjacentDateCandidates,
                ExactDateDirectionCompatibleCandidateCount = exactDateCandidates.Count(
                    candidate =>
                        candidate.DirectionCompatibility == DirectionCompatibility.Compatible),
            };
        }

        /// <summary>
        /// The eligible assets holding a copy dated to the message's own date.
        /// </summary>
        /// <remarks>
        /// The primary candidate set, and the only one the census reports direction figures for.
        /// Each source that supplied a supporting copy is credited once per relationship, which is
        /// what makes the per-source contribution a count of evidence rather than of files.
        /// </remarks>
        private static List<AttachmentMatchCandidate> BuildExactDateCandidates(
            MediaEvidenceIndex media, DateOnly messageDate, MessageDirection direction)
        {
            var candidates = new List<AttachmentMatchCandidate>();

            foreach (var mediaAssetID in media.EligibleAssetsOn(messageDate))
            {
                var supporting = media.CopiesOn(mediaAssetID, messageDate)!;

                foreach (var mediaSourceID in supporting.Sources)
                {
                    media.CountExactCandidateRelationContribution(mediaSourceID);
                }

                candidates.Add(
                    CreateCandidate(media, mediaAssetID, messageDate, [supporting], direction));
            }

            return candidates;
        }

        /// <summary>
        /// The eligible assets with no copy on the message's date but one on the day either side.
        /// </summary>
        /// <remarks>
        /// Kept apart from the exact-date set and never promoted into it, however empty that set is.
        /// An asset dated a day out is weaker evidence, and merging the two would make the weaker
        /// kind unrecoverable afterwards.
        /// <para>
        /// Where an asset has copies on both adjacent days, both days support the relationship: each
        /// is a qualifying reason the asset is here at all.
        /// </para>
        /// </remarks>
        private static List<AttachmentMatchCandidate> BuildAdjacentDateCandidates(
            MediaEvidenceIndex media,
            DateOnly messageDate,
            MessageDirection direction,
            List<AttachmentMatchCandidate> exactDateCandidates)
        {
            var exactDateAssets = exactDateCandidates.Select(candidate => candidate.MediaAssetID)
                .ToHashSet();

            var adjacentAssets = new SortedSet<long>();

            foreach (var adjacentDate in AdjacentDates(messageDate))
            {
                foreach (var mediaAssetID in media.EligibleAssetsOn(adjacentDate))
                {
                    if (!exactDateAssets.Contains(mediaAssetID))
                    {
                        adjacentAssets.Add(mediaAssetID);
                    }
                }
            }

            var candidates = new List<AttachmentMatchCandidate>(adjacentAssets.Count);

            foreach (var mediaAssetID in adjacentAssets)
            {
                var supporting = AdjacentDates(messageDate)
                    .Select(adjacentDate => media.CopiesOn(mediaAssetID, adjacentDate))
                    .OfType<MediaEvidenceIndex.SupportingCopies>()
                    .ToArray();

                candidates.Add(
                    CreateCandidate(media, mediaAssetID, messageDate, supporting, direction));
            }

            return candidates;
        }

        /// <summary>
        /// The day before and the day after <paramref name="messageDate"/>, omitting either when the
        /// calendar has no such day.
        /// </summary>
        private static IEnumerable<DateOnly> AdjacentDates(DateOnly messageDate)
        {
            if (messageDate > DateOnly.MinValue)
            {
                yield return messageDate.AddDays(-1);
            }

            if (messageDate < DateOnly.MaxValue)
            {
                yield return messageDate.AddDays(1);
            }
        }

        /// <summary>
        /// Assembles one candidate from the asset's own facts and the copies supporting this
        /// relationship.
        /// </summary>
        /// <remarks>
        /// The two kinds of fact are kept visibly apart. <c>PhysicalCopyCount</c> and
        /// <c>DistinctMediaSourceCount</c> describe the payload wherever it survives;
        /// every <c>Supporting</c> field and the direction verdict describe only the copies whose
        /// date placed the asset in this candidate set.
        /// <para>
        /// The three date flags are the exception, and deliberately so: they say how this asset's
        /// dated copies sit around this message's date, including a copy on a neighbouring day that
        /// is not itself supporting evidence here. They are date facts, and none of them feeds the
        /// direction verdict.
        /// </para>
        /// </remarks>
        private static AttachmentMatchCandidate CreateCandidate(
            MediaEvidenceIndex media,
            long mediaAssetID,
            DateOnly messageDate,
            IReadOnlyList<MediaEvidenceIndex.SupportingCopies> supporting,
            MessageDirection direction)
        {
            var asset = media.Asset(mediaAssetID);

            var supportingCopyCount = 0;
            var hasSentFolderCopy = false;
            var hasNotUnderSentFolderCopy = false;
            var hasDirectionUnknownCopy = false;

            foreach (var copies in supporting)
            {
                supportingCopyCount += copies.CopyCount;
                hasSentFolderCopy |= copies.HasSentFolderCopy;
                hasNotUnderSentFolderCopy |= copies.HasNotUnderSentFolderCopy;
                hasDirectionUnknownCopy |= copies.HasDirectionUnknownCopy;
            }

            return new AttachmentMatchCandidate
            {
                MediaAssetID = mediaAssetID,
                MediaType = asset.MediaType,
                SizeBytes = asset.SizeBytes,
                PhysicalCopyCount = asset.PhysicalCopyCount,
                DistinctMediaSourceCount = asset.Sources.Count,
                HasExactMessageDateCopy = media.CopiesOn(mediaAssetID, messageDate) is not null,
                HasPreviousDayCopy = HasCopyOnAdjacentDay(media, mediaAssetID, messageDate, -1),
                HasNextDayCopy = HasCopyOnAdjacentDay(media, mediaAssetID, messageDate, 1),
                SupportingPhysicalCopyCount = supportingCopyCount,
                SupportingMediaSourceCount = CountSupportingSources(supporting),
                HasSupportingSentFolderCopy = hasSentFolderCopy,
                HasSupportingNotUnderSentFolderCopy = hasNotUnderSentFolderCopy,
                HasSupportingDirectionUnknownCopy = hasDirectionUnknownCopy,
                DirectionCompatibility = ResolveCompatibility(
                    direction, hasSentFolderCopy, hasNotUnderSentFolderCopy),
            };
        }

        private static bool HasCopyOnAdjacentDay(
            MediaEvidenceIndex media, long mediaAssetID, DateOnly messageDate, int offset)
        {
            if (offset < 0 && messageDate == DateOnly.MinValue)
            {
                return false;
            }

            if (offset > 0 && messageDate == DateOnly.MaxValue)
            {
                return false;
            }

            return media.CopiesOn(mediaAssetID, messageDate.AddDays(offset)) is not null;
        }

        /// <remarks>
        /// The single-day case is the overwhelming majority and needs no set at all; only an asset
        /// supported on both adjacent days has to have its sources unioned.
        /// </remarks>
        private static int CountSupportingSources(
            IReadOnlyList<MediaEvidenceIndex.SupportingCopies> supporting)
        {
            if (supporting.Count == 1)
            {
                return supporting[0].Sources.Count;
            }

            var sources = new HashSet<long>();

            foreach (var copies in supporting)
            {
                sources.UnionWith(copies.Sources);
            }

            return sources.Count;
        }

        /// <summary>
        /// Which way a message travelled, given who the caller said the local participant is.
        /// </summary>
        /// <remarks>
        /// Never inferred. Without an explicitly supplied local participant every message is
        /// <see cref="MessageDirection.Unknown"/>, and a message with no sender at all — a system
        /// notice — stays unknown even when one was supplied.
        /// </remarks>
        private static MessageDirection ResolveDirection(
            long? localParticipantID, long? senderParticipantID)
        {
            if (localParticipantID is not { } local || senderParticipantID is not { } sender)
            {
                return MessageDirection.Unknown;
            }

            return sender == local ? MessageDirection.Outgoing : MessageDirection.Incoming;
        }

        /// <summary>
        /// How the supporting copies' folder evidence sits against the message's direction.
        /// </summary>
        /// <remarks>
        /// For an outgoing message a copy beneath <c>Sent</c> agrees and a copy known not to be
        /// beneath one disagrees; for an incoming message the reading is inverted. Copies recording
        /// no direction at all — a recovered source with no <c>Sent</c> structure — agree with
        /// nothing and contradict nothing, so an asset supported only by those is
        /// <see cref="DirectionCompatibility.Unknown"/> rather than compatible or contradictory.
        /// </remarks>
        private static DirectionCompatibility ResolveCompatibility(
            MessageDirection direction, bool hasSentFolderCopy, bool hasNotUnderSentFolderCopy)
        {
            if (direction == MessageDirection.Unknown)
            {
                return DirectionCompatibility.Unknown;
            }

            var outgoing = direction == MessageDirection.Outgoing;

            var agrees = outgoing ? hasSentFolderCopy : hasNotUnderSentFolderCopy;
            var disagrees = outgoing ? hasNotUnderSentFolderCopy : hasSentFolderCopy;

            return (agrees, disagrees) switch
            {
                (true, false) => DirectionCompatibility.Compatible,
                (true, true) => DirectionCompatibility.Mixed,
                (false, true) => DirectionCompatibility.ContradictoryOnly,
                _ => DirectionCompatibility.Unknown,
            };
        }

        private static DateTime ReadMessageDateTimeLocal(SqliteDataReader reader, long messageID)
        {
            var stored = reader.GetString(4);

            if (WorkspaceDateFormats.TryParseMessageDateTimeLocal(stored, out var messageDateTime))
            {
                return messageDateTime;
            }

            throw new InvalidOperationException(
                $"Message {messageID} has a MessageDateTimeLocal that is not the local wall-clock " +
                $"format this workspace writes. It is not reinterpreted under another format, " +
                $"because a timestamp read the wrong way produces candidates for the wrong day " +
                $"without ever looking wrong. Nothing has been analysed and the workspace is " +
                $"unchanged.");
        }

        private static void RequireConversation(SqliteConnection connection, long conversationID)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM Conversation WHERE ConversationID = $conversationID;";

            command.Parameters.AddWithValue("$conversationID", conversationID);

            if (Convert.ToInt64(command.ExecuteScalar()) == 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"The workspace has no conversation {conversationID}. An analysis of a conversation " +
                $"that is not there would report an empty census, which reads as a conversation " +
                $"with no attachments rather than as a conversation that does not exist.");
        }

        /// <summary>
        /// Proves a supplied local participant really belongs to the conversation being analysed.
        /// </summary>
        /// <remarks>
        /// A participant from another conversation would still produce direction for every message —
        /// all of it wrong, and all of it looking exactly like the right answer, because the sender
        /// would never match and every message would read as incoming.
        /// </remarks>
        private static void RequireLocalParticipantInConversation(
            SqliteConnection connection, MatchAnalysisRequest request)
        {
            if (request.LocalParticipantID is not { } localParticipantID)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM ConversationParticipant
                WHERE ConversationID = $conversationID
                  AND ParticipantID = $participantID;
                """;

            command.Parameters.AddWithValue("$conversationID", request.ConversationID);
            command.Parameters.AddWithValue("$participantID", localParticipantID);

            if (Convert.ToInt64(command.ExecuteScalar()) == 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Participant {localParticipantID} does not belong to conversation " +
                $"{request.ConversationID}, so it cannot be that conversation's local user. " +
                $"Direction evidence is only as good as this claim, and a wrong local participant " +
                $"would make every message read as incoming. Supply a participant of this " +
                $"conversation, or none at all.");
        }

        private static int CountWorkspaceUnresolvedAttachments(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = $unresolved;";

            command.Parameters.AddWithValue(
                "$unresolved", WorkspaceDatabase.UnresolvedAttachmentStatus);

            return Convert.ToInt32(command.ExecuteScalar());
        }

        /// <remarks>
        /// The dates are parsed rather than counted with a text function in SQL, so every message in
        /// the conversation is read under the one sanctioned format and a malformed timestamp is
        /// found here instead of quietly becoming a distinct "date" of its own.
        /// </remarks>
        private static int CountDistinctConversationMessageDates(
            SqliteConnection connection, long conversationID, CancellationToken cancellationToken)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT MessageID, MessageDateTimeLocal
                FROM Message
                WHERE ConversationID = $conversationID;
                """;

            command.Parameters.AddWithValue("$conversationID", conversationID);

            var dates = new HashSet<DateOnly>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var stored = reader.GetString(1);

                if (!WorkspaceDateFormats.TryParseMessageDateTimeLocal(stored, out var messageDateTime))
                {
                    throw new InvalidOperationException(
                        $"Message {reader.GetInt64(0)} has a MessageDateTimeLocal that is not the " +
                        $"local wall-clock format this workspace writes. Nothing has been analysed " +
                        $"and the workspace is unchanged.");
                }

                dates.Add(DateOnly.FromDateTime(messageDateTime));
            }

            return dates.Count;
        }

        /// <summary>
        /// Accumulates the census while the attachments stream past.
        /// </summary>
        /// <remarks>
        /// Everything here is a counter or a set of asset identifiers, so what it holds is bounded
        /// by the archive rather than by the number of relationships the analysis produces. No
        /// attachment and no candidate is retained.
        /// </remarks>
        private sealed class CensusAccumulator
        {
            private readonly MatchAnalysisRequest _request;
            private readonly int _workspaceUnresolvedAttachmentCount;
            private readonly int _distinctConversationMessageDates;

            private readonly int[] _exactDateCandidateCounts = new int[BandCount];
            private readonly int[] _exactDateCompatibleCandidateCounts = new int[BandCount];

            private readonly HashSet<DateOnly> _attachmentMessageDates = [];
            private readonly HashSet<long> _candidateMediaAssets = [];
            private readonly Dictionary<MediaType, int> _relationsByMediaType = [];

            private int _conversationUnresolvedAttachmentCount;
            private int _attachmentsOnMessagesWithNullSender;
            private int _attachmentCountOnDatesWithNoDatedEligibleMedia;
            private int _attachmentsWithExactDateCandidates;
            private int _uniqueExactDateCandidateCount;
            private int _uniqueExactDateAndDirectionCompatibleCandidateCount;
            private int _adjacentDateOnlyAttachmentCount;

            private int _attachmentsWithCompatibleExactCandidate;
            private int _attachmentsWithMixedExactCandidate;
            private int _attachmentsWithUnknownDirectionExactCandidate;
            private int _attachmentsWithContradictoryOnlyExactCandidate;

            private int _relationsCompatible;
            private int _relationsMixed;
            private int _relationsUnknown;
            private int _relationsContradictoryOnly;

            private const int BandCount = 8;

            internal CensusAccumulator(
                MatchAnalysisRequest request,
                int workspaceUnresolvedAttachmentCount,
                int distinctConversationMessageDates)
            {
                _request = request;
                _workspaceUnresolvedAttachmentCount = workspaceUnresolvedAttachmentCount;
                _distinctConversationMessageDates = distinctConversationMessageDates;
            }

            internal void Add(AttachmentMatchAnalysis analysis, MediaEvidenceIndex media)
            {
                _conversationUnresolvedAttachmentCount++;
                _attachmentMessageDates.Add(analysis.MessageDate);

                if (analysis.SenderParticipantID is null)
                {
                    _attachmentsOnMessagesWithNullSender++;
                }

                if (media.EligibleAssetsOn(analysis.MessageDate).Count == 0)
                {
                    _attachmentCountOnDatesWithNoDatedEligibleMedia++;
                }

                AddExactDateEvidence(analysis);

                foreach (var candidate in analysis.AdjacentDateCandidates)
                {
                    _candidateMediaAssets.Add(candidate.MediaAssetID);
                }

                if (analysis.ExactDateCandidateCount == 0 && analysis.HasAdjacentDateCandidates)
                {
                    _adjacentDateOnlyAttachmentCount++;
                }
            }

            /// <remarks>
            /// The four attachment-level direction counts are not mutually exclusive: one
            /// attachment's exact-date candidates can occupy several states at once, and it is
            /// counted in each state it reaches rather than in whichever state might be called the
            /// strongest.
            /// </remarks>
            private void AddExactDateEvidence(AttachmentMatchAnalysis analysis)
            {
                Band(_exactDateCandidateCounts, analysis.ExactDateCandidateCount);

                Band(
                    _exactDateCompatibleCandidateCounts,
                    analysis.ExactDateDirectionCompatibleCandidateCount);

                if (analysis.ExactDateCandidateCount > 0)
                {
                    _attachmentsWithExactDateCandidates++;
                }

                if (analysis.HasUniqueExactDateCandidate)
                {
                    _uniqueExactDateCandidateCount++;
                }

                if (analysis.HasUniqueExactDateDirectionCompatibleCandidate)
                {
                    _uniqueExactDateAndDirectionCompatibleCandidateCount++;
                }

                var compatible = false;
                var mixed = false;
                var unknown = false;
                var contradictoryOnly = false;

                foreach (var candidate in analysis.ExactDateCandidates)
                {
                    _candidateMediaAssets.Add(candidate.MediaAssetID);

                    _relationsByMediaType[candidate.MediaType] =
                        _relationsByMediaType.GetValueOrDefault(candidate.MediaType) + 1;

                    switch (candidate.DirectionCompatibility)
                    {
                        case DirectionCompatibility.Compatible:
                            _relationsCompatible++;
                            compatible = true;
                            break;

                        case DirectionCompatibility.Mixed:
                            _relationsMixed++;
                            mixed = true;
                            break;

                        case DirectionCompatibility.ContradictoryOnly:
                            _relationsContradictoryOnly++;
                            contradictoryOnly = true;
                            break;

                        default:
                            _relationsUnknown++;
                            unknown = true;
                            break;
                    }
                }

                if (compatible)
                {
                    _attachmentsWithCompatibleExactCandidate++;
                }

                if (mixed)
                {
                    _attachmentsWithMixedExactCandidate++;
                }

                if (unknown)
                {
                    _attachmentsWithUnknownDirectionExactCandidate++;
                }

                if (contradictoryOnly)
                {
                    _attachmentsWithContradictoryOnlyExactCandidate++;
                }
            }

            /// <summary>
            /// Files one attachment's candidate count into its band.
            /// </summary>
            private static void Band(int[] bands, int candidateCount)
            {
                var band = candidateCount switch
                {
                    0 => 0,
                    1 => 1,
                    2 => 2,
                    <= 5 => 3,
                    <= 10 => 4,
                    <= 25 => 5,
                    <= 50 => 6,
                    _ => 7,
                };

                bands[band]++;
            }

            private static CandidateCountDistribution Distribution(int[] bands) => new()
            {
                Zero = bands[0],
                One = bands[1],
                Two = bands[2],
                ThreeToFive = bands[3],
                SixToTen = bands[4],
                ElevenToTwentyFive = bands[5],
                TwentySixToFifty = bands[6],
                MoreThanFifty = bands[7],
            };

            internal MatchAnalysisCensus Build(MediaEvidenceIndex media) => new()
            {
                ConversationID = _request.ConversationID,
                LocalParticipantIDSupplied = _request.LocalParticipantID is not null,

                ConversationUnresolvedAttachmentCount = _conversationUnresolvedAttachmentCount,
                WorkspaceUnresolvedAttachmentCount = _workspaceUnresolvedAttachmentCount,
                UnresolvedAttachmentsOutsideAnalysedConversation =
                    _workspaceUnresolvedAttachmentCount - _conversationUnresolvedAttachmentCount,

                AttachmentsOnMessagesWithNullSender = _attachmentsOnMessagesWithNullSender,

                MediaFileWithFileDateCount = media.MediaFileWithFileDateCount,
                MediaFileWithNullFileDateCount = media.MediaFileWithNullFileDateCount,

                DistinctConversationMessageDates = _distinctConversationMessageDates,
                DistinctAttachmentMessageDates = _attachmentMessageDates.Count,
                AttachmentCountOnDatesWithNoDatedEligibleMedia =
                    _attachmentCountOnDatesWithNoDatedEligibleMedia,

                AttachmentsWithExactDateCandidates = _attachmentsWithExactDateCandidates,
                AttachmentsWithNoExactDateCandidates =
                    _conversationUnresolvedAttachmentCount - _attachmentsWithExactDateCandidates,
                ExactDateCandidateCountDistribution = Distribution(_exactDateCandidateCounts),
                UniqueExactDateCandidateCount = _uniqueExactDateCandidateCount,

                ExactDateCompatibleCandidateCountDistribution =
                    Distribution(_exactDateCompatibleCandidateCounts),
                UniqueExactDateAndDirectionCompatibleCandidateCount =
                    _uniqueExactDateAndDirectionCompatibleCandidateCount,

                AdjacentDateOnlyAttachmentCount = _adjacentDateOnlyAttachmentCount,
                DistinctCandidateMediaAssetsOverall = _candidateMediaAssets.Count,
                NoDateEvidenceAssetPoolCount = media.NoDateEvidenceAssetPoolCount,

                ZeroByteAssetsExcluded = media.ZeroByteAssetsExcluded,
                ZeroBytePhysicalFilesRepresentedByExcludedAsset =
                    media.ZeroBytePhysicalFilesRepresentedByExcludedAsset,

                AttachmentsWithAtLeastOneCompatibleExactCandidate =
                    _attachmentsWithCompatibleExactCandidate,
                AttachmentsWithAtLeastOneMixedExactCandidate = _attachmentsWithMixedExactCandidate,
                AttachmentsWithAtLeastOneUnknownDirectionExactCandidate =
                    _attachmentsWithUnknownDirectionExactCandidate,
                AttachmentsWithAtLeastOneContradictoryOnlyExactCandidate =
                    _attachmentsWithContradictoryOnlyExactCandidate,

                ExactCandidateRelationsCompatible = _relationsCompatible,
                ExactCandidateRelationsMixed = _relationsMixed,
                ExactCandidateRelationsUnknown = _relationsUnknown,
                ExactCandidateRelationsContradictoryOnly = _relationsContradictoryOnly,

                ExactCandidateRelationsByMediaType = new MediaTypeRelationCounts
                {
                    Image = _relationsByMediaType.GetValueOrDefault(MediaType.Image),
                    Video = _relationsByMediaType.GetValueOrDefault(MediaType.Video),
                    Audio = _relationsByMediaType.GetValueOrDefault(MediaType.Audio),
                    Document = _relationsByMediaType.GetValueOrDefault(MediaType.Document),
                    Unknown = _relationsByMediaType.GetValueOrDefault(MediaType.Unknown),
                },

                MediaSourceContributions = media.Sources.Select(
                    source => new MediaSourceDateContribution
                    {
                        MediaSourceID = source.MediaSourceID,
                        MediaFileCount = source.MediaFileCount,
                        MediaFileWithFileDateCount = source.MediaFileWithFileDateCount,
                        DistinctNonZeroAssetsWithFileDate =
                            source.DistinctEligibleAssetsWithFileDate.Count,
                        ExactCandidateRelationsContributed =
                            source.ExactCandidateRelationsContributed,
                    }).ToList(),

                AssetsPerDate = media.DescribeDensity(),
            };
        }
    }
}
