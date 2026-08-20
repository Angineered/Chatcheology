using System.Globalization;
using System.Numerics;
using Chatcheology.Data.Matching;
using Chatcheology.Data.Media;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Measures the candidate space a strict within-direction sequence order would leave, and how much
    /// of the frozen candidate ambiguity it would remove.
    /// </summary>
    /// <remarks>
    /// Conditional throughout on one untested assumption: that within a <c>(date, direction)</c> group,
    /// message source order corresponds to strictly increasing recovered WA token order. Stage B2A was
    /// the stage that would have tested it and terminated with insufficient power. So this census
    /// measures a consequence, never a confirmation.
    /// <para>
    /// The cohort comes from the frozen <see cref="WorkspaceMatchingService"/>, whose candidates depend
    /// on the message's date and direction alone — which is what makes a group the unit of analysis:
    /// every message in one group draws on the identical compatible candidate set.
    /// </para>
    /// <para>
    /// Two injectivity properties hold, and they differ. Token positions are assigned
    /// <em>injectively</em>: one message, one distinct position, strictly increasing. Assets are
    /// assigned <em>non-injectively</em>: one payload may serve several messages, but only where distinct
    /// supported <c>(asset, token)</c> occurrences exist at the selected positions. The second property
    /// is what makes <c>T &gt;= M</c> sufficient and the per-message candidate union exact.
    /// </para>
    /// <para>
    /// Nothing here resolves an attachment, ranks a candidate, applies a threshold or writes anything.
    /// </para>
    /// </remarks>
    public sealed class WithinDirectionAssignmentCensusService
    {
        /// <summary>The approved grammar's width: exactly four ASCII digits after <c>-WA</c>.</summary>
        private const int SupportedTokenLength = 4;

        private const byte SentCopy = 1;
        private const byte NotUnderSentCopy = 2;
        private const byte UnknownDirectionCopy = 4;

        /// <summary>
        /// Runs the census against one workspace.
        /// </summary>
        /// <exception cref="FileNotFoundException">There is no workspace at the requested path.</exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not one this census can read, or its media contradicts the frozen candidate
        /// rules. Nothing is repaired and nothing is skipped.
        /// </exception>
        /// <exception cref="OperationCanceledException">The token was signalled.</exception>
        public WithinDirectionAssignmentCensus Analyse(
            WithinDirectionAssignmentCensusRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);

            var collector = new GroupCollector();

            var matchingCensus = new WorkspaceMatchingService().Analyse(
                request.DatabasePath,
                new MatchAnalysisRequest(request.ConversationID, request.LocalParticipantID),
                collector.Add,
                cancellationToken);

            var groups = collector.BuildGroups();
            var evidence = ReadTokenEvidence(request.DatabasePath, groups, cancellationToken);

            return Build(
                request,
                matchingCensus,
                collector.ExcludedAdjacentDateOnlyAttachmentCount,
                groups,
                evidence,
                cancellationToken);
        }

        // -------------------------------------------------------------------------------------------
        // The sequence-evidence pass.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads every media row once, keeping the supported tokens of the candidate assets the groups
        /// name, with the direction evidence of the copies carrying them.
        /// </summary>
        /// <remarks>
        /// Wanted keys are <c>(asset, date)</c> rather than <c>(asset, date, direction)</c>: a token is a
        /// property of a name, not of a direction, so one key serves both direction groups on a date.
        /// <para>
        /// <c>FileName</c> and <c>Extension</c> are read here and nowhere else, and only to recover the
        /// four approved digits. No path, prefix or source identity takes part.
        /// </para>
        /// </remarks>
        private static TokenEvidence ReadTokenEvidence(
            string databasePath,
            IReadOnlyList<CandidateGroup> groups,
            CancellationToken cancellationToken)
        {
            var evidence = new TokenEvidence(groups);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(databasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(
                connection, "a within-direction assignment census");

            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    f.MediaFileID,
                    f.FileName,
                    f.Extension,
                    f.FileDate,
                    f.IsSent,
                    f.SHA256,
                    l.MediaAssetID,
                    a.SHA256,
                    a.SizeBytes
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

                if (reader.IsDBNull(3))
                {
                    // Undated rows carry no date to join on. Stage B1 already established archive-wide
                    // that an undated name holds no marker either, and re-deriving that here would need
                    // source-type semantics this census has no other use for.
                    continue;
                }

                ReadDatedRow(reader, mediaFileID, evidence);
            }

            evidence.RequireEveryCandidateObserved();

            return evidence;
        }

        private static void ReadDatedRow(
            SqliteDataReader reader, long mediaFileID, TokenEvidence evidence)
        {
            var fileDate = ReadFileDate(reader, mediaFileID);
            var fileName = reader.GetString(1);
            var extension = reader.IsDBNull(2) ? null : reader.GetString(2);
            var isSent = reader.IsDBNull(4) ? null : (bool?)(reader.GetInt64(4) != 0);
            var mediaAssetID = reader.GetInt64(6);
            var assetSizeBytes = reader.GetInt64(8);

            RequireMarkerAgreeingWithFileDate(mediaFileID, fileName, fileDate, out var suffixStart);

            if (!evidence.IsWanted(mediaAssetID, fileDate))
            {
                return;
            }

            if (assetSizeBytes == 0)
            {
                // A payload with no bytes is never a candidate, so a wanted key reaching one means this
                // census and the frozen analysis disagree about the eligible population.
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} supports a compatible candidate relation but its " +
                    $"MediaAsset holds no payload. The frozen analysis excludes zero-byte assets from " +
                    $"candidacy. Nothing has been censused and the workspace is unchanged.");
            }

            var direction = isSent switch
            {
                true => SentCopy,
                false => NotUnderSentCopy,
                null => UnknownDirectionCopy,
            };

            evidence.RecordQualifyingCopy(mediaAssetID, fileDate);

            if (ReadSupportedToken(fileName, extension, suffixStart) is { } token)
            {
                evidence.RecordToken(mediaAssetID, fileDate, token, direction);
            }
        }

        /// <summary>
        /// The approved four-digit token, or null when the name's suffix is not that shape.
        /// </summary>
        /// <remarks>
        /// The committed Stage A / Stage B1 rule, character for character: the whole remainder after
        /// <c>-WA</c>, with the recorded extension removed only when the name really ends with it, must
        /// be exactly four ASCII digits. Stated here rather than shared, because extracting a helper
        /// would mean editing a frozen path whose real-run evidence is preserved; the grammar-equivalence
        /// tests are what contain the drift.
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

        // -------------------------------------------------------------------------------------------
        // Aggregation.
        // -------------------------------------------------------------------------------------------

        private static WithinDirectionAssignmentCensus Build(
            WithinDirectionAssignmentCensusRequest request,
            MatchAnalysisCensus matchingCensus,
            int excludedAdjacentDateOnly,
            IReadOnlyList<CandidateGroup> groups,
            TokenEvidence evidence,
            CancellationToken cancellationToken)
        {
            var primary = new PrimaryAccumulator();
            var sensitivity = new SensitivityAccumulator();

            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var positions = evidence.BuildPositions(group, agreeingOnly: false);
                var metrics = Measure(group.MessageCount, group.CompatibleAssets.Length, positions);

                primary.Add(group, metrics);

                var agreeingPositions = evidence.BuildPositions(group, agreeingOnly: true);

                var agreeingAssets = DistinctAssetCount(agreeingPositions);

                var agreeingMetrics = Measure(
                    group.MessageCount, agreeingAssets, agreeingPositions);

                sensitivity.Add(group, metrics, agreeingMetrics, positions, agreeingPositions);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return primary.Build(request, matchingCensus, excludedAdjacentDateOnly, sensitivity);
        }

        /// <summary>
        /// Everything one group's token positions imply under the sequence-order hypothesis.
        /// </summary>
        /// <remarks>
        /// Feasibility is <c>T &gt;= M</c>, and that is exact rather than an approximation: `M` strictly
        /// increasing indexes require at least `M` positions, and taking the first `M` always works
        /// because every position holds at least one asset and nothing couples one message's choice to
        /// another's.
        /// <para>
        /// The per-rank candidate union comes from a sliding window of width <c>T - M + 1</c>, which is
        /// exactly the range <c>r .. T - M + r</c> — every index in it is attainable, so the union is the
        /// exact set of assets that can serve that message in at least one full valid assignment.
        /// </para>
        /// </remarks>
        private static GroupMetrics Measure(
            int messageCount, int baselineAssetCount, List<long[]> positions)
        {
            var tokenPositionCount = positions.Count;
            var occurrenceCount = positions.Sum(assets => assets.Length);
            var heavyPositions = positions.Count(assets => assets.Length > 1);

            var metrics = new GroupMetrics
            {
                MessageCount = messageCount,
                BaselineAssetCount = baselineAssetCount,
                TokenPositionCount = tokenPositionCount,
                OccurrenceCount = occurrenceCount,
                PositionsWithSeveralAssets = heavyPositions,
                Classification = Classify(messageCount, baselineAssetCount, tokenPositionCount),
            };

            DescribeMultiplicity(positions, metrics);

            if (metrics.Classification != FeasibilityClassification.EnoughTokenPositions)
            {
                return metrics;
            }

            metrics.WeightedAssignmentCount = CountAssignments(positions, messageCount);
            metrics.UnweightedChoiceCount = Choose(tokenPositionCount, messageCount);

            MeasureRanges(positions, messageCount, metrics);

            return metrics;
        }

        private static FeasibilityClassification Classify(
            int messageCount, int baselineAssetCount, int tokenPositionCount) =>
            baselineAssetCount == 0
                ? FeasibilityClassification.NoCompatibleCandidateAsset
                : tokenPositionCount == 0
                    ? FeasibilityClassification.NoSupportedTokenPosition
                    : tokenPositionCount < messageCount
                        ? FeasibilityClassification.TooFewTokenPositions
                        : FeasibilityClassification.EnoughTokenPositions;

        /// <summary>
        /// The exact number of complete message-to-token/asset assignments.
        /// </summary>
        /// <remarks>
        /// The sum, over every increasing choice of <c>M</c> token positions, of the product of their
        /// weights. Descending <c>j</c> stops one position being consumed twice. No assignment is
        /// materialised, and <see cref="BigInteger"/> keeps the count exact rather than approximate — a
        /// floating-point total would quietly lose the exactness the digit-length figures imply.
        /// </remarks>
        private static BigInteger CountAssignments(List<long[]> positions, int messageCount)
        {
            var dp = new BigInteger[messageCount + 1];
            dp[0] = BigInteger.One;

            var processed = 0;

            foreach (var assets in positions)
            {
                processed++;

                for (var j = Math.Min(processed, messageCount); j >= 1; j--)
                {
                    dp[j] += dp[j - 1] * assets.Length;
                }
            }

            return dp[messageCount];
        }

        /// <summary>The unweighted position-choice count, for the multi-asset comparison.</summary>
        private static BigInteger Choose(int n, int k)
        {
            if (k < 0 || k > n)
            {
                return BigInteger.Zero;
            }

            var result = BigInteger.One;

            for (var i = 1; i <= k; i++)
            {
                result = result * (n - k + i) / i;
            }

            return result;
        }

        /// <summary>
        /// Walks the per-rank window once, recording the candidate union, whether the window holds a
        /// multi-asset position, and the forced-position facts for a slack-zero group.
        /// </summary>
        private static void MeasureRanges(
            List<long[]> positions, int messageCount, GroupMetrics metrics)
        {
            var width = positions.Count - messageCount + 1;

            var window = new Dictionary<long, int>();
            var heavyInWindow = 0;

            for (var index = 0; index < width; index++)
            {
                AddPosition(window, ref heavyInWindow, positions[index]);
            }

            metrics.UnionCounts = new int[messageCount];
            metrics.RangeHoldsSeveralAssetPosition = new bool[messageCount];

            for (var rank = 0; rank < messageCount; rank++)
            {
                metrics.UnionCounts[rank] = window.Count;
                metrics.RangeHoldsSeveralAssetPosition[rank] = heavyInWindow > 0;

                if (rank + 1 >= messageCount)
                {
                    break;
                }

                RemovePosition(window, ref heavyInWindow, positions[rank]);
                AddPosition(window, ref heavyInWindow, positions[rank + width]);
            }
        }

        private static void AddPosition(
            Dictionary<long, int> window, ref int heavyInWindow, long[] assets)
        {
            foreach (var asset in assets)
            {
                window[asset] = window.TryGetValue(asset, out var count) ? count + 1 : 1;
            }

            if (assets.Length > 1)
            {
                heavyInWindow++;
            }
        }

        private static void RemovePosition(
            Dictionary<long, int> window, ref int heavyInWindow, long[] assets)
        {
            foreach (var asset in assets)
            {
                var remaining = window[asset] - 1;

                if (remaining == 0)
                {
                    window.Remove(asset);
                }
                else
                {
                    window[asset] = remaining;
                }
            }

            if (assets.Length > 1)
            {
                heavyInWindow--;
            }
        }

        /// <summary>How many token positions each candidate asset occupies in this group.</summary>
        private static void DescribeMultiplicity(List<long[]> positions, GroupMetrics metrics)
        {
            var perAsset = new Dictionary<long, int>();

            foreach (var assets in positions)
            {
                foreach (var asset in assets)
                {
                    perAsset[asset] = perAsset.TryGetValue(asset, out var count) ? count + 1 : 1;
                }
            }

            foreach (var count in perAsset.Values)
            {
                if (count == 1)
                {
                    metrics.AssetsAtOneTokenPosition++;
                }
                else
                {
                    metrics.AssetsAtSeveralTokenPositions++;
                }

                metrics.MaximumTokenPositionsForOneAsset =
                    Math.Max(metrics.MaximumTokenPositionsForOneAsset, count);
            }
        }

        private static int DistinctAssetCount(List<long[]> positions)
        {
            var assets = new HashSet<long>();

            foreach (var position in positions)
            {
                foreach (var asset in position)
                {
                    assets.Add(asset);
                }
            }

            return assets.Count;
        }

        // -------------------------------------------------------------------------------------------
        // Media-row validation. A third statement of the completed-Phase-5 rules, deliberately: sharing
        // one helper would mean editing a frozen path to serve this census.
        // -------------------------------------------------------------------------------------------

        private static void RequireSingleAssetLink(long mediaFileID, long previousMediaFileID)
        {
            if (mediaFileID != previousMediaFileID)
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} is linked to more than one MediaAsset. One physical file " +
                $"carries one payload, and the workspace's own unique constraint says so. Nothing has " +
                $"been censused and the workspace is unchanged.");
        }

        private static void RequireHashedFile(SqliteDataReader reader, long mediaFileID)
        {
            if (!reader.IsDBNull(5))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has no SHA-256, so media hashing is incomplete and Phase 5 " +
                $"has not finished for this workspace. Nothing has been censused and the workspace is " +
                $"unchanged.");
        }

        private static void RequireAssetLink(SqliteDataReader reader, long mediaFileID)
        {
            if (reader.IsDBNull(6))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is hashed but linked to no MediaAsset, so deduplication " +
                    $"is incomplete for this workspace. Nothing has been censused and the workspace is " +
                    $"unchanged.");
            }

            if (reader.IsDBNull(7))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is linked to a MediaAsset that does not exist. The " +
                    $"workspace's foreign keys forbid this, so it was written with enforcement " +
                    $"disabled. Nothing has been censused and the workspace is unchanged.");
            }
        }

        /// <remarks>
        /// Compared case-insensitively because both hash columns are declared <c>COLLATE NOCASE</c>.
        /// </remarks>
        private static void RequireMatchingHashes(SqliteDataReader reader, long mediaFileID)
        {
            if (string.Equals(
                reader.GetString(5), reader.GetString(7), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} records a different SHA-256 from the MediaAsset it is linked " +
                $"to, so the file's content identity and its deduplication disagree. Nothing has been " +
                $"censused and the workspace is unchanged.");
        }

        private static DateOnly ReadFileDate(SqliteDataReader reader, long mediaFileID)
        {
            if (WorkspaceDateFormats.TryParseFileDate(reader.GetString(3), out var fileDate))
            {
                return fileDate;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has a FileDate that is not the calendar date format this " +
                $"workspace writes. It is not guessed at under another format. Nothing has been " +
                $"censused and the workspace is unchanged.");
        }

        private static void RequireMarkerAgreeingWithFileDate(
            long mediaFileID, string fileName, DateOnly fileDate, out int suffixStart)
        {
            if (!WhatsAppNameMarker.TryLocate(fileName, out suffixStart, out var markerDate))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} carries a FileDate but no locatable -YYYYMMDD-WA marker, " +
                    $"so this census and the committed classifier disagree about which characters are a " +
                    $"date. Nothing has been censused and the workspace is unchanged.");
            }

            if (markerDate != fileDate)
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} records a FileDate that is not the date its own name " +
                    $"encodes. The token is read from the name and the date is joined from the column, " +
                    $"so a workspace where the two disagree cannot be censused. Nothing has been " +
                    $"censused and the workspace is unchanged.");
            }
        }

        // -------------------------------------------------------------------------------------------
        // Working state.
        // -------------------------------------------------------------------------------------------

        private enum FeasibilityClassification
        {
            NoCompatibleCandidateAsset,
            NoSupportedTokenPosition,
            TooFewTokenPositions,
            EnoughTokenPositions,
        }

        /// <summary>One <c>(date, direction)</c> group and the candidate set its messages share.</summary>
        private sealed class CandidateGroup
        {
            internal required DateOnly Date { get; init; }

            internal required MessageDirection Direction { get; init; }

            /// <summary>The frozen compatible candidate assets, in <c>MediaAssetID</c> order.</summary>
            internal required long[] CompatibleAssets { get; init; }

            /// <summary>An order-sensitive fold over that set, for the per-relation assertion.</summary>
            internal required long Fingerprint { get; init; }

            internal int MessageCount { get; set; }

            internal int BaselineRelationCount => MessageCount * CompatibleAssets.Length;
        }

        /// <summary>Collects the groups from the frozen analysis as it streams.</summary>
        private sealed class GroupCollector
        {
            private readonly Dictionary<(DateOnly, MessageDirection), CandidateGroup> _groups = [];

            internal int ExcludedAdjacentDateOnlyAttachmentCount { get; private set; }

            internal void Add(AttachmentMatchAnalysis analysis)
            {
                if (analysis.ExactDateCandidates.Count == 0)
                {
                    // No exact-date evidence at all. Reported, never moved onto a neighbouring date.
                    ExcludedAdjacentDateOnlyAttachmentCount++;

                    return;
                }

                if (analysis.MessageDirection == MessageDirection.Unknown)
                {
                    throw new InvalidOperationException(
                        "An attachment with exact-date candidates has an unknown message direction. " +
                        "Direction decides the whole group model, and the preserved first pass records " +
                        "no such attachment. Nothing has been censused and the workspace is unchanged.");
                }

                var compatible = analysis.ExactDateCandidates
                    .Where(
                        candidate =>
                            candidate.DirectionCompatibility == DirectionCompatibility.Compatible)
                    .Select(candidate => candidate.MediaAssetID)
                    .ToArray();

                if (compatible.Length != analysis.ExactDateDirectionCompatibleCandidateCount)
                {
                    throw new InvalidOperationException(
                        $"The frozen analysis reports " +
                        $"{analysis.ExactDateDirectionCompatibleCandidateCount} direction-compatible " +
                        $"exact-date candidates while listing {compatible.Length}. The count and the " +
                        $"list come from one pass. Nothing has been censused and the workspace is " +
                        $"unchanged.");
                }

                var fingerprint = Fold(compatible);
                var key = (analysis.MessageDate, analysis.MessageDirection);

                if (!_groups.TryGetValue(key, out var group))
                {
                    group = new CandidateGroup
                    {
                        Date = analysis.MessageDate,
                        Direction = analysis.MessageDirection,
                        CompatibleAssets = compatible,
                        Fingerprint = fingerprint,
                    };

                    _groups.Add(key, group);
                }
                else if (group.Fingerprint != fingerprint
                    || group.CompatibleAssets.Length != compatible.Length)
                {
                    throw new InvalidOperationException(
                        "Two attachments sharing one date and direction expose different compatible " +
                        "candidate sets. The frozen analysis derives candidates from the date and " +
                        "direction alone, so they cannot differ. Nothing has been censused and the " +
                        "workspace is unchanged.");
                }

                group.MessageCount++;
            }

            /// <summary>The groups in date then direction order.</summary>
            internal IReadOnlyList<CandidateGroup> BuildGroups() =>
                _groups.Values
                    .OrderBy(group => group.Date)
                    .ThenBy(group => group.Direction)
                    .ToList();

            /// <remarks>
            /// An order-sensitive fold, which is enough because the frozen analysis emits candidates in
            /// ascending <c>MediaAssetID</c> order: two groups agreeing on count and fold agree on the
            /// set. Cheaper than comparing the whole list per relation, of which there are 71,795.
            /// </remarks>
            private static long Fold(long[] assets)
            {
                var hash = 1469598103934665603L;

                foreach (var asset in assets)
                {
                    hash = unchecked((hash ^ asset) * 1099511628211L);
                }

                return hash;
            }
        }

        /// <summary>What one group's positions imply, primary or sensitivity alike.</summary>
        private sealed class GroupMetrics
        {
            internal required int MessageCount { get; init; }

            internal required int BaselineAssetCount { get; init; }

            internal required int TokenPositionCount { get; init; }

            internal required int OccurrenceCount { get; init; }

            internal required int PositionsWithSeveralAssets { get; init; }

            internal required FeasibilityClassification Classification { get; init; }

            internal int Slack => TokenPositionCount - MessageCount;

            internal bool Feasible =>
                Classification == FeasibilityClassification.EnoughTokenPositions;

            internal BigInteger WeightedAssignmentCount { get; set; }

            internal BigInteger UnweightedChoiceCount { get; set; }

            /// <summary>Candidate assets available to each message rank, when feasible.</summary>
            internal int[] UnionCounts { get; set; } = [];

            /// <summary>Whether each rank's window holds a multi-asset position.</summary>
            internal bool[] RangeHoldsSeveralAssetPosition { get; set; } = [];

            internal int AssetsAtOneTokenPosition { get; set; }

            internal int AssetsAtSeveralTokenPositions { get; set; }

            internal int MaximumTokenPositionsForOneAsset { get; set; }
        }

        /// <summary>
        /// The supported tokens of the candidate assets the groups name, and the direction evidence of
        /// the copies carrying them.
        /// </summary>
        private sealed class TokenEvidence
        {
            private readonly Dictionary<(long MediaAssetID, DateOnly Date), Dictionary<ushort, byte>>
                _tokens = [];

            private readonly HashSet<(long MediaAssetID, DateOnly Date)> _wanted = [];
            private readonly HashSet<(long MediaAssetID, DateOnly Date)> _observed = [];

            internal TokenEvidence(IReadOnlyList<CandidateGroup> groups)
            {
                foreach (var group in groups)
                {
                    foreach (var asset in group.CompatibleAssets)
                    {
                        _wanted.Add((asset, group.Date));
                    }
                }
            }

            internal bool IsWanted(long mediaAssetID, DateOnly date) =>
                _wanted.Contains((mediaAssetID, date));

            internal void RecordQualifyingCopy(long mediaAssetID, DateOnly date) =>
                _observed.Add((mediaAssetID, date));

            internal void RecordToken(
                long mediaAssetID, DateOnly date, ushort token, byte direction)
            {
                var key = (mediaAssetID, date);

                if (!_tokens.TryGetValue(key, out var tokens))
                {
                    tokens = [];
                    _tokens.Add(key, tokens);
                }

                tokens.TryGetValue(token, out var seen);
                tokens[token] = (byte)(seen | direction);
            }

            /// <remarks>
            /// The frozen analysis placed every compatible candidate in its group on the strength of a
            /// copy dated to that day, so a wanted key this pass never saw means the two disagree about
            /// the supporting population.
            /// </remarks>
            internal void RequireEveryCandidateObserved()
            {
                foreach (var key in _wanted)
                {
                    if (_observed.Contains(key))
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        "A compatible candidate asset has no qualifying physical copy on its group's " +
                        "date, although the frozen analysis placed it there on the strength of one. " +
                        "Nothing has been censused and the workspace is unchanged.");
                }
            }

            /// <summary>
            /// The group's token positions in ascending token order, each carrying its compatible assets
            /// in <c>MediaAssetID</c> order.
            /// </summary>
            /// <remarks>
            /// Acquisition duplication collapses here: a token is a key, so one payload copied into
            /// several stores at one recovered position contributes one occurrence. A genuine repeat of
            /// that payload carries a different name, hence a different token, and survives as a second
            /// position.
            /// </remarks>
            internal List<long[]> BuildPositions(CandidateGroup group, bool agreeingOnly)
            {
                var agreeing = group.Direction == MessageDirection.Outgoing
                    ? SentCopy
                    : NotUnderSentCopy;

                var contradicting = group.Direction == MessageDirection.Outgoing
                    ? NotUnderSentCopy
                    : SentCopy;

                var byToken = new SortedDictionary<ushort, List<long>>();

                foreach (var asset in group.CompatibleAssets)
                {
                    if (!_tokens.TryGetValue((asset, group.Date), out var tokens))
                    {
                        continue;
                    }

                    foreach (var (token, seen) in tokens)
                    {
                        if ((seen & contradicting) != 0)
                        {
                            throw new InvalidOperationException(
                                "A copy supporting a direction-compatible candidate records the " +
                                "opposite direction. The frozen rule makes a relation compatible only " +
                                "when no supporting copy contradicts the message's direction. Nothing " +
                                "has been censused and the workspace is unchanged.");
                        }

                        if (agreeingOnly && (seen & agreeing) == 0)
                        {
                            continue;
                        }

                        if (!byToken.TryGetValue(token, out var assets))
                        {
                            assets = [];
                            byToken.Add(token, assets);
                        }

                        assets.Add(asset);
                    }
                }

                return [.. byToken.Values.Select(assets => assets.ToArray())];
            }
        }

        /// <summary>Accumulates every primary figure across groups.</summary>
        private sealed class PrimaryAccumulator
        {
            private readonly DirectionAccumulator _outgoing = new();
            private readonly DirectionAccumulator _incoming = new();

            private readonly SlackAccumulator _pooledSlack = new();
            private readonly NarrowingAccumulator _pooledNarrowing = new();

            private readonly List<int> _possiblePositionCounts = [];
            private readonly BandAccumulator _messagePositionCounts = new();
            private readonly AssignmentBandAccumulator _assignmentCounts = new();
            private readonly List<int> _assignmentDigitCounts = [];
            private readonly ShortfallAccumulator _shortfall = new();

            private int _noCompatibleGroups;
            private int _noTokenGroups;
            private int _tooFewGroups;
            private int _enoughGroups;
            private int _noCompatibleMessages;
            private int _noTokenMessages;
            private int _tooFewMessages;
            private int _enoughMessages;

            private int _forcedGroups;
            private int _forcedMessages;
            private int _forcedOneAsset;
            private int _forcedSeveralAssets;

            private int _weightedEqualsUnweighted;
            private int _weightedExceedsUnweighted;
            private int _heavyPositions;
            private int _groupsWithHeavyPosition;
            private int _messagesWhoseRangeHoldsHeavy;

            private int _assetsAtOnePosition;
            private int _assetsAtSeveralPositions;
            private int _groupsWithRepeatedAsset;
            private int _maximumPositionsForOneAsset;

            private int _impossibleMessages;
            private int _impossibleBaselineRelations;

            internal void Add(CandidateGroup group, GroupMetrics metrics)
            {
                var direction = group.Direction == MessageDirection.Outgoing ? _outgoing : _incoming;

                direction.Add(group, metrics);

                _pooledSlack.Add(metrics.Slack, group.MessageCount);

                switch (metrics.Classification)
                {
                    case FeasibilityClassification.NoCompatibleCandidateAsset:
                        _noCompatibleGroups++;
                        _noCompatibleMessages += group.MessageCount;
                        break;

                    case FeasibilityClassification.NoSupportedTokenPosition:
                        _noTokenGroups++;
                        _noTokenMessages += group.MessageCount;
                        break;

                    case FeasibilityClassification.TooFewTokenPositions:
                        _tooFewGroups++;
                        _tooFewMessages += group.MessageCount;
                        _shortfall.Add(group.MessageCount - metrics.TokenPositionCount);
                        break;

                    default:
                        _enoughGroups++;
                        _enoughMessages += group.MessageCount;
                        break;
                }

                _assetsAtOnePosition += metrics.AssetsAtOneTokenPosition;
                _assetsAtSeveralPositions += metrics.AssetsAtSeveralTokenPositions;

                if (metrics.AssetsAtSeveralTokenPositions > 0)
                {
                    _groupsWithRepeatedAsset++;
                }

                _maximumPositionsForOneAsset = Math.Max(
                    _maximumPositionsForOneAsset, metrics.MaximumTokenPositionsForOneAsset);

                _heavyPositions += metrics.PositionsWithSeveralAssets;

                if (metrics.PositionsWithSeveralAssets > 0)
                {
                    _groupsWithHeavyPosition++;
                }

                if (!metrics.Feasible)
                {
                    _impossibleMessages += group.MessageCount;
                    _impossibleBaselineRelations += group.BaselineRelationCount;

                    return;
                }

                var possiblePositions = metrics.Slack + 1;

                _possiblePositionCounts.Add(possiblePositions);

                for (var rank = 0; rank < group.MessageCount; rank++)
                {
                    _messagePositionCounts.Add(possiblePositions);

                    if (metrics.RangeHoldsSeveralAssetPosition[rank])
                    {
                        _messagesWhoseRangeHoldsHeavy++;
                    }
                }

                if (metrics.Slack == 0)
                {
                    _forcedGroups++;
                    _forcedMessages += group.MessageCount;

                    for (var rank = 0; rank < group.MessageCount; rank++)
                    {
                        if (metrics.UnionCounts[rank] > 1)
                        {
                            _forcedSeveralAssets++;
                        }
                        else
                        {
                            _forcedOneAsset++;
                        }
                    }
                }

                _pooledNarrowing.Add(metrics);

                if (metrics.WeightedAssignmentCount == metrics.UnweightedChoiceCount)
                {
                    _weightedEqualsUnweighted++;
                }
                else
                {
                    _weightedExceedsUnweighted++;
                }

                _assignmentCounts.Add(metrics.WeightedAssignmentCount);
                _assignmentDigitCounts.Add(
                    metrics.WeightedAssignmentCount.ToString(CultureInfo.InvariantCulture).Length);
            }

            internal WithinDirectionAssignmentCensus Build(
                WithinDirectionAssignmentCensusRequest request,
                MatchAnalysisCensus matchingCensus,
                int excludedAdjacentDateOnly,
                SensitivityAccumulator sensitivity) =>
                new()
                {
                    ConversationID = request.ConversationID,
                    LocalParticipantID = request.LocalParticipantID,
                    MatchingCensus = matchingCensus,
                    ExcludedAdjacentDateOnlyAttachmentCount = excludedAdjacentDateOnly,

                    OutgoingPopulation = _outgoing.BuildPopulation(),
                    IncomingPopulation = _incoming.BuildPopulation(),
                    PooledPopulation = DirectionAccumulator.Pool(_outgoing, _incoming),

                    OutgoingSlack = _outgoing.BuildSlack(),
                    IncomingSlack = _incoming.BuildSlack(),
                    PooledSlack = _pooledSlack.Build(),

                    Feasibility = new FeasibilityCounts
                    {
                        NoCompatibleCandidateAssetGroups = _noCompatibleGroups,
                        NoSupportedTokenPositionGroups = _noTokenGroups,
                        TooFewTokenPositionGroups = _tooFewGroups,
                        EnoughTokenPositionGroups = _enoughGroups,
                        MessagesInNoCompatibleCandidateAssetGroups = _noCompatibleMessages,
                        MessagesInNoSupportedTokenPositionGroups = _noTokenMessages,
                        MessagesInTooFewTokenPositionGroups = _tooFewMessages,
                        MessagesInEnoughTokenPositionGroups = _enoughMessages,
                    },

                    ForcedPositions = new ForcedPositionCounts
                    {
                        Groups = _forcedGroups,
                        Messages = _forcedMessages,
                        MessagesWhereTokenHoldsOneAsset = _forcedOneAsset,
                        MessagesWhereTokenHoldsSeveralAssets = _forcedSeveralAssets,
                    },

                    PositionAmbiguity = new TokenPositionAmbiguityCensus
                    {
                        PossibleTokenPositionCountPerGroup = Summarise(_possiblePositionCounts),
                        MessagesByPossibleTokenPositionCount = _messagePositionCounts.Build(),
                    },

                    OutgoingNarrowing = _outgoing.BuildNarrowing(),
                    IncomingNarrowing = _incoming.BuildNarrowing(),
                    PooledNarrowing = _pooledNarrowing.Build(),

                    AssignmentCounts = new AssignmentCountCensus
                    {
                        FeasibleGroupCount = _enoughGroups,
                        GroupsWhereWeightedEqualsUnweighted = _weightedEqualsUnweighted,
                        GroupsWhereWeightedExceedsUnweighted = _weightedExceedsUnweighted,
                        TokenPositionsWithSeveralAssets = _heavyPositions,
                        GroupsWithASeveralAssetPosition = _groupsWithHeavyPosition,
                        AssignmentCounts = _assignmentCounts.Build(),
                        MaximumDecimalDigitCount =
                            _assignmentDigitCounts.Count == 0 ? 0 : _assignmentDigitCounts.Max(),
                        MedianDecimalDigitCount = LowerMedian(_assignmentDigitCounts),
                    },

                    AssetMultiplicity = new AssetTokenMultiplicityCensus
                    {
                        AssetGroupRelationsWithOneToken = _assetsAtOnePosition,
                        AssetGroupRelationsWithSeveralTokens = _assetsAtSeveralPositions,
                        GroupsWithARepeatedAsset = _groupsWithRepeatedAsset,
                        MaximumTokenPositionsForOneAsset = _maximumPositionsForOneAsset,
                    },

                    ImpossibleGroups = new ImpossibleGroupCensus
                    {
                        NoCompatibleCandidateAssetGroups = _noCompatibleGroups,
                        NoSupportedTokenPositionGroups = _noTokenGroups,
                        TooFewTokenPositionGroups = _tooFewGroups,
                        Shortfall = _shortfall.Build(),
                        MessagesInImpossibleGroups = _impossibleMessages,
                        BaselineRelationsInImpossibleGroups = _impossibleBaselineRelations,
                    },

                    Sensitivity = sensitivity.Build(),

                    Collisions = new CollisionParticipationCensus
                    {
                        TokenPositionsWithSeveralCompatibleAssets = _heavyPositions,
                        GroupsContainingSuchAPosition = _groupsWithHeavyPosition,
                        MessagesWhoseRangeIncludesSuchAPosition = _messagesWhoseRangeHoldsHeavy,
                    },
                };
        }

        /// <summary>The C0, slack and narrowing figures for one direction.</summary>
        private sealed class DirectionAccumulator
        {
            private readonly List<int> _messagesPerGroup = [];
            private readonly BandAccumulator _assets = new();
            private readonly BandAccumulator _positions = new();
            private readonly BandAccumulator _occurrences = new();
            private readonly SlackAccumulator _slack = new();
            private readonly NarrowingAccumulator _narrowing = new();

            private int _groups;
            private int _messages;
            private int _baselineRelations;
            private int _feasibleBaselineRelations;
            private int _impossibleBaselineRelations;

            internal void Add(CandidateGroup group, GroupMetrics metrics)
            {
                _groups++;
                _messages += group.MessageCount;
                _messagesPerGroup.Add(group.MessageCount);

                _assets.Add(group.CompatibleAssets.Length);
                _positions.Add(metrics.TokenPositionCount);
                _occurrences.Add(metrics.OccurrenceCount);

                _baselineRelations += group.BaselineRelationCount;
                _slack.Add(metrics.Slack, group.MessageCount);

                if (metrics.Feasible)
                {
                    _feasibleBaselineRelations += group.BaselineRelationCount;
                    _narrowing.Add(metrics);
                }
                else
                {
                    _impossibleBaselineRelations += group.BaselineRelationCount;
                }
            }

            internal AssignmentGroupPopulation BuildPopulation() =>
                new()
                {
                    GroupCount = _groups,
                    MessageCount = _messages,
                    MessagesPerGroup = Summarise(_messagesPerGroup),
                    CompatibleAssetsPerGroup = _assets.Build(),
                    TokenPositionsPerGroup = _positions.Build(),
                    OccurrencesPerGroup = _occurrences.Build(),
                    BaselineCompatibleRelationCount = _baselineRelations,
                    BaselineRelationsInFeasibleGroups = _feasibleBaselineRelations,
                    BaselineRelationsInImpossibleGroups = _impossibleBaselineRelations,
                };

            internal SequenceSlackDistribution BuildSlack() => _slack.Build();

            internal CandidateNarrowingCensus BuildNarrowing() => _narrowing.Build();

            /// <summary>The two directions' populations added together.</summary>
            internal static AssignmentGroupPopulation Pool(
                DirectionAccumulator outgoing, DirectionAccumulator incoming)
            {
                var pooledMessages = new List<int>(outgoing._messagesPerGroup);
                pooledMessages.AddRange(incoming._messagesPerGroup);

                return new AssignmentGroupPopulation
                {
                    GroupCount = outgoing._groups + incoming._groups,
                    MessageCount = outgoing._messages + incoming._messages,
                    MessagesPerGroup = Summarise(pooledMessages),
                    CompatibleAssetsPerGroup = BandAccumulator.Pool(outgoing._assets, incoming._assets),
                    TokenPositionsPerGroup =
                        BandAccumulator.Pool(outgoing._positions, incoming._positions),
                    OccurrencesPerGroup =
                        BandAccumulator.Pool(outgoing._occurrences, incoming._occurrences),
                    BaselineCompatibleRelationCount =
                        outgoing._baselineRelations + incoming._baselineRelations,
                    BaselineRelationsInFeasibleGroups =
                        outgoing._feasibleBaselineRelations + incoming._feasibleBaselineRelations,
                    BaselineRelationsInImpossibleGroups =
                        outgoing._impossibleBaselineRelations + incoming._impossibleBaselineRelations,
                };
            }
        }

        /// <summary>The direction-agreeing sensitivity view, kept as three separate effects.</summary>
        private sealed class SensitivityAccumulator
        {
            private readonly SlackAccumulator _slack = new();
            private readonly NarrowingAccumulator _narrowing = new();

            private int _frozenRelations;
            private int _agreeingRelations;
            private int _positionsRemoved;
            private int _assetsLosingEveryPosition;

            private int _noCompatibleGroups;
            private int _noTokenGroups;
            private int _tooFewGroups;
            private int _enoughGroups;
            private int _noCompatibleMessages;
            private int _noTokenMessages;
            private int _tooFewMessages;
            private int _enoughMessages;

            private int _combinedFrozenBaselineRelations;
            private int _combinedFinalRelations;

            internal void Add(
                CandidateGroup group,
                GroupMetrics primary,
                GroupMetrics agreeing,
                List<long[]> primaryPositions,
                List<long[]> agreeingPositions)
            {
                _frozenRelations += group.BaselineRelationCount;
                _agreeingRelations += group.MessageCount * agreeing.BaselineAssetCount;
                _positionsRemoved += primary.TokenPositionCount - agreeing.TokenPositionCount;

                _assetsLosingEveryPosition +=
                    DistinctAssetCount(primaryPositions) - DistinctAssetCount(agreeingPositions);

                _slack.Add(agreeing.Slack, group.MessageCount);

                switch (agreeing.Classification)
                {
                    case FeasibilityClassification.NoCompatibleCandidateAsset:
                        _noCompatibleGroups++;
                        _noCompatibleMessages += group.MessageCount;
                        break;

                    case FeasibilityClassification.NoSupportedTokenPosition:
                        _noTokenGroups++;
                        _noTokenMessages += group.MessageCount;
                        break;

                    case FeasibilityClassification.TooFewTokenPositions:
                        _tooFewGroups++;
                        _tooFewMessages += group.MessageCount;
                        break;

                    default:
                        _enoughGroups++;
                        _enoughMessages += group.MessageCount;

                        _narrowing.Add(agreeing);

                        // The combined effect compares the frozen baseline with the final sensitivity
                        // candidate set, over the groups the filtered evidence can still satisfy.
                        _combinedFrozenBaselineRelations += group.BaselineRelationCount;
                        _combinedFinalRelations += agreeing.UnionCounts.Sum();
                        break;
                }
            }

            internal SensitivityDecompositionCensus Build() =>
                new()
                {
                    FrozenCandidateRelations = _frozenRelations,
                    AgreeingTokenEligibleCandidateRelations = _agreeingRelations,
                    TokenPositionsRemovedByFiltering = _positionsRemoved,
                    AssetsLosingEveryTokenPosition = _assetsLosingEveryPosition,

                    Feasibility = new FeasibilityCounts
                    {
                        NoCompatibleCandidateAssetGroups = _noCompatibleGroups,
                        NoSupportedTokenPositionGroups = _noTokenGroups,
                        TooFewTokenPositionGroups = _tooFewGroups,
                        EnoughTokenPositionGroups = _enoughGroups,
                        MessagesInNoCompatibleCandidateAssetGroups = _noCompatibleMessages,
                        MessagesInNoSupportedTokenPositionGroups = _noTokenMessages,
                        MessagesInTooFewTokenPositionGroups = _tooFewMessages,
                        MessagesInEnoughTokenPositionGroups = _enoughMessages,
                    },

                    Slack = _slack.Build(),
                    SequenceOrderEffect = _narrowing.Build(),
                    CombinedFrozenBaselineRelations = _combinedFrozenBaselineRelations,
                    CombinedFinalCandidateRelations = _combinedFinalRelations,
                };
        }

        /// <summary>Counts the narrowing classes and the two relation totals.</summary>
        private sealed class NarrowingAccumulator
        {
            private int _noReduction;
            private int _unique;
            private int _reduced;
            private int _alreadyUnique;
            private int _baselineRelations;
            private int _sequenceRelations;

            internal void Add(GroupMetrics metrics)
            {
                foreach (var count in metrics.UnionCounts)
                {
                    _baselineRelations += metrics.BaselineAssetCount;
                    _sequenceRelations += count;

                    if (count == metrics.BaselineAssetCount)
                    {
                        _noReduction++;

                        if (metrics.BaselineAssetCount == 1)
                        {
                            // One candidate before the hypothesis was applied, so there was no
                            // narrowing to measure. Counted apart from the uniqueness result.
                            _alreadyUnique++;
                        }
                    }
                    else if (count == 1)
                    {
                        _unique++;
                    }
                    else
                    {
                        _reduced++;
                    }
                }
            }

            internal CandidateNarrowingCensus Build() =>
                new()
                {
                    NoReduction = _noReduction,
                    UniqueCandidateUnderSequenceOrderHypothesis = _unique,
                    ReducedUnderSequenceOrderHypothesis = _reduced,
                    MessagesAlreadyUniqueWithoutHypothesis = _alreadyUnique,
                    BaselineCandidateRelations = _baselineRelations,
                    SequenceCompatibleCandidateRelations = _sequenceRelations,
                };
        }

        /// <summary>Counts groups and messages into the slack bands.</summary>
        private sealed class SlackAccumulator
        {
            private readonly int[] _groups = new int[8];
            private readonly int[] _messages = new int[8];

            internal void Add(int slack, int messageCount)
            {
                var band = Band(slack);

                _groups[band]++;
                _messages[band] += messageCount;
            }

            internal SequenceSlackDistribution Build() =>
                new() { Groups = Build(_groups), Messages = Build(_messages) };

            private static int Band(int slack) =>
                slack switch
                {
                    < 0 => 0,
                    0 => 1,
                    1 => 2,
                    2 => 3,
                    <= 5 => 4,
                    <= 10 => 5,
                    <= 25 => 6,
                    _ => 7,
                };

            private static SlackBandCounts Build(int[] counts) =>
                new()
                {
                    Negative = counts[0],
                    Zero = counts[1],
                    One = counts[2],
                    Two = counts[3],
                    ThreeToFive = counts[4],
                    SixToTen = counts[5],
                    ElevenToTwentyFive = counts[6],
                    MoreThanTwentyFive = counts[7],
                };
        }

        /// <summary>Counts observations into the project's fixed size bands.</summary>
        private sealed class BandAccumulator
        {
            private readonly int[] _counts = new int[8];

            internal void Add(int value) => _counts[Band(value)]++;

            internal SequenceBandCounts Build() => Build(_counts);

            internal static SequenceBandCounts Pool(BandAccumulator first, BandAccumulator second)
            {
                var pooled = new int[8];

                for (var index = 0; index < pooled.Length; index++)
                {
                    pooled[index] = first._counts[index] + second._counts[index];
                }

                return Build(pooled);
            }

            private static int Band(int value) =>
                value switch
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

            private static SequenceBandCounts Build(int[] counts) =>
                new()
                {
                    Zero = counts[0],
                    One = counts[1],
                    Two = counts[2],
                    ThreeToFive = counts[3],
                    SixToTen = counts[4],
                    ElevenToTwentyFive = counts[5],
                    TwentySixToFifty = counts[6],
                    MoreThanFifty = counts[7],
                };
        }

        /// <summary>Counts how far short impossible groups fall.</summary>
        private sealed class ShortfallAccumulator
        {
            private int _oneToFive;
            private int _sixToTen;
            private int _elevenToTwentyFive;
            private int _moreThanTwentyFive;

            internal void Add(int shortfall)
            {
                switch (shortfall)
                {
                    case <= 5:
                        _oneToFive++;
                        break;

                    case <= 10:
                        _sixToTen++;
                        break;

                    case <= 25:
                        _elevenToTwentyFive++;
                        break;

                    default:
                        _moreThanTwentyFive++;
                        break;
                }
            }

            internal ShortfallBandCounts Build() =>
                new()
                {
                    OneToFive = _oneToFive,
                    SixToTen = _sixToTen,
                    ElevenToTwentyFive = _elevenToTwentyFive,
                    MoreThanTwentyFive = _moreThanTwentyFive,
                };
        }

        /// <summary>Counts feasible groups into the assignment-count bands.</summary>
        private sealed class AssignmentBandAccumulator
        {
            private static readonly BigInteger Ten = new(10);
            private static readonly BigInteger OneHundred = new(100);
            private static readonly BigInteger OneThousand = new(1_000);
            private static readonly BigInteger OneMillion = new(1_000_000);

            private int _zero;
            private int _one;
            private int _twoToTen;
            private int _elevenToOneHundred;
            private int _oneHundredOneToOneThousand;
            private int _oneThousandOneToOneMillion;
            private int _moreThanOneMillion;

            internal void Add(BigInteger count)
            {
                if (count.IsZero)
                {
                    _zero++;
                }
                else if (count.IsOne)
                {
                    _one++;
                }
                else if (count <= Ten)
                {
                    _twoToTen++;
                }
                else if (count <= OneHundred)
                {
                    _elevenToOneHundred++;
                }
                else if (count <= OneThousand)
                {
                    _oneHundredOneToOneThousand++;
                }
                else if (count <= OneMillion)
                {
                    _oneThousandOneToOneMillion++;
                }
                else
                {
                    _moreThanOneMillion++;
                }
            }

            internal AssignmentCountBandCounts Build() =>
                new()
                {
                    Zero = _zero,
                    One = _one,
                    TwoToTen = _twoToTen,
                    ElevenToOneHundred = _elevenToOneHundred,
                    OneHundredOneToOneThousand = _oneHundredOneToOneThousand,
                    OneThousandOneToOneMillion = _oneThousandOneToOneMillion,
                    MoreThanOneMillion = _moreThanOneMillion,
                };
        }

        /// <summary>
        /// The spread and bands of a set of counts, with the lower of the two middle values as the
        /// median so a second run of this census is comparable with the first.
        /// </summary>
        private static CountSummary Summarise(List<int> counts)
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

        private static int LowerMedian(List<int> values)
        {
            if (values.Count == 0)
            {
                return 0;
            }

            var ordered = values.ToList();
            ordered.Sort();

            return ordered[(ordered.Count - 1) / 2];
        }
    }
}
