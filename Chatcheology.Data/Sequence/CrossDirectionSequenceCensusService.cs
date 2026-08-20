using System.Globalization;
using System.Numerics;
using Chatcheology.Data.Matching;
using Chatcheology.Data.Media;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Sequence
{
    /// <summary>
    /// Measures whether the provisional cohort can say anything about sequence order, and if it can,
    /// whether committed message order and recovered WA token order agree on the same date.
    /// </summary>
    /// <remarks>
    /// The cohort comes from the frozen <see cref="WorkspaceMatchingService"/> rather than from
    /// candidate logic restated here, because a second statement of those semantics would be a second
    /// thing to keep true. This census adds one join the matching engine is not allowed to make — from
    /// a candidate's qualifying physical copies to the sequence token in their names — and the
    /// aggregation on top of it.
    /// <para>
    /// The structural fact the whole design turns on: the frozen analysis builds candidates from the
    /// message's local date and direction alone. So every cohort relation sharing a date and a
    /// direction names one asset, and those relations are one piece of evidence, not several. Relation
    /// counts therefore never become observations. One observation is one date carrying exactly one
    /// outgoing and one incoming cohort relation, which is the only shape where a message and an asset
    /// are paired on each side without the cohort having to say which message an asset belonged to.
    /// </para>
    /// <para>
    /// Nothing here resolves an attachment, ranks a candidate, writes anything, or applies a
    /// threshold. Gates and terminal states belong to the run harness; this returns measurements.
    /// </para>
    /// </remarks>
    public sealed class CrossDirectionSequenceCensusService
    {
        /// <summary>The approved grammar's width: exactly four ASCII digits after <c>-WA</c>.</summary>
        private const int SupportedTokenLength = 4;

        /// <summary>Significant digits the exact sign probability is rendered to.</summary>
        private const int ProbabilityDigits = 12;

        /// <summary>
        /// Runs the census against one workspace.
        /// </summary>
        /// <param name="request">The workspace, the conversation, and the local participant.</param>
        /// <param name="cancellationToken">
        /// Signalling it throws; no partial census is returned.
        /// </param>
        /// <returns>The aggregate census. Nothing is truncated or sampled to produce it.</returns>
        /// <exception cref="FileNotFoundException">
        /// There is no workspace at the requested path. No file is created.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// The workspace is not one this census can read — wrong schema version, unknown conversation,
        /// a local participant who does not belong to it, incomplete Phase 5 media state, a media row
        /// whose name and stored date disagree, or a cohort that contradicts the frozen candidate
        /// rules. Nothing is repaired and nothing is skipped.
        /// </exception>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was signalled.
        /// </exception>
        public CrossDirectionSequenceCensus Analyse(
            CrossDirectionSequenceCensusRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabasePath);

            var cohort = new CohortCollector();

            // The frozen analysis, called exactly as the preserved first pass called it. Its own media
            // pass and its own schema and participant validation happen inside this call.
            var matchingCensus = new WorkspaceMatchingService().Analyse(
                request.DatabasePath,
                new MatchAnalysisRequest(request.ConversationID, request.LocalParticipantID),
                cohort.Add,
                cancellationToken);

            var groups = cohort.BuildGroups();

            var namespaceEvidence = ReadSequenceEvidence(
                request.DatabasePath, groups, cancellationToken);

            return Build(request, matchingCensus, groups, namespaceEvidence, cancellationToken);
        }

        // -------------------------------------------------------------------------------------------
        // The sequence-evidence pass.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads every media row once, filling in the cohort groups' token evidence and the
        /// archive-wide direction/token structure at the same time.
        /// </summary>
        /// <remarks>
        /// One ordered streaming pass rather than a query per group: the schema indexes neither
        /// <c>MediaFile.FileDate</c> nor <c>MediaAssetFile.MediaAssetID</c>, and the groups' keys are
        /// answered from a dictionary instead.
        /// <para>
        /// <c>FileName</c> and <c>Extension</c> are read here and nowhere else, and only to recover the
        /// four approved digits. No prefix, path, root or directory name takes part, and
        /// <c>MediaSourceID</c> is not even selected, because this census reports nothing per source.
        /// </para>
        /// </remarks>
        private static NamespaceEvidence ReadSequenceEvidence(
            string databasePath,
            IReadOnlyList<CohortGroup> groups,
            CancellationToken cancellationToken)
        {
            var wanted = IndexWantedKeys(groups);
            var evidence = new NamespaceEvidence();

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(databasePath);

            WorkspaceSchemaGuard.RequireCurrentSchemaVersion(
                connection, "a cross-direction sequence census");

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
                    // that an undated name holds no marker either, so that converse is not re-checked
                    // here: doing so would need source-type semantics this census has no other use for.
                    continue;
                }

                ReadDatedRow(reader, mediaFileID, wanted, evidence);
            }

            RequireEveryGroupObserved(groups);

            return evidence;
        }

        /// <summary>
        /// Handles one row carrying a date: the name must agree with that date, and what it carries is
        /// then offered to the cohort group it belongs to, if any, and to the namespace diagnostic.
        /// </summary>
        private static void ReadDatedRow(
            SqliteDataReader reader,
            long mediaFileID,
            Dictionary<(long MediaAssetID, DateOnly FileDate), CohortGroup> wanted,
            NamespaceEvidence evidence)
        {
            var fileDate = ReadFileDate(reader, mediaFileID);
            var fileName = reader.GetString(1);
            var extension = reader.IsDBNull(2) ? null : reader.GetString(2);
            var isSent = reader.IsDBNull(4) ? null : (bool?)(reader.GetInt64(4) != 0);
            var mediaAssetID = reader.GetInt64(6);
            var assetSizeBytes = reader.GetInt64(8);

            RequireMarkerAgreeingWithFileDate(
                mediaFileID, fileName, fileDate, out var suffixStart);

            var key = (mediaAssetID, fileDate);
            var isWanted = wanted.TryGetValue(key, out var group);

            if (assetSizeBytes == 0)
            {
                // An asset holding no payload is never a candidate, so a cohort key reaching one means
                // this pass and the frozen analysis disagree about the eligible population.
                if (isWanted)
                {
                    throw new InvalidOperationException(
                        $"MediaFile {mediaFileID} supports a cohort candidate relation but its " +
                        $"MediaAsset holds no payload. The frozen analysis excludes zero-byte assets " +
                        $"from candidacy, so this census and that analysis disagree about which assets " +
                        $"are eligible. Nothing has been censused and the workspace is unchanged.");
                }

                return;
            }

            var token = ReadSupportedToken(fileName, extension, suffixStart);

            if (isWanted)
            {
                RecordCohortCopy(group!, mediaFileID, isSent, token);
            }

            if (token is { } supported && isSent is { } known)
            {
                evidence.Add(fileDate, supported, known);
            }
        }

        /// <summary>
        /// Records what one qualifying copy of a cohort candidate contributes.
        /// </summary>
        /// <remarks>
        /// Every qualifying copy is offered here, token-bearing or not, because two things are being
        /// established: what tokens the relation has, and that the relation still obeys the frozen
        /// direction rule. A copy contradicting its relation's direction cannot exist under that rule,
        /// so finding one is a failure rather than a fifth provenance class.
        /// </remarks>
        private static void RecordCohortCopy(
            CohortGroup group, long mediaFileID, bool? isSent, ushort? token)
        {
            group.HasQualifyingCopy = true;

            var agrees = isSent is { } sent
                && sent == (group.Direction == MessageDirection.Outgoing);

            if (isSent is not null && !agrees)
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} supports a direction-compatible candidate relation " +
                    $"while recording the opposite direction. The frozen rule makes a relation " +
                    $"compatible only when no supporting copy contradicts the message's direction, so " +
                    $"this census and that rule disagree. Nothing has been censused and the workspace " +
                    $"is unchanged.");
            }

            if (token is not { } supported)
            {
                return;
            }

            group.Tokens.Add(supported);

            if (agrees)
            {
                group.AgreeingTokens.Add(supported);
                group.HasAgreeingTokenCopy = true;
            }
            else
            {
                group.HasUnknownDirectionTokenCopy = true;
            }
        }

        /// <summary>
        /// The approved four-digit token, or null when the name's suffix is not that shape.
        /// </summary>
        /// <remarks>
        /// The committed Stage A / Stage B1 rule, character for character: the whole remainder after
        /// <c>-WA</c>, with the recorded extension removed only when the name really ends with it, must
        /// be exactly four ASCII digits. A decorated, differently sized or non-numeric suffix carries
        /// no supported evidence and is not repaired into one.
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

        private static CrossDirectionSequenceCensus Build(
            CrossDirectionSequenceCensusRequest request,
            MatchAnalysisCensus matchingCensus,
            IReadOnlyList<CohortGroup> groups,
            NamespaceEvidence namespaceEvidence,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var observations = new List<Observation>();
            var cardinality = new CardinalityAccumulator();

            foreach (var date in DatesWithBothDirections(groups))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var outgoing = date.Outgoing;
                var incoming = date.Incoming;

                if (outgoing.RelationCount != 1 || incoming.RelationCount != 1)
                {
                    continue;
                }

                cardinality.AddDate(outgoing, incoming, observations);
            }

            cancellationToken.ThrowIfCancellationRequested();

            return new CrossDirectionSequenceCensus
            {
                ConversationID = request.ConversationID,
                LocalParticipantID = request.LocalParticipantID,
                MatchingCensus = matchingCensus,
                CohortStructure = BuildCohortStructure(groups),
                RelationWeightedTokenProvenance = BuildProvenance(groups, relationWeighted: true),
                GroupWeightedTokenProvenance = BuildProvenance(groups, relationWeighted: false),
                StrictDateTokenCardinality = cardinality.Build(),
                PrimaryOrder = BuildOrder(observations),
                Displacement = BuildDisplacement(observations),
                DirectionNamespace = namespaceEvidence.Build(cancellationToken),
                DirectionAgreeingSensitivity = BuildOrder(
                    observations.Where(observation => observation.DirectionAgreeing).ToList()),
            };
        }

        private static CohortStructureCensus BuildCohortStructure(IReadOnlyList<CohortGroup> groups)
        {
            var assets = new HashSet<long>();
            var relationCounts = new List<int>(groups.Count);

            var relations = 0;
            var singletonGroups = 0;
            var relationsInMultiRelationGroups = 0;

            foreach (var group in groups)
            {
                assets.Add(group.MediaAssetID);
                relationCounts.Add(group.RelationCount);
                relations += group.RelationCount;

                if (group.RelationCount == 1)
                {
                    singletonGroups++;
                }
                else
                {
                    relationsInMultiRelationGroups += group.RelationCount;
                }
            }

            var dates = DescribeDates(groups);

            return new CohortStructureCensus
            {
                CohortRelationCount = relations,
                QualifyingGroupCount = groups.Count,
                DistinctCandidateAssetCount = assets.Count,
                DistinctCohortDateCount = dates.Outgoing + dates.Incoming + dates.Both,
                OutgoingOnlyDateCount = dates.Outgoing,
                IncomingOnlyDateCount = dates.Incoming,
                BothDirectionDateCount = dates.Both,
                RelationsPerGroup = Summarise(relationCounts),
                SingletonGroupCount = singletonGroups,
                MultiRelationGroupCount = groups.Count - singletonGroups,
                RelationsInMultiRelationGroups = relationsInMultiRelationGroups,
                MaximumRelationsInOneGroup = relationCounts.Count == 0 ? 0 : relationCounts.Max(),
                CrossDirectionDates = dates.Categories,
            };
        }

        /// <summary>
        /// Counts dates by which directions they carry, and how the both-direction ones are made up.
        /// </summary>
        private static (int Outgoing, int Incoming, int Both, CrossDirectionDateCategoryCounts
            Categories) DescribeDates(IReadOnlyList<CohortGroup> groups)
        {
            var byDate = new Dictionary<DateOnly, (CohortGroup? Outgoing, CohortGroup? Incoming)>();

            foreach (var group in groups)
            {
                byDate.TryGetValue(group.Date, out var pair);

                byDate[group.Date] = group.Direction == MessageDirection.Outgoing
                    ? (group, pair.Incoming)
                    : (pair.Outgoing, group);
            }

            var outgoingOnly = 0;
            var incomingOnly = 0;
            var bothSingleton = 0;
            var oneSingleton = 0;
            var bothMulti = 0;

            foreach (var (outgoing, incoming) in byDate.Values)
            {
                if (outgoing is null)
                {
                    incomingOnly++;
                    continue;
                }

                if (incoming is null)
                {
                    outgoingOnly++;
                    continue;
                }

                var singletons =
                    (outgoing.RelationCount == 1 ? 1 : 0) + (incoming.RelationCount == 1 ? 1 : 0);

                switch (singletons)
                {
                    case 2:
                        bothSingleton++;
                        break;

                    case 1:
                        oneSingleton++;
                        break;

                    default:
                        bothMulti++;
                        break;
                }
            }

            return (
                outgoingOnly,
                incomingOnly,
                bothSingleton + oneSingleton + bothMulti,
                new CrossDirectionDateCategoryCounts
                {
                    BothDirectionGroupsSingleton = bothSingleton,
                    OneSingletonOneMultiRelation = oneSingleton,
                    BothMultiRelation = bothMulti,
                });
        }

        /// <summary>The dates carrying a cohort group in each direction, in date order.</summary>
        private static IEnumerable<(DateOnly Date, CohortGroup Outgoing, CohortGroup Incoming)>
            DatesWithBothDirections(IReadOnlyList<CohortGroup> groups)
        {
            var byDate = new Dictionary<DateOnly, (CohortGroup? Outgoing, CohortGroup? Incoming)>();

            foreach (var group in groups)
            {
                byDate.TryGetValue(group.Date, out var pair);

                byDate[group.Date] = group.Direction == MessageDirection.Outgoing
                    ? (group, pair.Incoming)
                    : (pair.Outgoing, group);
            }

            // Ordered so the retained observations, and anything derived from them, do not depend on
            // dictionary iteration order.
            foreach (var (date, pair) in byDate.OrderBy(entry => entry.Key))
            {
                if (pair.Outgoing is { } outgoing && pair.Incoming is { } incoming)
                {
                    yield return (date, outgoing, incoming);
                }
            }
        }

        private static TokenDirectionProvenanceCounts BuildProvenance(
            IReadOnlyList<CohortGroup> groups, bool relationWeighted)
        {
            var agreeingOnly = 0;
            var unknownOnly = 0;
            var both = 0;
            var none = 0;

            foreach (var group in groups)
            {
                var weight = relationWeighted ? group.RelationCount : 1;

                if (group.Tokens.Count == 0)
                {
                    none += weight;
                    continue;
                }

                if (group.HasAgreeingTokenCopy && group.HasUnknownDirectionTokenCopy)
                {
                    both += weight;
                }
                else if (group.HasAgreeingTokenCopy)
                {
                    agreeingOnly += weight;
                }
                else
                {
                    unknownOnly += weight;
                }
            }

            return new TokenDirectionProvenanceCounts
            {
                AgreeingOnly = agreeingOnly,
                UnknownOnly = unknownOnly,
                AgreeingAndUnknown = both,
                NoSupportedToken = none,
            };
        }

        private static StrictOrderCensus BuildOrder(IReadOnlyList<Observation> observations)
        {
            var concordant = observations.Count(observation => observation.Concordant);
            var discordant = observations.Count - concordant;

            var (numerator, rendered) = SignProbability(observations.Count, concordant);

            return new StrictOrderCensus
            {
                ObservationCount = observations.Count,
                ConcordantCount = concordant,
                DiscordantCount = discordant,
                ExactOneSidedProbabilityNumerator = numerator,
                ExactOneSidedProbabilityDenominatorExponent =
                    numerator is null ? 0 : observations.Count,
                ExactOneSidedProbability = rendered,
            };
        }

        private static TokenDisplacementCensus BuildDisplacement(
            IReadOnlyList<Observation> observations)
        {
            var concordant = new BandAccumulator();
            var discordant = new BandAccumulator();

            foreach (var observation in observations)
            {
                var magnitude = Math.Abs(observation.LaterToken - observation.EarlierToken);

                if (observation.Concordant)
                {
                    concordant.Add(magnitude);
                }
                else
                {
                    discordant.Add(magnitude);
                }
            }

            return new TokenDisplacementCensus
            {
                Concordant = concordant.Build(),
                Discordant = discordant.Build(),
            };
        }

        // -------------------------------------------------------------------------------------------
        // The exact sign probability.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// The one-sided probability of at least <paramref name="concordant"/> concordant observations
        /// out of <paramref name="observations"/> when order carries no information.
        /// </summary>
        /// <remarks>
        /// Computed as the exact rational <c>SUM(i = k..n) C(n, i)</c> over <c>2 ^ n</c>. Each binomial
        /// coefficient comes from the previous one by <c>C(n, i - 1) = C(n, i) * i / (n - i + 1)</c>,
        /// multiplying before dividing, so every intermediate value is an exact integer.
        /// <para>
        /// <see cref="BigInteger"/> rather than floating point: the numerator legitimately exceeds
        /// 64 bits, and a chance baseline that rounded differently on another machine would make two
        /// runs of the same census disagree.
        /// </para>
        /// </remarks>
        private static (string? Numerator, string? Rendered) SignProbability(
            int observations, int concordant)
        {
            if (observations == 0)
            {
                return (null, null);
            }

            var sum = BigInteger.Zero;
            var term = BigInteger.One;

            for (var i = observations; i >= concordant; i--)
            {
                sum += term;
                term = term * i / (observations - i + 1);
            }

            var denominator = BigInteger.Pow(2, observations);

            return (
                sum.ToString(CultureInfo.InvariantCulture),
                RenderProbability(sum, denominator));
        }

        /// <summary>
        /// Renders an exact rational in (0, 1] to <see cref="ProbabilityDigits"/> significant digits.
        /// </summary>
        private static string RenderProbability(BigInteger numerator, BigInteger denominator)
        {
            var floor = BigInteger.Pow(10, ProbabilityDigits - 1);

            var scaled = numerator;
            var scale = 0;

            while (scaled / denominator < floor)
            {
                scaled *= 10;
                scale++;
            }

            var digits = (scaled / denominator).ToString(CultureInfo.InvariantCulture);
            var exponent = ProbabilityDigits - 1 - scale;

            return string.Create(
                CultureInfo.InvariantCulture,
                $"{digits[0]}.{digits[1..]}e{(exponent < 0 ? "-" : "+")}{Math.Abs(exponent):D2}");
        }

        // -------------------------------------------------------------------------------------------
        // Shared shaping.
        // -------------------------------------------------------------------------------------------

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

            counts.Sort();

            var one = 0;
            var two = 0;
            var threeToFive = 0;
            var sixToTen = 0;
            var elevenToTwentyFive = 0;
            var moreThanTwentyFive = 0;

            foreach (var count in counts)
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
                Population = counts.Count,
                Minimum = counts[0],
                Median = counts[(counts.Count - 1) / 2],
                Maximum = counts[^1],
                One = one,
                Two = two,
                ThreeToFive = threeToFive,
                SixToTen = sixToTen,
                ElevenToTwentyFive = elevenToTwentyFive,
                MoreThanTwentyFive = moreThanTwentyFive,
            };
        }

        private static Dictionary<(long MediaAssetID, DateOnly FileDate), CohortGroup>
            IndexWantedKeys(IReadOnlyList<CohortGroup> groups)
        {
            var wanted = new Dictionary<(long, DateOnly), CohortGroup>();

            foreach (var group in groups)
            {
                var key = (group.MediaAssetID, group.Date);

                if (wanted.TryGetValue(key, out var existing))
                {
                    // Compatibility for one direction requires an agreeing copy and no contradicting
                    // one, and the two directions' conditions are opposites, so one asset cannot be
                    // the unique compatible candidate for both directions on one date.
                    throw new InvalidOperationException(
                        $"One MediaAsset is the unique direction-compatible candidate for both " +
                        $"{existing.Direction} and {group.Direction} messages on one date. The frozen " +
                        $"direction rule makes that impossible, so this census and that rule " +
                        $"disagree. Nothing has been censused and the workspace is unchanged.");
                }

                wanted.Add(key, group);
            }

            return wanted;
        }

        private static void RequireEveryGroupObserved(IReadOnlyList<CohortGroup> groups)
        {
            foreach (var group in groups)
            {
                if (group.HasQualifyingCopy)
                {
                    continue;
                }

                throw new InvalidOperationException(
                    "A cohort candidate relation has no qualifying physical copy on its message's " +
                    "date, although the frozen analysis placed it there on the strength of one. This " +
                    "census and that analysis disagree about the supporting population. Nothing has " +
                    "been censused and the workspace is unchanged.");
            }
        }

        // -------------------------------------------------------------------------------------------
        // Media-row validation. A fourth statement of the completed-Phase-5 rules, deliberately:
        // sharing one helper would mean editing a frozen path to serve this census, and the tests
        // cover each copy.
        // -------------------------------------------------------------------------------------------

        private static void RequireSingleAssetLink(long mediaFileID, long previousMediaFileID)
        {
            if (mediaFileID != previousMediaFileID)
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} is linked to more than one MediaAsset. One physical file " +
                $"carries one payload, and the workspace's own unique constraint says so, which makes " +
                $"this a workspace written with that constraint disabled. Counting the file once per " +
                $"link would inflate its token evidence. Nothing has been censused and the workspace " +
                $"is unchanged.");
        }

        private static void RequireHashedFile(SqliteDataReader reader, long mediaFileID)
        {
            if (!reader.IsDBNull(5))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has no SHA-256, so media hashing is incomplete and Phase 5 " +
                $"has not finished for this workspace. An unhashed file belongs to no asset and would " +
                $"drop silently out of the evidence. Nothing has been censused and the workspace is " +
                $"unchanged.");
        }

        private static void RequireAssetLink(SqliteDataReader reader, long mediaFileID)
        {
            if (reader.IsDBNull(6))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is hashed but linked to no MediaAsset, so deduplication " +
                    $"is incomplete for this workspace. Nothing has been censused and the workspace " +
                    $"is unchanged.");
            }

            if (reader.IsDBNull(7))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} is linked to a MediaAsset that does not exist. The " +
                    $"workspace's foreign keys forbid this, so it was written with enforcement " +
                    $"disabled and its media relationships cannot be trusted. Nothing has been " +
                    $"censused and the workspace is unchanged.");
            }
        }

        /// <remarks>
        /// Compared case-insensitively because both hash columns are declared <c>COLLATE NOCASE</c>: to
        /// the database one hash written in each case is one value, and a case-sensitive comparison
        /// here would report a sound workspace as corrupt.
        /// </remarks>
        private static void RequireMatchingHashes(SqliteDataReader reader, long mediaFileID)
        {
            if (string.Equals(
                reader.GetString(5), reader.GetString(7), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} records a different SHA-256 from the MediaAsset it is " +
                $"linked to, so the file's content identity and its deduplication disagree. One of " +
                $"them is wrong and this census cannot tell which. Nothing has been censused and the " +
                $"workspace is unchanged.");
        }

        private static DateOnly ReadFileDate(SqliteDataReader reader, long mediaFileID)
        {
            if (WorkspaceDateFormats.TryParseFileDate(reader.GetString(3), out var fileDate))
            {
                return fileDate;
            }

            // The stored text is not quoted back: a date is harmless, but a diagnostic that repeats
            // stored media values is a habit rather than a special case.
            throw new InvalidOperationException(
                $"MediaFile {mediaFileID} has a FileDate that is not the calendar date format this " +
                $"workspace writes. It is not guessed at under another format, because a date read " +
                $"the wrong way is indistinguishable afterwards from one the archive really recorded. " +
                $"Nothing has been censused and the workspace is unchanged.");
        }

        /// <summary>
        /// Requires the name to carry a locatable marker whose date is the stored one, and reports
        /// where the suffix after <c>-WA</c> begins.
        /// </summary>
        /// <remarks>
        /// Both halves matter. The token is read from the name while the join is made on the stored
        /// date, so a workspace where the two disagree cannot be grouped by either.
        /// </remarks>
        private static void RequireMarkerAgreeingWithFileDate(
            long mediaFileID, string fileName, DateOnly fileDate, out int suffixStart)
        {
            if (!WhatsAppNameMarker.TryLocate(fileName, out suffixStart, out var markerDate))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} carries a FileDate but no locatable -YYYYMMDD-WA " +
                    $"marker, so this census and the committed classifier disagree about which " +
                    $"characters are a date. Every token below rests on that agreement. Nothing has " +
                    $"been censused and the workspace is unchanged.");
            }

            if (markerDate != fileDate)
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} records a FileDate that is not the date its own name " +
                    $"encodes. The sequence token is read from the name and the date is joined from " +
                    $"the column, so a workspace where the two disagree cannot be censused. Nothing " +
                    $"has been censused and the workspace is unchanged.");
            }
        }

        // -------------------------------------------------------------------------------------------
        // Working state.
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Collects the cohort from the frozen analysis as it streams, then groups it.
        /// </summary>
        private sealed class CohortCollector
        {
            private readonly List<CohortRelation> _relations = [];
            private readonly HashSet<int> _sequenceNumbers = [];

            internal void Add(AttachmentMatchAnalysis analysis)
            {
                if (analysis.ExactDateDirectionCompatibleCandidateCount != 1)
                {
                    return;
                }

                var compatible = analysis.ExactDateCandidates.Where(
                    candidate =>
                        candidate.DirectionCompatibility == DirectionCompatibility.Compatible)
                    .ToList();

                if (compatible.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"The frozen analysis reports one direction-compatible exact-date candidate " +
                        $"for an attachment while listing {compatible.Count}. The count and the list " +
                        $"come from one pass, so they cannot disagree unless the analysis changed. " +
                        $"Nothing has been censused and the workspace is unchanged.");
                }

                if (analysis.MessageDirection == MessageDirection.Unknown)
                {
                    throw new InvalidOperationException(
                        "An attachment has a direction-compatible candidate while its message's " +
                        "direction is unknown. Compatibility is only reachable from a known " +
                        "direction. Nothing has been censused and the workspace is unchanged.");
                }

                if (!_sequenceNumbers.Add(analysis.MessageSequenceNumber))
                {
                    throw new InvalidOperationException(
                        "Two cohort relations share one message sequence number. The workspace's " +
                        "unique constraint on (ConversationID, SequenceNumber) forbids it, and " +
                        "message order is the census's only ordering evidence. Nothing has been " +
                        "censused and the workspace is unchanged.");
                }

                _relations.Add(
                    new CohortRelation(
                        analysis.MessageDate,
                        analysis.MessageDirection,
                        analysis.MessageSequenceNumber,
                        compatible[0].MediaAssetID));
            }

            /// <summary>
            /// The cohort as <c>(date, direction)</c> groups, in date and direction order.
            /// </summary>
            /// <remarks>
            /// Every relation in one group must name one asset, because the frozen analysis builds
            /// candidates from the date and the direction and nothing else. A group naming two is that
            /// analysis having changed, not a result.
            /// </remarks>
            internal IReadOnlyList<CohortGroup> BuildGroups()
            {
                var groups = new Dictionary<(DateOnly, MessageDirection), CohortGroup>();

                foreach (var relation in _relations)
                {
                    var key = (relation.Date, relation.Direction);

                    if (!groups.TryGetValue(key, out var group))
                    {
                        group = new CohortGroup
                        {
                            Date = relation.Date,
                            Direction = relation.Direction,
                            MediaAssetID = relation.MediaAssetID,
                        };

                        groups.Add(key, group);
                    }
                    else if (group.MediaAssetID != relation.MediaAssetID)
                    {
                        throw new InvalidOperationException(
                            "Two cohort relations on one date and direction name different " +
                            "provisional candidate assets. The frozen analysis derives candidates " +
                            "from the date and direction alone, so they cannot differ. Nothing has " +
                            "been censused and the workspace is unchanged.");
                    }

                    group.RelationCount++;
                    group.LowestSequenceNumber = group.RelationCount == 1
                        ? relation.SequenceNumber
                        : Math.Min(group.LowestSequenceNumber, relation.SequenceNumber);
                }

                return groups.Values
                    .OrderBy(group => group.Date)
                    .ThenBy(group => group.Direction)
                    .ToList();
            }
        }

        /// <summary>One cohort relation, reduced to what the census uses.</summary>
        private sealed record CohortRelation(
            DateOnly Date, MessageDirection Direction, int SequenceNumber, long MediaAssetID);

        /// <summary>
        /// One <c>(date, direction)</c> group: one provisional candidate asset, however many
        /// attachment relations rest on it.
        /// </summary>
        private sealed class CohortGroup
        {
            internal required DateOnly Date { get; init; }

            internal required MessageDirection Direction { get; init; }

            internal required long MediaAssetID { get; init; }

            internal int RelationCount { get; set; }

            /// <summary>
            /// The group's message order. Meaningful as "the" message only for a singleton group,
            /// which is the only kind the strict test uses.
            /// </summary>
            internal int LowestSequenceNumber { get; set; }

            internal SortedSet<ushort> Tokens { get; } = [];

            /// <summary>Tokens seen on at least one direction-agreeing copy.</summary>
            internal SortedSet<ushort> AgreeingTokens { get; } = [];

            internal bool HasQualifyingCopy { get; set; }

            internal bool HasAgreeingTokenCopy { get; set; }

            internal bool HasUnknownDirectionTokenCopy { get; set; }
        }

        /// <summary>One independent cross-direction comparison.</summary>
        private sealed record Observation(
            ushort EarlierToken, ushort LaterToken, bool Concordant, bool DirectionAgreeing);

        /// <summary>Builds C1 and the observations the strict test runs on.</summary>
        private sealed class CardinalityAccumulator
        {
            private readonly BandAccumulator _distinctTokens = new();

            private int _noSupportedToken;
            private int _exactlyOne;
            private int _several;
            private int _eligibleDates;
            private int _excludedNoToken;
            private int _excludedSeveral;
            private int _excludedEqual;
            private int _strictOrderable;

            internal void AddDate(
                CohortGroup outgoing, CohortGroup incoming, List<Observation> observations)
            {
                _eligibleDates++;

                AddRelation(outgoing);
                AddRelation(incoming);

                if (outgoing.Tokens.Count == 0 || incoming.Tokens.Count == 0)
                {
                    _excludedNoToken++;
                    return;
                }

                if (outgoing.Tokens.Count > 1 || incoming.Tokens.Count > 1)
                {
                    _excludedSeveral++;
                    return;
                }

                var outgoingToken = outgoing.Tokens.Min;
                var incomingToken = incoming.Tokens.Min;

                if (outgoingToken == incomingToken)
                {
                    // Possible: Stage B1 found one (date, token) key held by more than one asset. An
                    // equal pair is reported, never broken by source, extension, path or asset order.
                    _excludedEqual++;
                    return;
                }

                _strictOrderable++;

                var outgoingIsEarlier = outgoing.LowestSequenceNumber < incoming.LowestSequenceNumber;

                var earlier = outgoingIsEarlier ? outgoingToken : incomingToken;
                var later = outgoingIsEarlier ? incomingToken : outgoingToken;

                observations.Add(
                    new Observation(
                        earlier,
                        later,
                        earlier < later,
                        outgoing.AgreeingTokens.Contains(outgoingToken)
                        && incoming.AgreeingTokens.Contains(incomingToken)));
            }

            internal TokenCardinalityCensus Build() =>
                new()
                {
                    NoSupportedToken = _noSupportedToken,
                    ExactlyOneDistinctToken = _exactlyOne,
                    SeveralDistinctTokens = _several,
                    DistinctTokenCounts = _distinctTokens.Build(),
                    SingletonBothDirectionDateCount = _eligibleDates,
                    DatesExcludedNoSupportedToken = _excludedNoToken,
                    DatesExcludedSeveralTokens = _excludedSeveral,
                    DatesExcludedEqualToken = _excludedEqual,
                    StrictOrderableDateCount = _strictOrderable,
                };

            private void AddRelation(CohortGroup group)
            {
                _distinctTokens.Add(group.Tokens.Count);

                switch (group.Tokens.Count)
                {
                    case 0:
                        _noSupportedToken++;
                        break;

                    case 1:
                        _exactlyOne++;
                        break;

                    default:
                        _several++;
                        break;
                }
            }
        }

        /// <summary>Counts observations into the project's fixed bands.</summary>
        private sealed class BandAccumulator
        {
            private int _zero;
            private int _one;
            private int _two;
            private int _threeToFive;
            private int _sixToTen;
            private int _elevenToTwentyFive;
            private int _twentySixToFifty;
            private int _moreThanFifty;

            internal void Add(int value)
            {
                switch (value)
                {
                    case 0:
                        _zero++;
                        break;

                    case 1:
                        _one++;
                        break;

                    case 2:
                        _two++;
                        break;

                    case <= 5:
                        _threeToFive++;
                        break;

                    case <= 10:
                        _sixToTen++;
                        break;

                    case <= 25:
                        _elevenToTwentyFive++;
                        break;

                    case <= 50:
                        _twentySixToFifty++;
                        break;

                    default:
                        _moreThanFifty++;
                        break;
                }
            }

            internal SequenceBandCounts Build() =>
                new()
                {
                    Zero = _zero,
                    One = _one,
                    Two = _two,
                    ThreeToFive = _threeToFive,
                    SixToTen = _sixToTen,
                    ElevenToTwentyFive = _elevenToTwentyFive,
                    TwentySixToFifty = _twentySixToFifty,
                    MoreThanFifty = _moreThanFifty,
                };
        }

        /// <summary>
        /// The archive-wide direction/token structure, accumulated during the same media pass.
        /// </summary>
        private sealed class NamespaceEvidence
        {
            private const byte OutgoingSeen = 1;
            private const byte IncomingSeen = 2;

            private readonly Dictionary<DateOnly, Dictionary<ushort, byte>> _dates = [];

            private int _occurrences;

            internal void Add(DateOnly fileDate, ushort token, bool isSent)
            {
                _occurrences++;

                if (!_dates.TryGetValue(fileDate, out var tokens))
                {
                    tokens = [];
                    _dates.Add(fileDate, tokens);
                }

                tokens.TryGetValue(token, out var seen);
                tokens[token] = (byte)(seen | (isSent ? OutgoingSeen : IncomingSeen));
            }

            internal DirectionNamespaceDiagnostic Build(CancellationToken cancellationToken)
            {
                var transitions = new BandAccumulator();

                var bothClassDates = 0;
                var outgoingOnlyTokens = 0;
                var incomingOnlyTokens = 0;
                var bothClassTokens = 0;
                var sharedTokenDates = 0;
                var singletonInvolved = 0;
                var outgoingBelow = 0;
                var incomingBelow = 0;
                var overlapping = 0;

                foreach (var (_, tokens) in _dates.OrderBy(entry => entry.Key))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var ordered = tokens.Keys.Order().ToList();

                    var outgoingTokens = 0;
                    var incomingTokens = 0;
                    var shared = 0;

                    ushort outgoingLowest = 0;
                    ushort outgoingHighest = 0;
                    ushort incomingLowest = 0;
                    ushort incomingHighest = 0;

                    byte previousClass = 0;
                    var transitionCount = 0;

                    foreach (var token in ordered)
                    {
                        var seen = tokens[token];

                        if ((seen & OutgoingSeen) != 0)
                        {
                            if (outgoingTokens++ == 0)
                            {
                                outgoingLowest = token;
                            }

                            outgoingHighest = token;
                        }

                        if ((seen & IncomingSeen) != 0)
                        {
                            if (incomingTokens++ == 0)
                            {
                                incomingLowest = token;
                            }

                            incomingHighest = token;
                        }

                        if (seen == (OutgoingSeen | IncomingSeen))
                        {
                            shared++;

                            // A token held by both classes says nothing about which came first there,
                            // so it breaks the chain rather than contributing a change.
                            previousClass = 0;
                            continue;
                        }

                        if (previousClass != 0 && previousClass != seen)
                        {
                            transitionCount++;
                        }

                        previousClass = seen;
                    }

                    if (outgoingTokens == 0 || incomingTokens == 0)
                    {
                        continue;
                    }

                    bothClassDates++;
                    outgoingOnlyTokens += outgoingTokens - shared;
                    incomingOnlyTokens += incomingTokens - shared;
                    bothClassTokens += shared;
                    transitions.Add(transitionCount);

                    if (shared > 0)
                    {
                        sharedTokenDates++;
                    }

                    if (outgoingTokens == 1 || incomingTokens == 1)
                    {
                        singletonInvolved++;
                    }
                    else if (outgoingHighest < incomingLowest)
                    {
                        outgoingBelow++;
                    }
                    else if (incomingHighest < outgoingLowest)
                    {
                        incomingBelow++;
                    }
                    else
                    {
                        overlapping++;
                    }
                }

                return new DirectionNamespaceDiagnostic
                {
                    KnownDirectionSupportedOccurrenceCount = _occurrences,
                    BothDirectionClassDateCount = bothClassDates,
                    OutgoingOnlyTokenCount = outgoingOnlyTokens,
                    IncomingOnlyTokenCount = incomingOnlyTokens,
                    BothClassTokenCount = bothClassTokens,
                    DatesContainingSharedToken = sharedTokenDates,
                    SingletonInvolvedDateCount = singletonInvolved,
                    OutgoingRangeEntirelyBelowIncomingDateCount = outgoingBelow,
                    IncomingRangeEntirelyBelowOutgoingDateCount = incomingBelow,
                    OverlapOrInterleaveDateCount = overlapping,
                    TransitionCounts = transitions.Build(),
                };
            }
        }
    }
}
