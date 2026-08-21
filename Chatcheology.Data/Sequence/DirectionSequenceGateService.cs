using System.Numerics;
using Chatcheology.Data.Media;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Measures whether the archive holds a population valid and informative enough for a later
    /// direction-order alignment test — before any alignment outcome is visible.
    /// </summary>
    /// <remarks>
    /// Read-only in the sense SQLite itself enforces: the connection is opened <c>Mode=ReadOnly</c>, so
    /// a stray write fails rather than succeeding quietly. Nothing is persisted, no schema is touched,
    /// no index is created, no attachment is resolved and no media file is opened — every figure comes
    /// from workspace metadata.
    /// <para>
    /// <b>What it deliberately does not compute.</b> No embedding of an actual token sequence, no
    /// longest common subsequence, no observed embedding share, no order loss, no adjacent-date
    /// comparison, no reversal. The reference quantities it does compute depend on the message pattern
    /// and on the token side's own <c>(outgoing, incoming, runs)</c> class, never on the order those
    /// token symbols were observed in, which is what keeps the gate blind to its own outcome.
    /// </para>
    /// <para>
    /// <b>What it deliberately does not consume.</b> No candidate, no asset identity, no first-pass
    /// relation and no Stage B2B assignment. The stage exists to ask whether the archive independently
    /// supports the monotone hypothesis, and an input carrying an assignment outcome would make that
    /// question circular. The message side is read straight from <c>Attachment</c> and <c>Message</c>,
    /// and the token side straight from <c>MediaFile</c>.
    /// </para>
    /// <para>
    /// Every hard gate here aborts. A device-group mapping failure, an incomplete-Phase-5 workspace, a
    /// marker or date disagreement, an unknown message direction, a Stage A coverage mismatch or a
    /// violated reference identity throws, and no census — not even a partial or flagged one — is
    /// returned.
    /// </para>
    /// </remarks>
    public sealed class DirectionSequenceGateService
    {
        /// <summary>The approved grammar's width: exactly four ASCII digits after <c>-WA</c>.</summary>
        private const int SupportedTokenLength = 4;

        /// <summary>
        /// The fixed-point scale a reported decimal reading of an exact ratio is taken at.
        /// </summary>
        /// <remarks>
        /// Exact integers decide every classification. A decimal appears only where a figure is read
        /// rather than compared, and it is produced by scaled integer division so an astronomically
        /// large numerator and denominator cannot overflow their way to an infinity.
        /// </remarks>
        private const long DecimalReadingScale = 1_000_000_000_000L;

        /// <summary>
        /// The scopes measured, in report order, each reported whole and never merged with the other.
        /// </summary>
        private static readonly ScopeLevel[] MeasuredScopes =
            [ScopeLevel.SourceDate, ScopeLevel.DeviceGroupDate];

        /// <summary>Every descriptive band, so a band with no pairs is still reported as empty.</summary>
        private static readonly DirectionSequenceQrBand[] AllBands =
            Enum.GetValues<DirectionSequenceQrBand>();

        /// <summary>
        /// Runs the gate census over one workspace.
        /// </summary>
        /// <param name="request">What to measure, with nothing left to be inferred.</param>
        /// <param name="pairSink">
        /// An optional sink for the per-pair figures behind the aggregates, keyed by a deterministic
        /// anonymised identifier. Intended for a freeze review's scratch output; it is not part of the
        /// census and nothing here retains it. Only pairs carrying message symbols are offered.
        /// </param>
        /// <param name="cancellationToken">Signalled to abandon the census.</param>
        /// <exception cref="ArgumentException">The request names no workspace.</exception>
        /// <exception cref="FileNotFoundException">There is no workspace at that path.</exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not at the current schema version; the conversation or the local
        /// participant is not there; the device-group mapping does not name every source exactly once;
        /// media hashing or deduplication is incomplete; a recovered name and its persisted date
        /// disagree; a message direction is unknown; declared Stage A coverage disagrees with the
        /// recount; or an exact reference identity was violated. Nothing is repaired and no census is
        /// returned.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was signalled. No census is returned. A supplied
        /// <paramref name="pairSink"/> may already have received a deterministic prefix of the pairs.
        /// </exception>
        public DirectionSequenceGateCensus Analyse(
            DirectionSequenceGateRequest request,
            Action<DirectionSequencePairRow>? pairSink = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);
            ArgumentNullException.ThrowIfNull(request.DeviceGroups);

            // Checked before any work, so a cancelled call cannot return a census merely because the
            // workspace it was pointed at happened to hold nothing to iterate.
            cancellationToken.ThrowIfCancellationRequested();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(request.DatabasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(
                connection, "a direction-sequence gate census");

            RequireConversation(connection, request.ConversationID);
            RequireLocalParticipant(connection, request);

            var sources = ReadMediaSources(connection);
            var groupBySource = ResolveDeviceGroups(request.DeviceGroups, sources);

            var messages = ReadMessageSide(connection, request, cancellationToken);
            var tokens = ReadTokenSide(connection, sources, cancellationToken);

            ReconcileStageACoverage(request.StageATokenCoverage, sources, tokens);

            var deviceGroups = groupBySource.Values.Distinct().Order().ToList();

            var sourceScope = tokens.PositionsBySourceScope();
            var deviceGroupScope = tokens.PositionsByDeviceGroupScope(groupBySource);

            var pairIdentifier = 0;
            var scopes = new List<DirectionSequenceScopeCensus>();

            foreach (var scope in MeasuredScopes)
            {
                scopes.Add(
                    MeasureScope(
                        scope,
                        scope == ScopeLevel.SourceDate ? sources : deviceGroups,
                        scope == ScopeLevel.SourceDate ? sourceScope : deviceGroupScope,
                        messages,
                        pairSink,
                        ref pairIdentifier,
                        cancellationToken));
            }

            cancellationToken.ThrowIfCancellationRequested();

            return new DirectionSequenceGateCensus
            {
                ConversationID = request.ConversationID,
                LocalParticipantID = request.LocalParticipantID,
                MessagePopulation = messages.Build(),
                Sources = BuildSourcePopulations(
                    sources, groupBySource, tokens, sourceScope, request.StageATokenCoverage),
                DeviceGroups = BuildDeviceGroupPopulations(
                    sources, groupBySource, tokens, deviceGroupScope),
                CrossSourceOverlap = BuildCrossSourceOverlap(tokens, groupBySource),
                StageATokenCoverageReconciled = request.StageATokenCoverage is not null,
                Scopes = scopes,
            };
        }

        // -------------------------------------------------------------------------------------------
        // Request validation.
        // -------------------------------------------------------------------------------------------

        /// <remarks>
        /// The same two checks the frozen matching engine applies, deliberately restated rather than
        /// shared: reaching into that path to serve this census would mean editing code whose real-run
        /// evidence is preserved, and the rule is two short queries either way.
        /// </remarks>
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
                $"The workspace has no conversation {conversationID}. A gate census of a conversation " +
                $"that is not there would report an empty population, which reads as a conversation " +
                $"with no attachments rather than as a conversation that does not exist. Nothing has " +
                $"been censused and the workspace is unchanged.");
        }

        /// <remarks>
        /// A participant from another conversation would still produce a direction for every message —
        /// all of it wrong, and all of it looking exactly like the right answer, because the sender
        /// would never match and the whole message side would read as incoming.
        /// </remarks>
        private static void RequireLocalParticipant(
            SqliteConnection connection, DirectionSequenceGateRequest request)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT COUNT(*)
                FROM ConversationParticipant
                WHERE ConversationID = $conversationID
                  AND ParticipantID = $participantID;
                """;

            command.Parameters.AddWithValue("$conversationID", request.ConversationID);
            command.Parameters.AddWithValue("$participantID", request.LocalParticipantID);

            if (Convert.ToInt64(command.ExecuteScalar()) == 1)
            {
                return;
            }

            throw new InvalidOperationException(
                $"Participant {request.LocalParticipantID} does not belong to conversation " +
                $"{request.ConversationID}, so it cannot be that conversation's local user. Every " +
                $"message-side direction symbol rests on this claim, and a wrong local participant " +
                $"would make the whole message side read as incoming. Nothing has been censused and " +
                $"the workspace is unchanged.");
        }

        private static List<long> ReadMediaSources(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MediaSourceID FROM MediaSource ORDER BY MediaSourceID;";

            var sources = new List<long>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                sources.Add(reader.GetInt64(0));
            }

            return sources;
        }

        /// <summary>
        /// Turns the caller's assignments into a source-to-group lookup, refusing anything that is not
        /// a total, single-valued function over exactly the sources present.
        /// </summary>
        /// <remarks>
        /// There is deliberately no default. A silent one-group-per-source fallback would reinstate the
        /// assumption that an acquisition store is a numbering authority, which is exactly the
        /// assumption the sequence work exists to test rather than to make.
        /// <para>
        /// Validated before <c>MediaFile</c> is opened, so a malformed mapping cannot move a counter
        /// before it is caught.
        /// </para>
        /// </remarks>
        private static Dictionary<long, long> ResolveDeviceGroups(
            IReadOnlyList<DeviceGroupAssignment> assignments, List<long> sources)
        {
            if (assignments.Count == 0)
            {
                throw new InvalidOperationException(
                    "No device-group assignment was supplied. A numbering scope is the caller's claim " +
                    "that certain sources share a counter, and this census will not assume one group " +
                    "per source on the caller's behalf: a caller who wants that must say so by " +
                    "supplying it. Nothing has been censused and the workspace is unchanged.");
            }

            var groupBySource = new Dictionary<long, long>();

            foreach (var assignment in assignments)
            {
                ArgumentNullException.ThrowIfNull(assignment);

                if (!groupBySource.TryAdd(assignment.MediaSourceID, assignment.DeviceGroupID))
                {
                    throw new InvalidOperationException(
                        $"MediaSource {assignment.MediaSourceID} is assigned to a device group more " +
                        $"than once. One source belongs to one numbering authority, and a mapping " +
                        $"that says otherwise cannot be applied. Nothing has been censused and the " +
                        $"workspace is unchanged.");
                }
            }

            foreach (var mediaSourceID in sources)
            {
                if (!groupBySource.ContainsKey(mediaSourceID))
                {
                    throw new InvalidOperationException(
                        $"MediaSource {mediaSourceID} exists in the workspace but was assigned to no " +
                        $"device group. Every source must be assigned, because a source left out " +
                        $"would be silently absent from every device-scoped figure. Nothing has been " +
                        $"censused and the workspace is unchanged.");
                }
            }

            foreach (var mediaSourceID in groupBySource.Keys)
            {
                if (!sources.Contains(mediaSourceID))
                {
                    throw new InvalidOperationException(
                        $"The device-group mapping names MediaSource {mediaSourceID}, which this " +
                        $"workspace does not contain. A mapping written for a different workspace " +
                        $"would describe groups this one cannot fill. Nothing has been censused and " +
                        $"the workspace is unchanged.");
                }
            }

            return groupBySource;
        }

        // -------------------------------------------------------------------------------------------
        // The message side.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads one direction symbol per unresolved attachment of the conversation, in the declared
        /// deterministic order.
        /// </summary>
        /// <remarks>
        /// The statement selects unresolved attachments of one conversation and nothing else. No
        /// candidate, asset or media evidence takes part, which makes "candidate availability was not
        /// used as an inclusion rule" a property of the query rather than a promise about the code
        /// above it.
        /// <para>
        /// Ordered by <c>SequenceNumber</c>, then <c>Ordinal</c>, then <c>AttachmentID</c>. The last is
        /// a tie-break the workspace's own uniqueness makes unreachable, and it is there so the order
        /// is total whatever the workspace holds.
        /// </para>
        /// </remarks>
        private static MessageSide ReadMessageSide(
            SqliteConnection connection,
            DirectionSequenceGateRequest request,
            CancellationToken cancellationToken)
        {
            var side = new MessageSide
            {
                WorkspaceUnresolvedAttachmentCount = CountUnresolvedAttachments(connection, null),
                ConversationUnresolvedAttachmentCount =
                    CountUnresolvedAttachments(connection, request.ConversationID),
            };

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    a.AttachmentID,
                    a.MessageID,
                    a.Ordinal,
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

                var messageID = reader.GetInt64(1);
                var ordinal = reader.GetInt32(2);
                var stored = reader.GetString(3);

                if (!WorkspaceDateFormats.TryParseMessageDateTimeLocal(stored, out var localTime))
                {
                    throw new InvalidOperationException(
                        $"Message {messageID} records a local timestamp this build cannot read, so " +
                        $"the local date its attachments belong to cannot be established. Nothing " +
                        $"has been censused and the workspace is unchanged.");
                }

                if (reader.IsDBNull(4))
                {
                    throw new InvalidOperationException(
                        $"Message {messageID} carries an unresolved attachment but names no sender, " +
                        $"so its direction is unknown. The gate emits one direction symbol per " +
                        $"attachment and will not guess one, and an unknown message direction is a " +
                        $"stop condition rather than a figure to carry forward. Nothing has been " +
                        $"censused and the workspace is unchanged.");
                }

                side.Add(
                    messageID,
                    ordinal,
                    DateOnly.FromDateTime(localTime),
                    reader.GetInt64(4) == request.LocalParticipantID);
            }

            return side;
        }

        private static int CountUnresolvedAttachments(
            SqliteConnection connection, long? conversationID)
        {
            using var command = connection.CreateCommand();

            command.CommandText = conversationID is null
                ? "SELECT COUNT(*) FROM Attachment WHERE ResolutionStatus = $unresolved;"
                : """
                  SELECT COUNT(*)
                  FROM Attachment AS a
                  INNER JOIN Message AS m ON m.MessageID = a.MessageID
                  WHERE a.ResolutionStatus = $unresolved
                    AND m.ConversationID = $conversationID;
                  """;

            command.Parameters.AddWithValue(
                "$unresolved", WorkspaceDatabase.UnresolvedAttachmentStatus);

            if (conversationID is { } identifier)
            {
                command.Parameters.AddWithValue("$conversationID", identifier);
            }

            return Convert.ToInt32(command.ExecuteScalar());
        }

        // -------------------------------------------------------------------------------------------
        // The token side.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads and validates every media row in one ordered pass, keeping the supported tokens with
        /// the direction evidence of the copies carrying them.
        /// </summary>
        /// <remarks>
        /// Only the columns this census is entitled to use are selected. No root path, relative path,
        /// display name, device description, message, attachment, conversation or participant appears
        /// in the statement, which makes that boundary a property of the query. The two hash columns
        /// are read for one purpose — proving a file and its asset agree about the payload — and are
        /// never emitted.
        /// <para>
        /// Rows are ordered by <c>MediaFileID</c> so a file somehow holding two asset links would
        /// appear as an adjacent pair and be refused before any counter moves.
        /// </para>
        /// </remarks>
        private static TokenSide ReadTokenSide(
            SqliteConnection connection, List<long> sources, CancellationToken cancellationToken)
        {
            var side = new TokenSide(sources);

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    f.MediaFileID,
                    f.MediaSourceID,
                    f.FileName,
                    f.Extension,
                    f.FileDate,
                    f.IsSent,
                    f.SHA256,
                    l.MediaAssetID,
                    a.SHA256
                FROM MediaFile AS f
                LEFT JOIN MediaAssetFile AS l ON l.MediaFileID = f.MediaFileID
                LEFT JOIN MediaAsset AS a ON a.MediaAssetID = l.MediaAssetID
                ORDER BY f.MediaFileID;
                """;

            using var reader = command.ExecuteReader();

            var previousMediaFileID = -1L;

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();

                var mediaFileID = reader.GetInt64(0);

                RequireSingleAssetLink(mediaFileID, previousMediaFileID);
                RequireHashedFile(reader, mediaFileID);
                RequireAssetLink(reader, mediaFileID);
                RequireMatchingHashes(reader, mediaFileID);

                previousMediaFileID = mediaFileID;

                var mediaSourceID = reader.GetInt64(1);
                var isSent = reader.IsDBNull(5) ? null : (bool?)(reader.GetInt64(5) != 0);

                side.CountPhysical(mediaSourceID, isSent);

                if (reader.IsDBNull(4))
                {
                    // Undated rows carry no date to key a logical position on. Stage B1 established
                    // archive-wide that an undated name holds no marker either, and re-deriving that
                    // here would need source-type semantics this census has no other use for.
                    continue;
                }

                var fileDate = ReadFileDate(reader, mediaFileID);
                var fileName = reader.GetString(2);
                var extension = reader.IsDBNull(3) ? null : reader.GetString(3);

                side.CountDated(mediaSourceID);

                RequireMarkerAgreeingWithFileDate(
                    mediaFileID, fileName, fileDate, out var suffixStart);

                if (ReadSupportedToken(fileName, extension, suffixStart) is not { } token)
                {
                    continue;
                }

                side.RecordSupportedToken(mediaSourceID, fileDate, token, isSent);
            }

            return side;
        }

        private static void RequireSingleAssetLink(long mediaFileID, long previousMediaFileID)
        {
            if (mediaFileID != previousMediaFileID)
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} is linked to more than one MediaAsset. One physical file " +
                $"carries one payload, and the workspace's own unique constraint says so, which makes " +
                $"this a workspace written by something that did not enforce it. Counting the file " +
                $"twice would inflate every position figure in this census. Nothing has been censused " +
                $"and the workspace is unchanged.");
        }

        /// <remarks>
        /// The same completed-Phase-5 rules the matching engine and the earlier sequence censuses
        /// apply, and deliberately another copy rather than a shared helper: extracting one would mean
        /// editing a frozen path whose real-run evidence is preserved. Tests cover it on every side.
        /// </remarks>
        private static void RequireHashedFile(SqliteDataReader reader, long mediaFileID)
        {
            if (!reader.IsDBNull(6))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has no SHA-256, so media hashing is incomplete and Phase 5 " +
                $"has not finished for this workspace. A gate census taken now would describe whatever " +
                $"fraction of the archive happened to be hashed as though it were the archive. Nothing " +
                $"has been censused and the workspace is unchanged.");
        }

        private static void RequireAssetLink(SqliteDataReader reader, long mediaFileID)
        {
            if (reader.IsDBNull(7))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is hashed but linked to no MediaAsset, so deduplication " +
                    $"is incomplete for this workspace. Nothing has been censused and the workspace " +
                    $"is unchanged.");
            }

            if (reader.IsDBNull(8))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is linked to a MediaAsset that does not exist. The " +
                    $"workspace's foreign keys forbid this, so it was written with enforcement off. " +
                    $"Nothing has been censused and the workspace is unchanged.");
            }
        }

        private static void RequireMatchingHashes(SqliteDataReader reader, long mediaFileID)
        {
            if (string.Equals(
                    reader.GetString(6), reader.GetString(8), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} and the MediaAsset it is linked to record different SHA-256 " +
                $"values, so the workspace disagrees with itself about which payload this file holds. " +
                $"Deduplication cannot be trusted, and neither can any figure derived from it. " +
                $"Nothing has been censused and the workspace is unchanged.");
        }

        private static DateOnly ReadFileDate(SqliteDataReader reader, long mediaFileID)
        {
            if (WorkspaceDateFormats.TryParseFileDate(reader.GetString(4), out var fileDate))
            {
                return fileDate;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} records a FileDate this build cannot read, so the local date " +
                $"its token belongs to cannot be established. Nothing has been censused and the " +
                $"workspace is unchanged.");
        }

        /// <remarks>
        /// The committed Stage A / Stage B1 rule, character for character: the eight marker digits must
        /// be a real calendar date preceded by <c>-</c> and followed by <c>-WA</c>, and they must agree
        /// with the persisted <c>FileDate</c>. A dated row whose own name encodes a different day is a
        /// workspace this census cannot read, because the token comes from the name and the date is
        /// joined from the column.
        /// </remarks>
        private static void RequireMarkerAgreeingWithFileDate(
            long mediaFileID, string fileName, DateOnly fileDate, out int suffixStart)
        {
            if (!WhatsAppNameMarker.TryLocate(fileName, out suffixStart, out var markerDate))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} carries a FileDate but no locatable -YYYYMMDD-WA " +
                    $"marker, so this census and the committed classifier disagree about which " +
                    $"characters are a date. Nothing has been censused and the workspace is " +
                    $"unchanged.");
            }

            if (markerDate != fileDate)
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} records a FileDate that is not the date its own name " +
                    $"encodes. The token is read from the name and the date is joined from the " +
                    $"column, so a workspace where the two disagree cannot be censused. Nothing has " +
                    $"been censused and the workspace is unchanged.");
            }
        }

        /// <summary>
        /// The approved four-digit token, or null when the name's suffix is not that shape.
        /// </summary>
        /// <remarks>
        /// The committed Stage A / Stage B1 rule, character for character: the whole remainder after
        /// <c>-WA</c>, with the recorded extension removed only when the name really ends with it, must
        /// be exactly four ASCII digits. Stated here rather than shared, because extracting a helper
        /// would mean editing a frozen path whose real-run evidence is preserved; the
        /// grammar-equivalence tests are what contain the drift.
        /// </remarks>
        private static ushort? ReadSupportedToken(
            string fileName, string? extension, int suffixStart)
        {
            var remainder = fileName[suffixStart..];

            var extensionMatchesEnding =
                extension is not null
                && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

            var suffix = extensionMatchesEnding && remainder.Length >= extension!.Length
                ? remainder[..^extension.Length]
                : remainder;

            if (suffix.Length != SupportedTokenLength)
            {
                return null;
            }

            foreach (var character in suffix)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return null;
                }
            }

            // Fixed width, so the numeric value orders identically to the digit string.
            return (ushort)(
                ((suffix[0] - '0') * 1000)
                + ((suffix[1] - '0') * 100)
                + ((suffix[2] - '0') * 10)
                + (suffix[3] - '0'));
        }

        /// <summary>
        /// Refuses to continue where declared Stage A coverage and the gate's own recount disagree.
        /// </summary>
        /// <remarks>
        /// The gate necessarily recounts supported tokens, because it cannot read a preserved report.
        /// The declaration is what turns that recount into a reconciliation: without it, a grammar that
        /// had drifted since Stage A would produce a census indistinguishable from one that had not.
        /// </remarks>
        private static void ReconcileStageACoverage(
            IReadOnlyList<StageATokenCoverageDeclaration>? declarations,
            List<long> sources,
            TokenSide tokens)
        {
            if (declarations is null)
            {
                return;
            }

            var declared = new Dictionary<long, StageATokenCoverageDeclaration>();

            foreach (var declaration in declarations)
            {
                ArgumentNullException.ThrowIfNull(declaration);

                if (!declared.TryAdd(declaration.MediaSourceID, declaration))
                {
                    throw new InvalidOperationException(
                        $"The declared Stage A coverage names MediaSource " +
                        $"{declaration.MediaSourceID} more than once, so it does not say what the " +
                        $"preserved figure for that source was. Nothing has been censused and the " +
                        $"workspace is unchanged.");
                }
            }

            foreach (var mediaSourceID in declared.Keys)
            {
                if (!sources.Contains(mediaSourceID))
                {
                    throw new InvalidOperationException(
                        $"The declared Stage A coverage names MediaSource {mediaSourceID}, which " +
                        $"this workspace does not contain, so it was written for a different " +
                        $"workspace. Nothing has been censused and the workspace is unchanged.");
                }
            }

            foreach (var mediaSourceID in sources)
            {
                if (!declared.TryGetValue(mediaSourceID, out var declaration))
                {
                    throw new InvalidOperationException(
                        $"The declared Stage A coverage does not name MediaSource {mediaSourceID}, " +
                        $"which this workspace contains. A partial declaration would reconcile part " +
                        $"of the token population and silently accept the rest. Nothing has been " +
                        $"censused and the workspace is unchanged.");
                }

                var tally = tokens.TallyFor(mediaSourceID);

                RequireDeclaredFigure(
                    mediaSourceID,
                    "physical media rows",
                    declaration.PhysicalObservationCount,
                    tally.PhysicalObservations);

                RequireDeclaredFigure(
                    mediaSourceID,
                    "supported-token observations",
                    declaration.SupportedTokenObservationCount,
                    tally.SupportedObservations);
            }
        }

        private static void RequireDeclaredFigure(
            long mediaSourceID, string figure, int declared, int observed)
        {
            if (declared == observed)
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaSource {mediaSourceID} was declared to hold {declared} {figure} at Stage A, " +
                $"and this census counts {observed}. Either the grammar or the population has moved " +
                $"since Stage A, and a gate that carried the difference forward would report a " +
                $"coverage figure that no longer describes what it measured. Nothing has been " +
                $"censused and the workspace is unchanged.");
        }

        // -------------------------------------------------------------------------------------------
        // Token-side population reporting.
        // -------------------------------------------------------------------------------------------

        private static List<DirectionSequenceSourcePopulation> BuildSourcePopulations(
            List<long> sources,
            Dictionary<long, long> groupBySource,
            TokenSide tokens,
            Dictionary<ScopePairKey, SortedDictionary<ushort, LogicalPosition>> sourceScope,
            IReadOnlyList<StageATokenCoverageDeclaration>? declarations)
        {
            var declared = new Dictionary<long, StageATokenCoverageDeclaration>();

            if (declarations is not null)
            {
                foreach (var declaration in declarations)
                {
                    declared[declaration.MediaSourceID] = declaration;
                }
            }

            var populations = new List<DirectionSequenceSourcePopulation>();

            foreach (var mediaSourceID in sources)
            {
                var tally = tokens.TallyFor(mediaSourceID);

                var collapsed = 0;
                var labelled = 0;
                var unlabelled = 0;
                var conflicting = 0;
                var shared = 0;

                foreach (var (pairKey, atDate) in sourceScope)
                {
                    if (pairKey.ScopeKey != mediaSourceID)
                    {
                        continue;
                    }

                    foreach (var (token, position) in atDate)
                    {
                        collapsed++;

                        if (position.IsConflicting)
                        {
                            conflicting++;
                        }
                        else if (position.HasDirection)
                        {
                            labelled++;
                        }
                        else
                        {
                            unlabelled++;
                        }

                        if (tokens.SourceCountAt(new PositionKey(pairKey.Date, token)) > 1)
                        {
                            shared++;
                        }
                    }
                }

                declared.TryGetValue(mediaSourceID, out var declaration);

                populations.Add(
                    new DirectionSequenceSourcePopulation
                    {
                        MediaSourceID = mediaSourceID,
                        DeviceGroupID = groupBySource[mediaSourceID],
                        PhysicalObservationCount = tally.PhysicalObservations,
                        DatedObservationCount = tally.DatedObservations,
                        SupportedTokenObservationCount = tally.SupportedObservations,
                        DirectionCapableObservationCount = tally.DirectionCapableObservations,
                        SupportedObservationsWithoutDirectionCount =
                            tally.SupportedObservations - tally.DirectionCapableObservations,
                        RecordsAnyDirection = tally.RecordsAnyDirection,
                        DistinctSupportedDateCount = tally.SupportedDates.Count,
                        EarliestSupportedDate = tally.EarliestSupportedDate,
                        LatestSupportedDate = tally.LatestSupportedDate,
                        LogicalPositionsBeforeCollapse = tally.SupportedObservations,
                        LogicalPositionsAfterCollapse = collapsed,
                        DirectionLabelledLogicalPositionCount = labelled,
                        LogicalPositionsWithoutDirectionCount = unlabelled,
                        ConflictingLogicalPositionCount = conflicting,
                        SharedLogicalPositionCount = shared,
                        SourceOnlyLogicalPositionCount = collapsed - shared,
                        DeclaredStageAPhysicalObservationCount =
                            declaration?.PhysicalObservationCount,
                        DeclaredStageASupportedTokenObservationCount =
                            declaration?.SupportedTokenObservationCount,
                    });
            }

            return populations;
        }

        private static List<DirectionSequenceDeviceGroupPopulation> BuildDeviceGroupPopulations(
            List<long> sources,
            Dictionary<long, long> groupBySource,
            TokenSide tokens,
            Dictionary<ScopePairKey, SortedDictionary<ushort, LogicalPosition>> deviceGroupScope)
        {
            var populations = new List<DirectionSequenceDeviceGroupPopulation>();

            foreach (var deviceGroupID in groupBySource.Values.Distinct().Order())
            {
                var physical = 0;
                var supported = 0;
                var capable = 0;
                var blindObservations = 0;
                var blindSources = 0;
                var members = 0;

                foreach (var mediaSourceID in sources)
                {
                    if (groupBySource[mediaSourceID] != deviceGroupID)
                    {
                        continue;
                    }

                    members++;

                    var tally = tokens.TallyFor(mediaSourceID);

                    physical += tally.PhysicalObservations;
                    supported += tally.SupportedObservations;
                    capable += tally.DirectionCapableObservations;

                    if (tally.RecordsAnyDirection)
                    {
                        continue;
                    }

                    blindSources++;
                    blindObservations += tally.SupportedObservations;
                }

                var collapsed = 0;
                var labelled = 0;
                var unlabelled = 0;
                var conflicting = 0;
                var blindOnly = 0;
                var dates = new HashSet<DateOnly>();

                foreach (var (pairKey, atDate) in deviceGroupScope)
                {
                    if (pairKey.ScopeKey != deviceGroupID)
                    {
                        continue;
                    }

                    dates.Add(pairKey.Date);

                    foreach (var (token, position) in atDate)
                    {
                        collapsed++;

                        if (position.IsConflicting)
                        {
                            conflicting++;
                        }
                        else if (position.HasDirection)
                        {
                            labelled++;
                        }
                        else
                        {
                            unlabelled++;
                        }

                        if (tokens.IsKnownOnlyFromDirectionBlindSources(
                                new PositionKey(pairKey.Date, token), deviceGroupID, groupBySource))
                        {
                            blindOnly++;
                        }
                    }
                }

                populations.Add(
                    new DirectionSequenceDeviceGroupPopulation
                    {
                        DeviceGroupID = deviceGroupID,
                        SourceCount = members,
                        DirectionCapableSourceCount = members - blindSources,
                        DirectionBlindSourceCount = blindSources,
                        PhysicalObservationCount = physical,
                        SupportedTokenObservationCount = supported,
                        DirectionCapableObservationCount = capable,
                        DirectionBlindSourceObservationCount = blindObservations,
                        DistinctSupportedDateCount = dates.Count,
                        EarliestSupportedDate = dates.Count == 0 ? null : dates.Min(),
                        LatestSupportedDate = dates.Count == 0 ? null : dates.Max(),
                        LogicalPositionsBeforeCollapse = supported,
                        LogicalPositionsAfterCollapse = collapsed,
                        DirectionLabelledLogicalPositionCount = labelled,
                        LogicalPositionsWithoutDirectionCount = unlabelled,
                        ConflictingLogicalPositionCount = conflicting,
                        PositionsKnownOnlyFromDirectionBlindSources = blindOnly,
                    });
            }

            return populations;
        }

        /// <summary>
        /// Classifies every logical position more than one source observed, and counts the
        /// disagreements.
        /// </summary>
        /// <remarks>
        /// A position where one source's own copies disagree is counted as conflicting whatever the
        /// other sources say, because the label is already unstable within a single tree. A
        /// disagreement confined to one device group is separated from one that spans groups, since
        /// only the first excludes a device-group pair.
        /// </remarks>
        private static DirectionSequenceCrossSourceOverlap BuildCrossSourceOverlap(
            TokenSide tokens, Dictionary<long, long> groupBySource)
        {
            var distinct = 0;
            var shared = 0;
            var agreeing = 0;
            var conflicting = 0;
            var oneSide = 0;
            var neitherSide = 0;
            var withinGroup = 0;
            var acrossGroups = 0;

            foreach (var (_, bySource) in tokens.AllPositions)
            {
                distinct++;

                if (bySource.Count < 2)
                {
                    continue;
                }

                shared++;

                var outgoing = 0;
                var incoming = 0;
                var internallyConflicted = 0;
                var groups = new HashSet<long>();

                foreach (var (mediaSourceID, position) in bySource)
                {
                    if (position.IsConflicting)
                    {
                        internallyConflicted++;
                    }
                    else if (position.IsOutgoing)
                    {
                        outgoing++;
                    }
                    else if (position.HasDirection)
                    {
                        incoming++;
                    }
                    else
                    {
                        continue;
                    }

                    groups.Add(groupBySource[mediaSourceID]);
                }

                var known = outgoing + incoming + internallyConflicted;

                if (known == 0)
                {
                    neitherSide++;

                    continue;
                }

                if (internallyConflicted > 0 || (outgoing > 0 && incoming > 0))
                {
                    conflicting++;

                    if (internallyConflicted > 0 || groups.Count == 1)
                    {
                        withinGroup++;
                    }
                    else
                    {
                        acrossGroups++;
                    }
                }
                else if (known == 1)
                {
                    oneSide++;
                }
                else
                {
                    agreeing++;
                }
            }

            return new DirectionSequenceCrossSourceOverlap
            {
                DistinctLogicalPositionCount = distinct,
                SharedLogicalPositionCount = shared,
                SingleSourceLogicalPositionCount = distinct - shared,
                AgreeingPositionCount = agreeing,
                ConflictingPositionCount = conflicting,
                OneSideDirectionKnownPositionCount = oneSide,
                NoDirectionKnownPositionCount = neitherSide,
                ConflictingPositionsWithinOneDeviceGroup = withinGroup,
                ConflictingPositionsSpanningDeviceGroups = acrossGroups,
            };
        }

        // -------------------------------------------------------------------------------------------
        // Per-scope measurement.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Measures one scope whole: its pair universe, its exclusions, its supply, its burstiness and
        /// its exact reference expectations.
        /// </summary>
        /// <remarks>
        /// The pair universe is every scope key paired with every date on which either that scope key
        /// holds a supported token position or the conversation holds an unresolved attachment. Scope
        /// keys and dates are walked in ascending order, so the anonymised pair identifiers a sink
        /// receives are the same on every run over the same workspace.
        /// </remarks>
        private static DirectionSequenceScopeCensus MeasureScope(
            ScopeLevel scope,
            List<long> scopeKeys,
            Dictionary<ScopePairKey, SortedDictionary<ushort, LogicalPosition>> positions,
            MessageSide messages,
            Action<DirectionSequencePairRow>? pairSink,
            ref int pairIdentifier,
            CancellationToken cancellationToken)
        {
            var datesByScopeKey = new Dictionary<long, SortedSet<DateOnly>>();

            foreach (var scopeKey in scopeKeys)
            {
                datesByScopeKey[scopeKey] = new SortedSet<DateOnly>(messages.Dates);
            }

            foreach (var pairKey in positions.Keys)
            {
                if (datesByScopeKey.TryGetValue(pairKey.ScopeKey, out var dates))
                {
                    dates.Add(pairKey.Date);
                }
            }

            var accumulator = new ScopeAccumulator(scope, scopeKeys.Count);

            foreach (var scopeKey in scopeKeys.Order())
            {
                foreach (var date in datesByScopeKey[scopeKey])
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    positions.TryGetValue(new ScopePairKey(scopeKey, date), out var atDate);

                    var row = accumulator.Add(
                        BuildShape(scope, scopeKey, date, messages.PatternOn(date), atDate),
                        ref pairIdentifier);

                    if (row is null)
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();

                    pairSink?.Invoke(row);
                }
            }

            return accumulator.Build();
        }

        /// <summary>
        /// Collapses one pair's logical positions into the two sequences and the counts every figure is
        /// derived from.
        /// </summary>
        /// <remarks>
        /// Emitted symbols are ordered by the supported four-digit token value, which is identical to
        /// ordering by the digit string because the grammar is fixed width. Source and folder evidence
        /// selects and labels positions here; it never orders them.
        /// <para>
        /// A position whose copies disagree emits nothing and is counted as a conflict; a position no
        /// copy of which records direction emits nothing and is counted as direction-coverage loss.
        /// Neither is coerced into a symbol.
        /// </para>
        /// </remarks>
        private static PairShape BuildShape(
            ScopeLevel scope,
            long scopeKey,
            DateOnly date,
            bool[] pattern,
            SortedDictionary<ushort, LogicalPosition>? positions)
        {
            var conflicting = 0;
            var unlabelled = 0;
            var outgoingCopies = 0;
            var incomingCopies = 0;
            var emitted = new List<bool>();

            if (positions is not null)
            {
                foreach (var (_, position) in positions)
                {
                    outgoingCopies += position.SentCopies;
                    incomingCopies += position.NotUnderSentCopies;

                    if (position.IsConflicting)
                    {
                        conflicting++;
                    }
                    else if (position.HasDirection)
                    {
                        emitted.Add(position.IsOutgoing);
                    }
                    else
                    {
                        unlabelled++;
                    }
                }
            }

            return new PairShape
            {
                Scope = scope,
                ScopeKey = scopeKey,
                Date = date,
                Pattern = pattern,
                EmittedTokens = [.. emitted],
                ConflictingPositionCount = conflicting,
                UnlabelledPositionCount = unlabelled,
                OutgoingCopiesBeforeCollapse = outgoingCopies,
                IncomingCopiesBeforeCollapse = incomingCopies,
            };
        }

        // -------------------------------------------------------------------------------------------
        // Exact ratios, bands and distributions.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// A decimal reading of an exact ratio, taken by scaled integer division so that an enormous
        /// numerator and denominator cannot overflow to an infinity.
        /// </summary>
        internal static double DecimalReading(BigInteger numerator, BigInteger denominator)
        {
            if (denominator.IsZero)
            {
                return 0d;
            }

            var negative = numerator.Sign * denominator.Sign < 0;

            var scaled =
                BigInteger.Abs(numerator) * DecimalReadingScale / BigInteger.Abs(denominator);

            var reading = (double)scaled / DecimalReadingScale;

            return negative ? -reading : reading;
        }

        /// <summary>
        /// Which descriptive band an exact <c>q_r</c> falls in, decided by integer comparison so the
        /// two endpoints stay exact.
        /// </summary>
        internal static DirectionSequenceQrBand BandOf(BigInteger admitting, BigInteger arrangements)
        {
            if (admitting.IsZero)
            {
                return DirectionSequenceQrBand.ExactlyZero;
            }

            if (admitting == arrangements)
            {
                return DirectionSequenceQrBand.ExactlyOne;
            }

            if (admitting * 20 <= arrangements)
            {
                return DirectionSequenceQrBand.AboveZeroToFiveHundredths;
            }

            if (admitting * 4 <= arrangements)
            {
                return DirectionSequenceQrBand.AboveFiveHundredthsToOneQuarter;
            }

            if (admitting * 2 <= arrangements)
            {
                return DirectionSequenceQrBand.AboveOneQuarterToOneHalf;
            }

            if (admitting * 4 <= arrangements * 3)
            {
                return DirectionSequenceQrBand.AboveOneHalfToThreeQuarters;
            }

            return admitting * 20 <= arrangements * 19
                ? DirectionSequenceQrBand.AboveThreeQuartersToNinetyFiveHundredths
                : DirectionSequenceQrBand.AboveNinetyFiveHundredthsBelowOne;
        }

        internal static void Increment(Dictionary<int, int> counts, int value) =>
            counts[value] = counts.TryGetValue(value, out var existing) ? existing + 1 : 1;

        internal static List<ValueCount> Distribution(Dictionary<int, int> counts)
        {
            var rows = new List<ValueCount>();

            foreach (var value in counts.Keys.Order())
            {
                rows.Add(new ValueCount { Value = value, Count = counts[value] });
            }

            return rows;
        }

        /// <summary>
        /// The spread and bands of a set of counts, with the lower of the two middle values as the
        /// median so a second run of this census is comparable with the first.
        /// </summary>
        internal static CountSummary Summarise(List<int> counts)
        {
            if (counts.Count == 0)
            {
                return new CountSummary
                {
                    Population = 0,
                    Minimum = 0,
                    Median = 0,
                    Maximum = 0,
                    One = 0,
                    Two = 0,
                    ThreeToFive = 0,
                    SixToTen = 0,
                    ElevenToTwentyFive = 0,
                    MoreThanTwentyFive = 0,
                };
            }

            var ordered = counts.ToList();
            ordered.Sort();

            var one = 0;
            var two = 0;
            var threeToFive = 0;
            var sixToTen = 0;
            var elevenToTwentyFive = 0;
            var moreThanTwentyFive = 0;

            foreach (var count in ordered)
            {
                switch (count)
                {
                    case 1:
                        one++;
                        break;

                    case 2:
                        two++;
                        break;

                    case <= 5:
                        threeToFive++;
                        break;

                    case <= 10:
                        sixToTen++;
                        break;

                    case <= 25:
                        elevenToTwentyFive++;
                        break;

                    default:
                        moreThanTwentyFive++;
                        break;
                }
            }

            return new CountSummary
            {
                Population = ordered.Count,
                Minimum = ordered[0],
                Median = ordered[(ordered.Count - 1) / 2],
                Maximum = ordered[^1],
                One = one,
                Two = two,
                ThreeToFive = threeToFive,
                SixToTen = sixToTen,
                ElevenToTwentyFive = elevenToTwentyFive,
                MoreThanTwentyFive = moreThanTwentyFive,
            };
        }

        // -------------------------------------------------------------------------------------------
        // The two evidence streams, held apart until the pair is assembled.
        // -------------------------------------------------------------------------------------------

        /// <summary>One logical <c>(date, token)</c> position, archive-wide.</summary>
        private readonly record struct PositionKey(DateOnly Date, ushort Token);

        /// <summary>One <c>(scope key, local date)</c> pair.</summary>
        private readonly record struct ScopePairKey(long ScopeKey, DateOnly Date);

        /// <summary>
        /// What the copies of one logical position say about its direction.
        /// </summary>
        /// <remarks>
        /// Copies are counted rather than reduced to a verdict, so a disagreement remains visible as a
        /// disagreement. Equivalent observations collapse to one position when their direction agrees;
        /// where it does not, no source is preferred and nothing is dropped in isolation.
        /// </remarks>
        private sealed class LogicalPosition
        {
            internal int SentCopies { get; private set; }

            internal int NotUnderSentCopies { get; private set; }

            internal int UnknownCopies { get; private set; }

            /// <summary>Copies disagree, so the whole affected pair is excluded.</summary>
            internal bool IsConflicting => SentCopies > 0 && NotUnderSentCopies > 0;

            /// <summary>Exactly one direction is recorded, so the position emits a symbol.</summary>
            internal bool HasDirection =>
                !IsConflicting && (SentCopies > 0 || NotUnderSentCopies > 0);

            /// <summary>Beneath a complete <c>Sent</c> segment, which the gate reads as outgoing.</summary>
            internal bool IsOutgoing => SentCopies > 0 && NotUnderSentCopies == 0;

            internal void Observe(bool? isSent)
            {
                switch (isSent)
                {
                    case true:
                        SentCopies++;
                        break;

                    case false:
                        NotUnderSentCopies++;
                        break;

                    default:
                        UnknownCopies++;
                        break;
                }
            }

            internal void Merge(LogicalPosition other)
            {
                SentCopies += other.SentCopies;
                NotUnderSentCopies += other.NotUnderSentCopies;
                UnknownCopies += other.UnknownCopies;
            }
        }

        /// <summary>One acquisition source's running token-side counts.</summary>
        private sealed class SourceTally
        {
            internal int PhysicalObservations { get; set; }

            internal int DatedObservations { get; set; }

            internal int SupportedObservations { get; set; }

            internal int DirectionCapableObservations { get; set; }

            /// <summary>
            /// Whether any row of this source records folder direction at all, which is what makes it
            /// able to emit a symbol.
            /// </summary>
            internal bool RecordsAnyDirection { get; set; }

            internal HashSet<DateOnly> SupportedDates { get; } = [];

            internal DateOnly? EarliestSupportedDate =>
                SupportedDates.Count == 0 ? null : SupportedDates.Min();

            internal DateOnly? LatestSupportedDate =>
                SupportedDates.Count == 0 ? null : SupportedDates.Max();
        }

        /// <summary>
        /// The token side: one tally per source, and every logical position with the per-source
        /// direction evidence behind it.
        /// </summary>
        /// <remarks>
        /// Positions are held per source rather than pre-merged, because both scopes are reported and
        /// the cross-source consistency check needs to see the sources apart. Merging happens when a
        /// scope map is built, into fresh positions, so no scope can disturb another.
        /// </remarks>
        private sealed class TokenSide
        {
            private readonly Dictionary<long, SourceTally> _tallies = [];

            private readonly Dictionary<PositionKey, Dictionary<long, LogicalPosition>> _positions =
                [];

            internal TokenSide(List<long> sources)
            {
                foreach (var mediaSourceID in sources)
                {
                    _tallies[mediaSourceID] = new SourceTally();
                }
            }

            internal IReadOnlyDictionary<PositionKey, Dictionary<long, LogicalPosition>>
                AllPositions => _positions;

            internal SourceTally TallyFor(long mediaSourceID) =>
                _tallies.TryGetValue(mediaSourceID, out var tally)
                    ? tally
                    : throw new InvalidOperationException(
                        $"A MediaFile row names MediaSource {mediaSourceID}, which the MediaSource " +
                        $"table does not contain. The workspace's foreign keys forbid this, so it " +
                        $"was written with enforcement off. Nothing has been censused and the " +
                        $"workspace is unchanged.");

            internal void CountPhysical(long mediaSourceID, bool? isSent)
            {
                var tally = TallyFor(mediaSourceID);

                tally.PhysicalObservations++;

                if (isSent is not null)
                {
                    tally.RecordsAnyDirection = true;
                }
            }

            internal void CountDated(long mediaSourceID) => TallyFor(mediaSourceID)
                .DatedObservations++;

            internal void RecordSupportedToken(
                long mediaSourceID, DateOnly date, ushort token, bool? isSent)
            {
                var tally = TallyFor(mediaSourceID);

                tally.SupportedObservations++;
                tally.SupportedDates.Add(date);

                if (isSent is not null)
                {
                    tally.DirectionCapableObservations++;
                }

                var key = new PositionKey(date, token);

                if (!_positions.TryGetValue(key, out var bySource))
                {
                    bySource = [];
                    _positions[key] = bySource;
                }

                if (!bySource.TryGetValue(mediaSourceID, out var position))
                {
                    position = new LogicalPosition();
                    bySource[mediaSourceID] = position;
                }

                position.Observe(isSent);
            }

            internal int SourceCountAt(PositionKey key) => _positions[key].Count;

            /// <summary>
            /// Whether every source of <paramref name="deviceGroupID"/> observing this position is
            /// direction-blind, so the position is dropped however many copies of it survive.
            /// </summary>
            internal bool IsKnownOnlyFromDirectionBlindSources(
                PositionKey key, long deviceGroupID, Dictionary<long, long> groupBySource)
            {
                var contributors = 0;

                foreach (var mediaSourceID in _positions[key].Keys)
                {
                    if (groupBySource[mediaSourceID] != deviceGroupID)
                    {
                        continue;
                    }

                    if (TallyFor(mediaSourceID).RecordsAnyDirection)
                    {
                        return false;
                    }

                    contributors++;
                }

                return contributors > 0;
            }

            internal Dictionary<ScopePairKey, SortedDictionary<ushort, LogicalPosition>>
                PositionsBySourceScope()
            {
                var scoped = new Dictionary<ScopePairKey, SortedDictionary<ushort, LogicalPosition>>();

                foreach (var (key, bySource) in _positions)
                {
                    foreach (var (mediaSourceID, position) in bySource)
                    {
                        AtPair(scoped, new ScopePairKey(mediaSourceID, key.Date))[key.Token] =
                            position;
                    }
                }

                return scoped;
            }

            internal Dictionary<ScopePairKey, SortedDictionary<ushort, LogicalPosition>>
                PositionsByDeviceGroupScope(Dictionary<long, long> groupBySource)
            {
                var scoped = new Dictionary<ScopePairKey, SortedDictionary<ushort, LogicalPosition>>();

                foreach (var (key, bySource) in _positions)
                {
                    foreach (var (mediaSourceID, position) in bySource)
                    {
                        var atPair = AtPair(
                            scoped, new ScopePairKey(groupBySource[mediaSourceID], key.Date));

                        if (!atPair.TryGetValue(key.Token, out var merged))
                        {
                            merged = new LogicalPosition();
                            atPair[key.Token] = merged;
                        }

                        merged.Merge(position);
                    }
                }

                return scoped;
            }

            private static SortedDictionary<ushort, LogicalPosition> AtPair(
                Dictionary<ScopePairKey, SortedDictionary<ushort, LogicalPosition>> scoped,
                ScopePairKey key)
            {
                if (scoped.TryGetValue(key, out var atPair))
                {
                    return atPair;
                }

                atPair = [];
                scoped[key] = atPair;

                return atPair;
            }
        }

        /// <summary>
        /// The message side: one direction symbol per unresolved attachment, grouped by local date.
        /// </summary>
        /// <remarks>
        /// The per-date pattern is the unit every reference is computed against, because the message
        /// pattern depends on the date and nothing else. Only the date, the order and the direction are
        /// retained; no content, sender name or timestamp survives the read.
        /// </remarks>
        private sealed class MessageSide
        {
            private readonly Dictionary<DateOnly, List<bool>> _symbolsByDate = [];
            private readonly Dictionary<DateOnly, bool[]> _patterns = [];
            private readonly Dictionary<long, int> _attachmentsPerMessage = [];
            private readonly Dictionary<int, int> _ordinals = [];

            private int _considered;
            private int _outgoing;
            private int _incoming;

            internal required int WorkspaceUnresolvedAttachmentCount { get; init; }

            internal required int ConversationUnresolvedAttachmentCount { get; init; }

            internal IEnumerable<DateOnly> Dates => _symbolsByDate.Keys;

            internal void Add(long messageID, int ordinal, DateOnly date, bool outgoing)
            {
                _considered++;

                Increment(_ordinals, ordinal);

                _attachmentsPerMessage[messageID] =
                    _attachmentsPerMessage.TryGetValue(messageID, out var existing)
                        ? existing + 1
                        : 1;

                if (outgoing)
                {
                    _outgoing++;
                }
                else
                {
                    _incoming++;
                }

                if (!_symbolsByDate.TryGetValue(date, out var symbols))
                {
                    symbols = [];
                    _symbolsByDate[date] = symbols;
                }

                symbols.Add(outgoing);
            }

            /// <summary>
            /// The date's message pattern, or an empty one where it carries no attachment.
            /// </summary>
            internal bool[] PatternOn(DateOnly date)
            {
                if (_patterns.TryGetValue(date, out var cached))
                {
                    return cached;
                }

                var pattern = _symbolsByDate.TryGetValue(date, out var symbols)
                    ? symbols.ToArray()
                    : [];

                _patterns[date] = pattern;

                return pattern;
            }

            internal DirectionSequenceMessagePopulation Build()
            {
                var lengths = new List<int>();
                var lengthCounts = new Dictionary<int, int>();
                var transitionCounts = new Dictionary<int, int>();
                var runCounts = new Dictionary<int, int>();

                var outgoingOnly = 0;
                var incomingOnly = 0;
                var bothDirections = 0;

                foreach (var date in _symbolsByDate.Keys.Order())
                {
                    var pattern = PatternOn(date);

                    lengths.Add(pattern.Length);
                    Increment(lengthCounts, pattern.Length);
                    Increment(runCounts, DirectionSequenceReference.RunCount(pattern));
                    Increment(
                        transitionCounts, DirectionSequenceReference.TransitionCount(pattern));

                    var outgoing = DirectionSequenceReference.CountOutgoing(pattern);

                    if (outgoing == pattern.Length)
                    {
                        outgoingOnly++;
                    }
                    else if (outgoing == 0)
                    {
                        incomingOnly++;
                    }
                    else
                    {
                        bothDirections++;
                    }
                }

                var multiAttachment = 0;
                var maximum = 0;

                foreach (var count in _attachmentsPerMessage.Values)
                {
                    if (count > 1)
                    {
                        multiAttachment++;
                    }

                    maximum = Math.Max(maximum, count);
                }

                return new DirectionSequenceMessagePopulation
                {
                    WorkspaceUnresolvedAttachmentCount = WorkspaceUnresolvedAttachmentCount,
                    ConversationUnresolvedAttachmentCount = ConversationUnresolvedAttachmentCount,
                    ConsideredAttachmentCount = _considered,

                    // Zero by construction: an attachment whose message names no sender aborts the
                    // census, so a returned census has none. Reported so the reader can see the check
                    // ran rather than infer it from the absence of a failure.
                    UnknownDirectionAttachmentCount = 0,
                    DistinctAttachmentDateCount = _symbolsByDate.Count,
                    OutgoingAttachmentCount = _outgoing,
                    IncomingAttachmentCount = _incoming,
                    OrdinalDistribution = Distribution(_ordinals),
                    MultiAttachmentMessageCount = multiAttachment,
                    MessageWithAttachmentCount = _attachmentsPerMessage.Count,
                    MaximumAttachmentsOnOneMessage = maximum,
                    SequenceLengthDistribution = Distribution(lengthCounts),
                    SequenceLength = Summarise(lengths),
                    TransitionCountDistribution = Distribution(transitionCounts),
                    RunCountDistribution = Distribution(runCounts),
                    OutgoingOnlyDateCount = outgoingOnly,
                    IncomingOnlyDateCount = incomingOnly,
                    BothDirectionDateCount = bothDirections,
                };
            }
        }

        /// <summary>
        /// One pair's two sides, collapsed and counted, before any reference is computed.
        /// </summary>
        /// <remarks>
        /// <see cref="Date"/> is carried so distinct-date counts can be reported per band, and for no
        /// other purpose. It never reaches a census figure or a pair row.
        /// </remarks>
        private sealed class PairShape
        {
            internal required ScopeLevel Scope { get; init; }

            internal required long ScopeKey { get; init; }

            internal required DateOnly Date { get; init; }

            internal required bool[] Pattern { get; init; }

            internal required bool[] EmittedTokens { get; init; }

            internal required int ConflictingPositionCount { get; init; }

            internal required int UnlabelledPositionCount { get; init; }

            internal required int OutgoingCopiesBeforeCollapse { get; init; }

            internal required int IncomingCopiesBeforeCollapse { get; init; }
        }

        // -------------------------------------------------------------------------------------------
        // Accumulators.
        // -------------------------------------------------------------------------------------------

        /// <summary>The spread of one per-pair ratio, banded by exact integer comparison.</summary>
        private sealed class RatioAccumulator
        {
            private readonly List<double> _readings = [];

            private int _negative;
            private int _zero;
            private int _positive;
            private int _atMostFiveHundredths;
            private int _atMostOneQuarter;
            private int _atMostOneHalf;
            private int _atMostNinetyFiveHundredths;
            private int _belowOne;
            private int _exactlyOne;
            private int _aboveOne;

            /// <param name="denominator">Strictly positive; the sign lives in the numerator.</param>
            internal void Add(BigInteger numerator, BigInteger denominator)
            {
                _readings.Add(DecimalReading(numerator, denominator));

                if (numerator.IsZero)
                {
                    _zero++;

                    return;
                }

                if (numerator.Sign < 0)
                {
                    _negative++;
                }
                else
                {
                    _positive++;
                }

                var magnitude = BigInteger.Abs(numerator);

                if (magnitude * 20 <= denominator)
                {
                    _atMostFiveHundredths++;
                }
                else if (magnitude * 4 <= denominator)
                {
                    _atMostOneQuarter++;
                }
                else if (magnitude * 2 <= denominator)
                {
                    _atMostOneHalf++;
                }
                else if (magnitude * 20 <= denominator * 19)
                {
                    _atMostNinetyFiveHundredths++;
                }
                else if (magnitude < denominator)
                {
                    _belowOne++;
                }
                else if (magnitude == denominator)
                {
                    _exactlyOne++;
                }
                else
                {
                    _aboveOne++;
                }
            }

            internal DirectionSequenceRatioSummary Build()
            {
                var ordered = _readings.ToList();
                ordered.Sort();

                return new DirectionSequenceRatioSummary
                {
                    Population = ordered.Count,
                    Minimum = ordered.Count == 0 ? 0d : ordered[0],
                    Median = ordered.Count == 0 ? 0d : ordered[(ordered.Count - 1) / 2],
                    Maximum = ordered.Count == 0 ? 0d : ordered[^1],
                    Negative = _negative,
                    Zero = _zero,
                    Positive = _positive,
                    MagnitudeAtMostFiveHundredths = _atMostFiveHundredths,
                    MagnitudeAtMostOneQuarter = _atMostOneQuarter,
                    MagnitudeAtMostOneHalf = _atMostOneHalf,
                    MagnitudeAtMostNinetyFiveHundredths = _atMostNinetyFiveHundredths,
                    MagnitudeBelowOne = _belowOne,
                    MagnitudeExactlyOne = _exactlyOne,
                    MagnitudeAboveOne = _aboveOne,
                };
            }
        }

        /// <summary>The spread of one per-pair signed difference measured in whole units.</summary>
        private sealed class DifferenceAccumulator
        {
            private readonly List<double> _values = [];

            private int _negative;
            private int _zero;
            private int _positive;
            private int _atMostOne;
            private int _atMostTwo;
            private int _atMostFive;
            private int _atMostTen;
            private int _atMostTwentyFive;
            private int _aboveTwentyFive;

            internal void Add(double value)
            {
                _values.Add(value);

                if (value < 0d)
                {
                    _negative++;
                }
                else if (value > 0d)
                {
                    _positive++;
                }
                else
                {
                    _zero++;
                }

                var magnitude = Math.Abs(value);

                if (magnitude <= 1d)
                {
                    _atMostOne++;
                }
                else if (magnitude <= 2d)
                {
                    _atMostTwo++;
                }
                else if (magnitude <= 5d)
                {
                    _atMostFive++;
                }
                else if (magnitude <= 10d)
                {
                    _atMostTen++;
                }
                else if (magnitude <= 25d)
                {
                    _atMostTwentyFive++;
                }
                else
                {
                    _aboveTwentyFive++;
                }
            }

            internal DirectionSequenceDifferenceSummary Build()
            {
                var ordered = _values.ToList();
                ordered.Sort();

                return new DirectionSequenceDifferenceSummary
                {
                    Population = ordered.Count,
                    Minimum = ordered.Count == 0 ? 0d : ordered[0],
                    Median = ordered.Count == 0 ? 0d : ordered[(ordered.Count - 1) / 2],
                    Maximum = ordered.Count == 0 ? 0d : ordered[^1],
                    Negative = _negative,
                    Zero = _zero,
                    Positive = _positive,
                    MagnitudeAtMostOne = _atMostOne,
                    MagnitudeAtMostTwo = _atMostTwo,
                    MagnitudeAtMostFive = _atMostFive,
                    MagnitudeAtMostTen = _atMostTen,
                    MagnitudeAtMostTwentyFive = _atMostTwentyFive,
                    MagnitudeAboveTwentyFive = _aboveTwentyFive,
                };
            }
        }

        /// <summary>Supply adequacy at one stage of collapse.</summary>
        private sealed class SupplyAccumulator
        {
            private readonly Dictionary<int, int> _outgoing = [];
            private readonly Dictionary<int, int> _incoming = [];

            private int _population;
            private int _sufficient;
            private int _insufficient;
            private int _observationsInInsufficient;

            internal void Add(int outgoingShortfall, int incomingShortfall, int messageSymbols)
            {
                _population++;

                Increment(_outgoing, outgoingShortfall);
                Increment(_incoming, incomingShortfall);

                if (outgoingShortfall == 0 && incomingShortfall == 0)
                {
                    _sufficient++;

                    return;
                }

                _insufficient++;
                _observationsInInsufficient += messageSymbols;
            }

            internal DirectionSequenceSupplyCounts Build() =>
                new()
                {
                    Population = _population,
                    SupplySufficientPairCount = _sufficient,
                    SupplyInsufficientPairCount = _insufficient,
                    OutgoingShortfallDistribution = Distribution(_outgoing),
                    IncomingShortfallDistribution = Distribution(_incoming),
                    MessageObservationsInSupplyInsufficientPairs = _observationsInInsufficient,
                };
        }

        /// <summary>What one descriptive band is made of.</summary>
        private sealed class BandAccumulator
        {
            internal int PairCount { get; set; }

            internal int MessageObservations { get; set; }

            internal int TokenPositions { get; set; }

            internal List<int> MessageLengths { get; } = [];

            internal Dictionary<int, int> TransitionCounts { get; } = [];

            internal HashSet<long> ScopeKeys { get; } = [];

            internal HashSet<DateOnly> Dates { get; } = [];
        }

        /// <summary>
        /// One scope's running census: the pair universe, the exclusions, supply, burstiness, the exact
        /// reference expectations and both determinacy classifications.
        /// </summary>
        /// <remarks>
        /// Pair states are a precedence rather than a set of filters — a pair with no message symbols is
        /// not asked about conflict, a conflicted pair is not asked about supply, and supply is asked
        /// before capacity. The degenerate classes are tallied independently of that precedence, because
        /// a pair can be both supply-insufficient and degenerate and both facts matter.
        /// </remarks>
        private sealed class ScopeAccumulator
        {
            private readonly ScopeLevel _scope;
            private readonly int _scopeKeyCount;

            private readonly List<int> _dilutionMessageSymbols = [];
            private readonly Dictionary<int, int> _dilutionMessageSymbolCounts = [];
            private readonly List<int> _dilutionTokenPositions = [];
            private readonly RatioAccumulator _conversationShare = new();

            private readonly SupplyAccumulator _beforeCollapse = new();
            private readonly SupplyAccumulator _afterCollapse = new();

            private readonly List<int> _tokenRunCounts = [];
            private readonly DifferenceAccumulator _runCountDifference = new();
            private readonly Dictionary<int, int> _messageRunCounts = [];
            private readonly Dictionary<int, int> _messageTransitionCounts = [];

            private readonly Dictionary<DirectionSequenceQrBand, BandAccumulator> _bands = [];
            private readonly RatioAccumulator _exchangeableLessConditional = new();

            private int _pairCount;
            private int _pairsWithMessageSymbols;
            private int _pairsWithTokenPositions;
            private int _conflictingPositions;
            private int _excludedPairs;
            private int _messageObservationsLostToConflict;
            private int _supplyInsufficientPairs;
            private int _classifiedPairs;
            private int _classifiedMessageObservations;

            private int _noTokenPositions;
            private int _noMessageSymbols;
            private int _singleMessageSymbol;
            private int _noOutgoingTokens;
            private int _noIncomingTokens;
            private int _singleArrangement;
            private int _degeneratePairs;
            private int _degenerateMessageObservations;

            private int _pairsBecomingInsufficient;
            private int _pairsBecomingSufficient;
            private int _messageObservationsLostToCollapse;

            private int _notOrderInformative;
            private int _weaklyOrderInformative;
            private int _strictlyOrderInformative;

            private double _sumOfConditional;
            private double _sumOfConditionalOverInformative;
            private double _sumOfExpectedShare;
            private double _sumOfExpectedShareOverInformative;
            private int _exchangeableInformative;
            private int _exchangeableDeterminate;
            private int _informativeLostToRunConditioning;

            private int _binaryDeterminate;
            private int _binaryInformative;
            private int _binaryDeterminateObservations;
            private int _binaryInformativeObservations;
            private int _gradedDeterminate;
            private int _gradedInformative;
            private int _gradedDeterminateObservations;
            private int _gradedInformativeObservations;
            private int _informativeAndInformative;
            private int _determinateAndInformative;
            private int _informativeAndDeterminate;
            private int _determinateAndDeterminate;

            internal ScopeAccumulator(ScopeLevel scope, int scopeKeyCount)
            {
                _scope = scope;
                _scopeKeyCount = scopeKeyCount;

                foreach (var band in AllBands)
                {
                    _bands[band] = new BandAccumulator();
                }
            }

            /// <summary>
            /// Measures one pair, and returns its anonymised row where it carries message symbols.
            /// </summary>
            internal DirectionSequencePairRow? Add(PairShape shape, ref int pairIdentifier)
            {
                _pairCount++;
                _conflictingPositions += shape.ConflictingPositionCount;

                var messageSymbols = shape.Pattern.Length;

                var messageOutgoing = DirectionSequenceReference.CountOutgoing(shape.Pattern);
                var messageIncoming = messageSymbols - messageOutgoing;

                var tokenPositions = shape.EmittedTokens.Length;

                var tokenOutgoing = DirectionSequenceReference.CountOutgoing(shape.EmittedTokens);
                var tokenIncoming = tokenPositions - tokenOutgoing;
                var tokenRuns = DirectionSequenceReference.RunCount(shape.EmittedTokens);

                if (messageSymbols > 0)
                {
                    _pairsWithMessageSymbols++;
                }

                if (tokenPositions > 0)
                {
                    _pairsWithTokenPositions++;
                }

                if (messageSymbols == 0)
                {
                    _noMessageSymbols++;

                    return null;
                }

                pairIdentifier++;

                var row = new PairRowBuilder
                {
                    PairID = pairIdentifier,
                    Shape = shape,
                    MessageOutgoing = messageOutgoing,
                    MessageIncoming = messageIncoming,
                    TokenOutgoing = tokenOutgoing,
                    TokenIncoming = tokenIncoming,
                    TokenRuns = tokenRuns,
                };

                if (shape.ConflictingPositionCount > 0)
                {
                    _excludedPairs++;
                    _messageObservationsLostToConflict += messageSymbols;

                    return row.Build(DirectionSequencePairState.ExcludedByDirectionConflict);
                }

                var arrangements = tokenPositions == 0
                    ? BigInteger.Zero
                    : DirectionSequenceReference.ArrangementCount(
                        tokenOutgoing, tokenIncoming, tokenRuns);

                row.ArrangementCount = arrangements;

                var degenerate = TallyDegenerate(
                    messageSymbols, tokenPositions, tokenOutgoing, tokenIncoming, arrangements);

                if (degenerate)
                {
                    _degeneratePairs++;
                    _degenerateMessageObservations += messageSymbols;
                }

                if (tokenPositions > 0)
                {
                    _dilutionMessageSymbols.Add(messageSymbols);
                    Increment(_dilutionMessageSymbolCounts, messageSymbols);
                    _dilutionTokenPositions.Add(tokenPositions);
                    _conversationShare.Add(messageSymbols, tokenPositions);
                }

                var supplySufficient = TallySupply(
                    shape, messageSymbols, messageOutgoing, messageIncoming,
                    tokenOutgoing, tokenIncoming, row);

                if (tokenPositions == 0)
                {
                    return row.Build(DirectionSequencePairState.NoTokenPositions);
                }

                if (!supplySufficient)
                {
                    _supplyInsufficientPairs++;

                    return row.Build(DirectionSequencePairState.SupplyInsufficient);
                }

                if (degenerate)
                {
                    return row.Build(DirectionSequencePairState.Degenerate);
                }

                Classify(shape, messageSymbols, tokenPositions, tokenOutgoing, tokenIncoming,
                    tokenRuns, arrangements, row);

                return row.Build(DirectionSequencePairState.Classified);
            }

            /// <remarks>
            /// The classes overlap and are each counted independently. The token-side classes are asked
            /// only where the token side emitted something, because with nothing emitted they would all
            /// be vacuously true and would drown the figures that matter.
            /// </remarks>
            private bool TallyDegenerate(
                int messageSymbols,
                int tokenPositions,
                int tokenOutgoing,
                int tokenIncoming,
                BigInteger arrangements)
            {
                var degenerate = false;

                if (tokenPositions == 0)
                {
                    _noTokenPositions++;
                    degenerate = true;
                }

                if (messageSymbols == 1)
                {
                    _singleMessageSymbol++;
                    degenerate = true;
                }

                if (tokenPositions == 0)
                {
                    return degenerate;
                }

                if (tokenOutgoing == 0)
                {
                    _noOutgoingTokens++;
                    degenerate = true;
                }

                if (tokenIncoming == 0)
                {
                    _noIncomingTokens++;
                    degenerate = true;
                }

                if (arrangements == BigInteger.One)
                {
                    _singleArrangement++;
                    degenerate = true;
                }

                return degenerate;
            }

            private bool TallySupply(
                PairShape shape,
                int messageSymbols,
                int messageOutgoing,
                int messageIncoming,
                int tokenOutgoing,
                int tokenIncoming,
                PairRowBuilder row)
            {
                var beforeOutgoing =
                    Math.Max(0, messageOutgoing - shape.OutgoingCopiesBeforeCollapse);

                var beforeIncoming =
                    Math.Max(0, messageIncoming - shape.IncomingCopiesBeforeCollapse);

                var afterOutgoing = Math.Max(0, messageOutgoing - tokenOutgoing);
                var afterIncoming = Math.Max(0, messageIncoming - tokenIncoming);

                _beforeCollapse.Add(beforeOutgoing, beforeIncoming, messageSymbols);
                _afterCollapse.Add(afterOutgoing, afterIncoming, messageSymbols);

                var beforeSufficient = beforeOutgoing == 0 && beforeIncoming == 0;
                var afterSufficient = afterOutgoing == 0 && afterIncoming == 0;

                if (beforeSufficient && !afterSufficient)
                {
                    _pairsBecomingInsufficient++;
                    _messageObservationsLostToCollapse += messageSymbols;
                }
                else if (!beforeSufficient && afterSufficient)
                {
                    _pairsBecomingSufficient++;
                }

                row.OutgoingShortfall = afterOutgoing;
                row.IncomingShortfall = afterIncoming;

                return afterSufficient;
            }

            /// <summary>
            /// Computes the exact reference quantities for one classified pair and files it under both
            /// determinacy classifications.
            /// </summary>
            /// <remarks>
            /// The two always-true identities are checked here rather than asserted in a report.
            /// <c>A * Q &gt;= P * P</c> is Cauchy–Schwarz and a strict violation can only be an
            /// accumulator defect; a binary-informative pair classified graded-determinate contradicts
            /// the one implication that does hold between the two classifications. Either aborts the
            /// census, because a gate that reported them would be inviting the reader to absorb a
            /// defect.
            /// </remarks>
            private void Classify(
                PairShape shape,
                int messageSymbols,
                int tokenPositions,
                int tokenOutgoing,
                int tokenIncoming,
                int tokenRuns,
                BigInteger arrangements,
                PairRowBuilder row)
            {
                var admitting = DirectionSequenceReference.AdmittingArrangementCount(
                    shape.Pattern, tokenOutgoing, tokenIncoming, tokenRuns);

                var embeddings = DirectionSequenceReference.EmbeddingPairCount(
                    shape.Pattern, tokenOutgoing, tokenIncoming, tokenRuns);

                var squared = DirectionSequenceReference.SquaredEmbeddingCount(
                    shape.Pattern, tokenOutgoing, tokenIncoming, tokenRuns);

                if (arrangements.IsZero)
                {
                    throw new InvalidOperationException(
                        $"A reference class of {tokenOutgoing} outgoing and {tokenIncoming} incoming " +
                        $"token positions in {tokenRuns} runs holds no arrangement, yet a sequence " +
                        $"with exactly those properties was built from the workspace. The reference " +
                        $"and the sequence disagree about what was observed. Nothing has been " +
                        $"censused and the workspace is unchanged.");
                }

                if (arrangements * squared < embeddings * embeddings)
                {
                    throw new InvalidOperationException(
                        $"The exact reference counts for a class of {tokenOutgoing} outgoing and " +
                        $"{tokenIncoming} incoming token positions in {tokenRuns} runs violate " +
                        $"A * Q >= P * P, which holds for every class by Cauchy-Schwarz. One of the " +
                        $"three counts is wrong. Nothing has been censused and the workspace is " +
                        $"unchanged.");
                }

                var binaryDeterminate = admitting.IsZero || admitting == arrangements;
                var gradedDeterminate = arrangements * squared == embeddings * embeddings;

                if (!binaryDeterminate && gradedDeterminate)
                {
                    throw new InvalidOperationException(
                        $"A pair was classified binary-informative and graded-determinate, for a " +
                        $"class of {tokenOutgoing} outgoing and {tokenIncoming} incoming token " +
                        $"positions in {tokenRuns} runs. Graded determinacy implies binary " +
                        $"determinacy, so that combination cannot occur and one of the two " +
                        $"classifications is wrong. Nothing has been censused and the workspace is " +
                        $"unchanged.");
                }

                var exchangeableAdmitting = DirectionSequenceReference.ExchangeableAdmittingCount(
                    shape.Pattern, tokenOutgoing, tokenIncoming);

                var exchangeableClass =
                    DirectionSequenceReference.Binomial(tokenPositions, tokenOutgoing);

                var shareDenominator =
                    arrangements * DirectionSequenceReference.Binomial(tokenPositions, messageSymbols);

                _classifiedPairs++;
                _classifiedMessageObservations += messageSymbols;

                _sumOfConditional += DecimalReading(admitting, arrangements);
                _sumOfExpectedShare += DecimalReading(embeddings, shareDenominator);

                if (!binaryDeterminate)
                {
                    _sumOfConditionalOverInformative += DecimalReading(admitting, arrangements);
                }

                if (!gradedDeterminate)
                {
                    _sumOfExpectedShareOverInformative +=
                        DecimalReading(embeddings, shareDenominator);
                }

                // q(p, o, i) - q_r(p, o, i, r), as one exact rational rather than two divisions.
                _exchangeableLessConditional.Add(
                    (exchangeableAdmitting * arrangements) - (admitting * exchangeableClass),
                    exchangeableClass * arrangements);

                var exchangeableInformative =
                    !exchangeableAdmitting.IsZero && exchangeableAdmitting != exchangeableClass;

                if (exchangeableInformative)
                {
                    _exchangeableInformative++;

                    if (binaryDeterminate)
                    {
                        _informativeLostToRunConditioning++;
                    }
                }
                else
                {
                    _exchangeableDeterminate++;
                }

                TallyDeterminacy(messageSymbols, binaryDeterminate, gradedDeterminate);

                TallyBurstiness(shape, tokenOutgoing, tokenIncoming, tokenRuns);

                TallyBand(
                    shape, messageSymbols, tokenPositions, admitting, arrangements);

                row.AdmittingArrangementCount = admitting;
                row.EmbeddingPairCount = embeddings;
                row.SquaredEmbeddingCount = squared;
                row.ConditionalAdmissionProbability = DecimalReading(admitting, arrangements);
                row.ExpectedEmbeddingShare = DecimalReading(embeddings, shareDenominator);
                row.ExchangeableAdmissionProbability =
                    DecimalReading(exchangeableAdmitting, exchangeableClass);
                row.BinaryClass = binaryDeterminate
                    ? DirectionSequenceDeterminacyClass.Determinate
                    : DirectionSequenceDeterminacyClass.Informative;
                row.GradedClass = gradedDeterminate
                    ? DirectionSequenceDeterminacyClass.Determinate
                    : DirectionSequenceDeterminacyClass.Informative;
            }

            private void TallyDeterminacy(
                int messageSymbols, bool binaryDeterminate, bool gradedDeterminate)
            {
                if (binaryDeterminate)
                {
                    _binaryDeterminate++;
                    _binaryDeterminateObservations += messageSymbols;
                }
                else
                {
                    _binaryInformative++;
                    _binaryInformativeObservations += messageSymbols;
                }

                if (gradedDeterminate)
                {
                    _gradedDeterminate++;
                    _gradedDeterminateObservations += messageSymbols;
                }
                else
                {
                    _gradedInformative++;
                    _gradedInformativeObservations += messageSymbols;
                }

                if (binaryDeterminate && gradedDeterminate)
                {
                    _determinateAndDeterminate++;
                }
                else if (binaryDeterminate)
                {
                    _determinateAndInformative++;
                }
                else if (gradedDeterminate)
                {
                    _informativeAndDeterminate++;
                }
                else
                {
                    _informativeAndInformative++;
                }
            }

            private void TallyBurstiness(
                PairShape shape, int tokenOutgoing, int tokenIncoming, int tokenRuns)
            {
                _tokenRunCounts.Add(tokenRuns);

                _runCountDifference.Add(
                    tokenRuns
                    - DirectionSequenceReference.ExpectedRunCount(tokenOutgoing, tokenIncoming));

                var messageRuns = DirectionSequenceReference.RunCount(shape.Pattern);
                var messageTransitions = DirectionSequenceReference.TransitionCount(shape.Pattern);

                Increment(_messageRunCounts, messageRuns);
                Increment(_messageTransitionCounts, messageTransitions);

                switch (messageTransitions)
                {
                    case 0:
                        _notOrderInformative++;
                        break;

                    case 1:
                        _weaklyOrderInformative++;
                        break;

                    default:
                        _strictlyOrderInformative++;
                        break;
                }
            }

            private void TallyBand(
                PairShape shape,
                int messageSymbols,
                int tokenPositions,
                BigInteger admitting,
                BigInteger arrangements)
            {
                var band = _bands[BandOf(admitting, arrangements)];

                band.PairCount++;
                band.MessageObservations += messageSymbols;
                band.TokenPositions += tokenPositions;
                band.MessageLengths.Add(messageSymbols);
                band.ScopeKeys.Add(shape.ScopeKey);
                band.Dates.Add(shape.Date);

                Increment(
                    band.TransitionCounts,
                    DirectionSequenceReference.TransitionCount(shape.Pattern));
            }

            internal DirectionSequenceScopeCensus Build() =>
                new()
                {
                    Scope = _scope,
                    PairPopulation = new DirectionSequencePairPopulation
                    {
                        ScopeKeyCount = _scopeKeyCount,
                        PairCount = _pairCount,
                        PairsWithMessageSymbols = _pairsWithMessageSymbols,
                        PairsWithTokenPositions = _pairsWithTokenPositions,
                        ConflictingLogicalPositionCount = _conflictingPositions,
                        ExcludedByDirectionConflictPairCount = _excludedPairs,
                        MessageObservationsLostToDirectionConflict =
                            _messageObservationsLostToConflict,
                        SupplyInsufficientPairCount = _supplyInsufficientPairs,
                        Degenerate = new DirectionSequenceDegenerateCounts
                        {
                            NoTokenPositionPairCount = _noTokenPositions,
                            NoMessageSymbolPairCount = _noMessageSymbols,
                            SingleMessageSymbolPairCount = _singleMessageSymbol,
                            NoOutgoingTokenPositionPairCount = _noOutgoingTokens,
                            NoIncomingTokenPositionPairCount = _noIncomingTokens,
                            SingleArrangementPairCount = _singleArrangement,
                            DegeneratePairCount = _degeneratePairs,
                            MessageObservationsInDegeneratePairs = _degenerateMessageObservations,
                        },
                        ClassifiedPairCount = _classifiedPairs,
                        MessageObservationsClassified = _classifiedMessageObservations,
                    },
                    Dilution = new DirectionSequenceDilutionContext
                    {
                        Population = _dilutionMessageSymbols.Count,
                        MessageSymbolDistribution = Distribution(_dilutionMessageSymbolCounts),
                        TokenPositions = Summarise(_dilutionTokenPositions),
                        ConversationShare = _conversationShare.Build(),
                    },
                    Supply = new DirectionSequenceSupplyCensus
                    {
                        BeforeCollapse = _beforeCollapse.Build(),
                        AfterCollapse = _afterCollapse.Build(),
                        PairsBecomingInsufficientAfterCollapse = _pairsBecomingInsufficient,
                        PairsBecomingSufficientAfterCollapse = _pairsBecomingSufficient,
                        MessageObservationsLostToCollapse = _messageObservationsLostToCollapse,
                    },
                    Burstiness = new DirectionSequenceBurstinessCensus
                    {
                        Population = _classifiedPairs,
                        TokenRunCounts = Summarise(_tokenRunCounts),
                        ObservedLessExpectedTokenRunCount = _runCountDifference.Build(),
                        MessageRunCountDistribution = Distribution(_messageRunCounts),
                        MessageTransitionCountDistribution =
                            Distribution(_messageTransitionCounts),
                        NotOrderInformativePairCount = _notOrderInformative,
                        WeaklyOrderInformativePairCount = _weaklyOrderInformative,
                        StrictlyOrderInformativePairCount = _strictlyOrderInformative,
                    },
                    Reference = new DirectionSequenceReferenceCensus
                    {
                        Population = _classifiedPairs,
                        Bands = BuildBandCounts(),
                        BandRows = BuildBandRows(),
                        SumOfConditionalAdmissionProbability = _sumOfConditional,
                        SumOfConditionalAdmissionProbabilityOverInformative =
                            _sumOfConditionalOverInformative,
                        SumOfExpectedEmbeddingShare = _sumOfExpectedShare,
                        SumOfExpectedEmbeddingShareOverInformative =
                            _sumOfExpectedShareOverInformative,
                        ExchangeableLessConditionalAdmission =
                            _exchangeableLessConditional.Build(),
                        InformativeUnderExchangeableReferenceCount = _exchangeableInformative,
                        DeterminateUnderExchangeableReferenceCount = _exchangeableDeterminate,
                        InformativeLostToRunConditioningCount = _informativeLostToRunConditioning,
                    },
                    Determinacy = new DirectionSequenceDeterminacyCensus
                    {
                        Population = _classifiedPairs,
                        BinaryDeterminatePairCount = _binaryDeterminate,
                        BinaryInformativePairCount = _binaryInformative,
                        BinaryDeterminateMessageObservations = _binaryDeterminateObservations,
                        BinaryInformativeMessageObservations = _binaryInformativeObservations,
                        GradedDeterminatePairCount = _gradedDeterminate,
                        GradedInformativePairCount = _gradedInformative,
                        GradedDeterminateMessageObservations = _gradedDeterminateObservations,
                        GradedInformativeMessageObservations = _gradedInformativeObservations,
                        BinaryInformativeAndGradedInformative = _informativeAndInformative,
                        BinaryDeterminateAndGradedInformative = _determinateAndInformative,
                        BinaryInformativeAndGradedDeterminate = _informativeAndDeterminate,
                        BinaryDeterminateAndGradedDeterminate = _determinateAndDeterminate,
                    },
                };

            private DirectionSequenceQrBandCounts BuildBandCounts() =>
                new()
                {
                    Population = _classifiedPairs,
                    ExactlyZero = _bands[DirectionSequenceQrBand.ExactlyZero].PairCount,
                    AboveZeroToFiveHundredths =
                        _bands[DirectionSequenceQrBand.AboveZeroToFiveHundredths].PairCount,
                    AboveFiveHundredthsToOneQuarter =
                        _bands[DirectionSequenceQrBand.AboveFiveHundredthsToOneQuarter].PairCount,
                    AboveOneQuarterToOneHalf =
                        _bands[DirectionSequenceQrBand.AboveOneQuarterToOneHalf].PairCount,
                    AboveOneHalfToThreeQuarters =
                        _bands[DirectionSequenceQrBand.AboveOneHalfToThreeQuarters].PairCount,
                    AboveThreeQuartersToNinetyFiveHundredths =
                        _bands[DirectionSequenceQrBand.AboveThreeQuartersToNinetyFiveHundredths]
                            .PairCount,
                    AboveNinetyFiveHundredthsBelowOne =
                        _bands[DirectionSequenceQrBand.AboveNinetyFiveHundredthsBelowOne].PairCount,
                    ExactlyOne = _bands[DirectionSequenceQrBand.ExactlyOne].PairCount,
                };

            private List<DirectionSequenceQrBandRow> BuildBandRows()
            {
                var rows = new List<DirectionSequenceQrBandRow>();

                foreach (var band in AllBands)
                {
                    var accumulated = _bands[band];

                    rows.Add(
                        new DirectionSequenceQrBandRow
                        {
                            Band = band,
                            PairCount = accumulated.PairCount,
                            MessageObservationCount = accumulated.MessageObservations,
                            TokenPositionCount = accumulated.TokenPositions,
                            MessageSequenceLength = Summarise(accumulated.MessageLengths),
                            TransitionCountDistribution =
                                Distribution(accumulated.TransitionCounts),
                            DistinctScopeKeyCount = accumulated.ScopeKeys.Count,
                            DistinctDateCount = accumulated.Dates.Count,
                        });
                }

                return rows;
            }
        }

        /// <summary>
        /// Collects one pair's anonymised row as the measurement proceeds.
        /// </summary>
        /// <remarks>
        /// A builder rather than a long constructor call, because most pairs stop before most of the
        /// figures exist and a partly filled row must still be well defined. Absent quantities stay at
        /// zero, and the row's state says which those are.
        /// </remarks>
        private sealed class PairRowBuilder
        {
            internal required int PairID { get; init; }

            internal required PairShape Shape { get; init; }

            internal required int MessageOutgoing { get; init; }

            internal required int MessageIncoming { get; init; }

            internal required int TokenOutgoing { get; init; }

            internal required int TokenIncoming { get; init; }

            internal required int TokenRuns { get; init; }

            internal int OutgoingShortfall { get; set; }

            internal int IncomingShortfall { get; set; }

            internal BigInteger ArrangementCount { get; set; }

            internal BigInteger AdmittingArrangementCount { get; set; }

            internal BigInteger EmbeddingPairCount { get; set; }

            internal BigInteger SquaredEmbeddingCount { get; set; }

            internal double ConditionalAdmissionProbability { get; set; }

            internal double ExpectedEmbeddingShare { get; set; }

            internal double ExchangeableAdmissionProbability { get; set; }

            internal DirectionSequenceDeterminacyClass BinaryClass { get; set; }

            internal DirectionSequenceDeterminacyClass GradedClass { get; set; }

            internal DirectionSequencePairRow Build(DirectionSequencePairState state) =>
                new()
                {
                    PairID = PairID,
                    Scope = Shape.Scope,
                    ScopeKeyID = Shape.ScopeKey,
                    State = state,
                    MessageSymbolCount = Shape.Pattern.Length,
                    MessageOutgoingCount = MessageOutgoing,
                    MessageIncomingCount = MessageIncoming,
                    MessageRunCount = DirectionSequenceReference.RunCount(Shape.Pattern),
                    MessageTransitionCount =
                        DirectionSequenceReference.TransitionCount(Shape.Pattern),
                    TokenPositionCount = Shape.EmittedTokens.Length,
                    TokenOutgoingCount = TokenOutgoing,
                    TokenIncomingCount = TokenIncoming,
                    TokenRunCount = TokenRuns,
                    TokenOutgoingCountBeforeCollapse = Shape.OutgoingCopiesBeforeCollapse,
                    TokenIncomingCountBeforeCollapse = Shape.IncomingCopiesBeforeCollapse,
                    OutgoingShortfall = OutgoingShortfall,
                    IncomingShortfall = IncomingShortfall,
                    ArrangementCount = ArrangementCount,
                    AdmittingArrangementCount = AdmittingArrangementCount,
                    EmbeddingPairCount = EmbeddingPairCount,
                    SquaredEmbeddingCount = SquaredEmbeddingCount,
                    ConditionalAdmissionProbability = ConditionalAdmissionProbability,
                    ExpectedEmbeddingShare = ExpectedEmbeddingShare,
                    ExchangeableAdmissionProbability = ExchangeableAdmissionProbability,
                    BinaryClass = BinaryClass,
                    GradedClass = GradedClass,
                };
        }
    }
}
