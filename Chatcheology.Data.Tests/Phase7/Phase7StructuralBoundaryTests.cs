using System.Reflection;
using System.Text.RegularExpressions;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// The boundaries that are properties of the SQL and of the public surface rather than promises
    /// about the code above them.
    /// </summary>
    /// <remarks>
    /// The device is the one <c>MediaEvidenceIndex</c> already uses for file names: hold the statements
    /// as constants and assert what they do and do not name. A promise in a doc comment can be broken
    /// by an edit nobody reviews; a query that has no such column in it cannot read one.
    /// </remarks>
    public class Phase7StructuralBoundaryTests
    {
        /// <summary>The five tables Stage 7.1 is allowed to write.</summary>
        private static readonly string[] Phase7EvidenceTables =
        [
            "Phase7Run",
            "AttachmentEvidenceSnapshot",
            "AttachmentCandidate",
            "AttachmentCandidateOccurrenceEvidence",
            "AttachmentAdjacentDateContext",
        ];

        /// <summary>The three Stage 7.2 tables Stage 7.1 must leave completely alone.</summary>
        private static readonly string[] StageSevenTwoTables =
        [
            "AttachmentReviewEvent",
            "AttachmentCandidateDispositionEvent",
            "AttachmentResolutionEvent",
        ];

        // ---------------------------------------------------------------------------------------
        // The writer.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Writer_EveryStatement_TargetsOnlyAPhase7EvidenceTable()
        {
            Assert.NotEmpty(Phase7EvidenceWriter.Statements);

            foreach (var statement in Phase7EvidenceWriter.Statements)
            {
                var target = TargetTableOf(statement);

                Assert.Contains(target, Phase7EvidenceTables);
            }
        }

        [Fact]
        public void Writer_NoStatement_NamesAnExistingTableOrAStage72Table()
        {
            foreach (var statement in Phase7EvidenceWriter.Statements)
            {
                foreach (var tableName in WorkspaceDatabase.VersionTwoTableNames)
                {
                    Assert.False(
                        NamesIdentifier(statement, tableName),
                        $"A writer statement names the pre-existing table {tableName}.");
                }

                foreach (var tableName in StageSevenTwoTables)
                {
                    Assert.False(
                        NamesIdentifier(statement, tableName),
                        $"A writer statement names the Stage 7.2 table {tableName}.");
                }
            }
        }

        [Fact]
        public void Writer_NoStatement_NamesAFileNameOrPathColumn()
        {
            string[] forbidden = ["FileName", "RelativePath", "Extension", "RootPath"];

            foreach (var statement in Phase7EvidenceWriter.Statements)
            {
                foreach (var column in forbidden)
                {
                    Assert.DoesNotContain(column, statement, StringComparison.Ordinal);
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // The reader.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// The nine names the ranking must not be able to read. Their absence from these queries is
        /// what makes "the ranking function never reads them" checkable.
        /// </remarks>
        [Fact]
        public void Reader_NoStatement_NamesAForbiddenColumnOrTable()
        {
            string[] forbidden =
            [
                "DateTokenKeyUniqueToPayload",
                "SequenceToken",
                "TokenGrammarSupported",
                "RemainingCompatiblePositionCountUnderHypothesis",
                "IsForcedPositionGroupShape",
                "IsWithinDirectionImpossibleGroup",
                "AttachmentAdjacentDateContext",
                "SupportingPhysicalCopyCount",
                "MediaType",
            ];

            Assert.NotEmpty(Phase7SupportReader.Statements);

            foreach (var statement in Phase7SupportReader.Statements)
            {
                foreach (var name in forbidden)
                {
                    Assert.DoesNotContain(name, statement, StringComparison.Ordinal);
                }
            }
        }

        [Fact]
        public void Reader_EveryStatement_IsASelect()
        {
            foreach (var statement in Phase7SupportReader.Statements)
            {
                Assert.StartsWith("SELECT", statement.TrimStart(), StringComparison.Ordinal);

                AssertWritesNothing(statement);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Reconciliation.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// Reconciliation legitimately <em>reads</em> pre-existing tables and the Stage 7.2 decision
        /// tables — proving a run left them as it found them is one of its jobs. What it may never do is
        /// write, so that is what is asserted here.
        /// </remarks>
        [Fact]
        public void Reconciliation_EveryStatement_OnlyReads()
        {
            Assert.NotEmpty(Phase7RunReconciliation.Statements);

            foreach (var statement in Phase7RunReconciliation.Statements)
            {
                var opening = statement.TrimStart();

                // SELECT, or a WITH … MATERIALIZED common table expression feeding one. Both are
                // read-only; a common table expression materialises into SQLite's own temporary store
                // and touches no table in the database.
                Assert.True(
                    opening.StartsWith("SELECT", StringComparison.Ordinal)
                        || opening.StartsWith("WITH", StringComparison.Ordinal),
                    $"A reconciliation statement is neither a SELECT nor a WITH: {statement}");

                AssertWritesNothing(statement);
            }
        }

        // ---------------------------------------------------------------------------------------
        // The public surface.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void RankingDimensionSetVersion_HasItsExactFrozenValue()
        {
            Assert.Equal(
                "PH7-v3.1/Direction+OrderSupport/closure-5+7.2",
                Phase7EvidenceSnapshotService.RankingDimensionSetVersion);

            // It names the dimension set and its closure citation, and nothing about request scope. A
            // later edit that smuggled a conversation or participant identifier into it fails here.
            Assert.DoesNotContain(
                "Conversation",
                Phase7EvidenceSnapshotService.RankingDimensionSetVersion,
                StringComparison.OrdinalIgnoreCase);

            Assert.DoesNotContain(
                "Participant",
                Phase7EvidenceSnapshotService.RankingDimensionSetVersion,
                StringComparison.OrdinalIgnoreCase);
        }

        /// <remarks>
        /// <para>Stage 7.2 legitimately introduces resolution operations, so the old form of this test —
        /// that no such member exists anywhere — is no longer a statable property. What replaces it
        /// proves strictly more.</para>
        /// <para>The words that could only ever name something the frozen design forbids stay forbidden
        /// on every public member of every public type, with no allowlist. The resolution vocabulary is
        /// permitted only on the resolution, history, reconciliation and reconstruction types, so
        /// <strong>no evidence, ranking, review or disposition type can gain a resolution
        /// operation</strong> — which is the property that actually matters once resolution code exists.
        /// </para>
        /// </remarks>
        [Fact]
        public void Phase7Namespace_ExposesNoRankingDerivedResolutionOperation()
        {
            string[] alwaysForbidden =
            [
                "Anchor", "Score", "Weight", "Threshold", "Automatic", "Probability", "Confidence",
                "Best", "TopRanked",
            ];

            string[] resolutionVocabulary = ["Resolve", "Resolution", "Confirm", "Revoke"];

            var publicTypes = typeof(Phase7EvidenceSnapshotService).Assembly.GetTypes()
                .Where(type => type.IsPublic && type.Namespace == "Chatcheology.Data.Phase7")
                .ToList();

            Assert.NotEmpty(publicTypes);

            var carriers = new List<string>();

            foreach (var type in publicTypes)
            {
                var members = type.GetMembers()
                    .Select(member => $"{type.Name}.{member.Name}")
                    .ToList();

                foreach (var member in members)
                {
                    foreach (var word in alwaysForbidden)
                    {
                        Assert.DoesNotContain(word, member, StringComparison.OrdinalIgnoreCase);
                    }
                }

                var carriesResolutionVocabulary = members.Any(
                    member => resolutionVocabulary.Any(
                        word => member.Contains(word, StringComparison.OrdinalIgnoreCase)));

                if (!carriesResolutionVocabulary)
                {
                    continue;
                }

                carriers.Add(type.Name);
            }

            // Exactly the types the approved design gives a resolution vocabulary to, and no others. A
            // new type quietly gaining a Confirm fails here rather than being tolerated.
            Assert.Equal(
                [
                    "Phase7ConfirmReplacementRequest",
                    "Phase7ConfirmRequest",
                    "Phase7ReconstructionState",
                    "Phase7ResolutionEventType",
                    "Phase7ResolutionHistoryEntry",
                    "Phase7ResolutionHistoryReader",
                    "Phase7ResolutionProjection",
                    "Phase7ResolutionReconciliation",
                    "Phase7ResolutionService",
                    "Phase7ResolutionStatus",
                    "Phase7ResolutionTransitionResult",
                    "Phase7ResolvedAssetDescriptor",
                    "Phase7RevokeRequest",
                    "Phase7UnresolvedPlaceholderReason",
                ],
                carriers.Order());
        }

        // ---------------------------------------------------------------------------------------
        // Stage 7.2 write targets.
        // ---------------------------------------------------------------------------------------

        /// <remarks>
        /// <para>The constraint is on what a statement <em>writes</em>. A decision legitimately reads
        /// the attachment projection, the run lifecycle, the evidence snapshot and the asset table in
        /// order to validate itself; what each service may modify is one table, or for the resolution
        /// service two.</para>
        /// <para><strong>The verb matters as well as the target.</strong> The three event tables are
        /// audit history, so a statement writing one of them must be an <c>INSERT INTO</c>. A target
        /// check alone would tolerate an <c>UPDATE AttachmentReviewEvent</c>, which is exactly the
        /// correction a new event exists to make instead. The companion source scan in
        /// <see cref="Decision_NoProductionFile_UpdatesOrDeletesAnEventRow"/> covers the SQL nobody
        /// remembered to register here.</para>
        /// </remarks>
        [Fact]
        public void Decision_EveryServiceStatement_TargetsOnlyItsOwnTables()
        {
            (IReadOnlyList<string> Statements, string[] Permitted)[] services =
            [
                (Phase7ReviewService.Statements, ["AttachmentReviewEvent"]),
                (Phase7CandidateDispositionService.Statements,
                    ["AttachmentCandidateDispositionEvent"]),
                (Phase7ResolutionService.Statements,
                    ["AttachmentResolutionEvent", "Attachment"]),
            ];

            foreach (var (statements, permitted) in services)
            {
                Assert.NotEmpty(statements);

                var modifying = statements.Where(IsDataModifying).ToList();

                Assert.Equal(permitted.Order(), modifying.Select(TargetTableOf).Order().ToList());

                foreach (var statement in modifying
                    .Where(statement => StageSevenTwoTables.Contains(TargetTableOf(statement))))
                {
                    Assert.StartsWith(
                        "INSERT INTO",
                        statement.TrimStart(),
                        StringComparison.Ordinal);
                }
            }
        }

        /// <remarks>
        /// The other half of the append-only proof, and the reason it is not carried by the statement
        /// sets alone: a statement set only holds what somebody remembered to register, so a scan of the
        /// source text is what catches an unregistered one. The three event tables are corrected by
        /// appending a new event — a replacement, a revocation, a second review — never by editing or
        /// removing the row that recorded what was decided before.
        /// <para>
        /// The database does not enforce this: schema v3 carries no trigger, and adding one would be a
        /// schema change. Append-only is a service invariant, and this is where it is proved.
        /// </para>
        /// </remarks>
        [Fact]
        public void Decision_NoProductionFile_UpdatesOrDeletesAnEventRow()
        {
            var directory = Path.Combine(FindRepositoryRoot(), "Chatcheology.Data", "Phase7");

            var offenders = new List<string>();

            foreach (var tableName in StageSevenTwoTables)
            {
                var pattern = $@"(?:UPDATE|DELETE\s+FROM)\s+{tableName}\b";

                offenders.AddRange(
                    Directory.GetFiles(directory, "*.cs")
                        .Where(
                            path => Regex.IsMatch(
                                File.ReadAllText(path),
                                pattern,
                                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                        .Select(path => $"{Path.GetFileName(path)}: {tableName}"));
            }

            Assert.Empty(offenders);

            // And the scan is not vacuous: the same shape does match the one legitimate projection
            // update, so a real UPDATE in this directory is demonstrably visible to it.
            Assert.Matches(
                @"(?:UPDATE|DELETE\s+FROM)\s+Attachment\b",
                File.ReadAllText(Path.Combine(directory, "Phase7ResolutionService.cs")));
        }

        /// <remarks>
        /// Two independent proofs, because a statement set only covers what somebody remembered to
        /// register. The source scan catches a second write path even then.
        /// </remarks>
        [Fact]
        public void ResolutionService_IsTheOnlyPhase7CodeThatWritesTheAttachmentProjection()
        {
            IReadOnlyList<string>[] everyStatementSet =
            [
                Phase7EvidenceWriter.Statements,
                Phase7SupportReader.Statements,
                Phase7RunReconciliation.Statements,
                Phase7DecisionStatements.Statements,
                Phase7ReviewService.Statements,
                Phase7CandidateDispositionService.Statements,
                Phase7CandidateDispositionReader.Statements,
                Phase7ResolutionHistoryReader.Statements,
                Phase7ResolutionReconciliation.Statements,
                Phase7ReconstructionReader.Statements,
                Phase7ResolutionService.Statements,
            ];

            var attachmentWriters = everyStatementSet
                .SelectMany(set => set)
                .Where(IsDataModifying)
                .Where(statement => TargetTableOf(statement) == "Attachment")
                .ToList();

            Assert.Single(attachmentWriters);
            Assert.Contains(attachmentWriters[0], Phase7ResolutionService.Statements);

            // And as text, across every production file in the namespace.
            var directory = Path.Combine(FindRepositoryRoot(), "Chatcheology.Data", "Phase7");

            var writingFiles = Directory.GetFiles(directory, "*.cs")
                .Where(
                    path => Regex.IsMatch(
                        File.ReadAllText(path),
                        @"(?:UPDATE|DELETE\s+FROM)\s+Attachment\b|INSERT\s+INTO\s+Attachment\b",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                .Select(Path.GetFileName)
                .ToList();

            Assert.Equal(["Phase7ResolutionService.cs"], writingFiles);
        }

        [Fact]
        public void Decision_NoStatement_WritesMessageMediaOrFileState()
        {
            string[] forbidden = ["Message", "MediaAsset", "MediaFile", "MediaAssetFile"];

            IReadOnlyList<string>[] everyStatementSet =
            [
                Phase7DecisionStatements.Statements,
                Phase7ReviewService.Statements,
                Phase7CandidateDispositionService.Statements,
                Phase7CandidateDispositionReader.Statements,
                Phase7ResolutionHistoryReader.Statements,
                Phase7ResolutionReconciliation.Statements,
                Phase7ReconstructionReader.Statements,
                Phase7ResolutionService.Statements,
            ];

            foreach (var statement in everyStatementSet.SelectMany(set => set).Where(IsDataModifying))
            {
                foreach (var tableName in forbidden)
                {
                    Assert.NotEqual(tableName, TargetTableOf(statement));
                }
            }
        }

        // ---------------------------------------------------------------------------------------
        // Stage 7.2 reads.
        // ---------------------------------------------------------------------------------------

        [Fact]
        public void Decision_EveryReaderStatement_OnlyReads()
        {
            IReadOnlyList<string>[] readerStatementSets =
            [
                Phase7DecisionStatements.Statements,
                Phase7CandidateDispositionReader.Statements,
                Phase7ResolutionHistoryReader.Statements,
                Phase7ResolutionReconciliation.Statements,
                Phase7ReconstructionReader.Statements,
            ];

            foreach (var set in readerStatementSets)
            {
                Assert.NotEmpty(set);

                foreach (var statement in set)
                {
                    var opening = statement.TrimStart();

                    Assert.True(
                        opening.StartsWith("SELECT", StringComparison.Ordinal)
                            || opening.StartsWith("WITH", StringComparison.Ordinal),
                        $"A Stage 7.2 read statement is neither a SELECT nor a WITH: {statement}");

                    AssertWritesNothing(statement);
                }
            }
        }

        /// <remarks>
        /// The frozen replay-ordering rule as a property of the SQL. Every read of the resolution history
        /// states its order explicitly, and no ordering clause anywhere in Stage 7.2 mentions the
        /// timestamp — a clock cannot be allowed to change what a history means.
        /// </remarks>
        [Fact]
        public void Replay_EveryHistoryReadStatement_OrdersByEventIdentifier()
        {
            IReadOnlyList<string>[] everyStatementSet =
            [
                Phase7DecisionStatements.Statements,
                Phase7ReviewService.Statements,
                Phase7CandidateDispositionService.Statements,
                Phase7CandidateDispositionReader.Statements,
                Phase7ResolutionHistoryReader.Statements,
                Phase7ResolutionReconciliation.Statements,
                Phase7ReconstructionReader.Statements,
                Phase7ResolutionService.Statements,
            ];

            var histories = everyStatementSet
                .SelectMany(set => set)
                .Where(statement => !IsDataModifying(statement))
                .Where(statement => NamesIdentifier(statement, "AttachmentResolutionEvent"))
                .ToList();

            Assert.NotEmpty(histories);

            foreach (var statement in histories)
            {
                Assert.Contains("ORDER BY", statement, StringComparison.Ordinal);

                Assert.Contains(
                    "AttachmentResolutionEventID ASC", statement, StringComparison.Ordinal);
            }

            foreach (var statement in everyStatementSet.SelectMany(set => set))
            {
                var order = statement.IndexOf("ORDER BY", StringComparison.Ordinal);

                if (order < 0)
                {
                    continue;
                }

                Assert.DoesNotContain(
                    "OccurredAtUtc", statement[order..], StringComparison.Ordinal);
            }
        }

        /// <remarks>
        /// A per-attachment question is never answered with the workspace-global latest completed run:
        /// two runs may cover two different conversations, and the later one is not this attachment's
        /// evidence unless it evaluated it.
        /// </remarks>
        [Fact]
        public void Decision_NoStatement_UsesTheWorkspaceGlobalLatestRun()
        {
            IReadOnlyList<string>[] everyStatementSet =
            [
                Phase7DecisionStatements.Statements,
                Phase7ReviewService.Statements,
                Phase7CandidateDispositionService.Statements,
                Phase7CandidateDispositionReader.Statements,
                Phase7ResolutionHistoryReader.Statements,
                Phase7ResolutionReconciliation.Statements,
                Phase7ReconstructionReader.Statements,
                Phase7ResolutionService.Statements,
            ];

            foreach (var statement in everyStatementSet.SelectMany(set => set))
            {
                Assert.DoesNotContain(
                    "MAX(RunID) FROM Phase7Run", statement, StringComparison.Ordinal);
            }
        }

        /// <remarks>
        /// The other half of the same rule, and the half a statement scan cannot reach: the
        /// workspace-global answer is also available as a Stage 7.1 method, so a Stage 7.2 caller could
        /// regress to it without writing any new SQL. Its own doc comment says it is scope-blind, and
        /// nothing in Stage 7.2 may use it for a per-attachment question — the single definition of
        /// "current" for a decision is <c>ReadLatestCompletedRunContaining(attachmentID)</c>.
        /// <para>
        /// The declaration itself is allowed to exist, because Stage 7.1 legitimately offers the method;
        /// what fails here is a Stage 7.2 call site.
        /// </para>
        /// </remarks>
        [Fact]
        public void Decision_NoProductionFile_CallsTheWorkspaceGlobalLatestRunReader()
        {
            var directory = Path.Combine(FindRepositoryRoot(), "Chatcheology.Data", "Phase7");

            var callers = new List<string>();

            foreach (var path in Directory.GetFiles(directory, "*.cs"))
            {
                var fileName = Path.GetFileName(path);

                foreach (var line in File.ReadAllLines(path))
                {
                    if (!line.Contains("ReadLatestCompletedRunID", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Its own declaration, and any doc comment that names it in order to say it must
                    // not be used, are both legitimate. Anything else is a call.
                    var trimmed = line.TrimStart();

                    var declaration = fileName == "Phase7SupportReader.cs"
                        && trimmed.StartsWith("public long? ReadLatestCompletedRunID", StringComparison.Ordinal);

                    if (declaration || trimmed.StartsWith("///", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    callers.Add($"{fileName}: {trimmed}");
                }
            }

            Assert.Empty(callers);

            // Not vacuous: the attachment-scoped reader the decision path actually uses is present in
            // this same directory, so a name of this shape is demonstrably findable by this scan.
            Assert.Contains(
                "ReadLatestCompletedRunContaining",
                File.ReadAllText(Path.Combine(directory, "Phase7DecisionValidation.cs")),
                StringComparison.Ordinal);
        }

        /// <remarks>
        /// The three statements Stage 7.2 restates from Stage 7.1 rather than reusing, because the
        /// closed reader's methods cannot run inside a write transaction. Held character for character
        /// to the originals, so a restatement cannot drift into a second definition of run scope,
        /// run status or currency.
        /// </remarks>
        [Fact]
        public void Decision_RestatedStageSevenOneStatements_MatchTheOriginalsExactly()
        {
            Assert.NotEmpty(Phase7DecisionStatements.RestatedStageSevenOneStatements);

            foreach (var statement in Phase7DecisionStatements.RestatedStageSevenOneStatements)
            {
                Assert.Contains(statement, Phase7SupportReader.Statements);
            }
        }

        [Fact]
        public void Decision_NoStatement_NamesAFileNameOrPathColumn()
        {
            string[] forbidden = ["FileName", "RelativePath", "Extension", "RootPath", "SHA256"];

            IReadOnlyList<string>[] everyStatementSet =
            [
                Phase7DecisionStatements.Statements,
                Phase7ReviewService.Statements,
                Phase7CandidateDispositionService.Statements,
                Phase7CandidateDispositionReader.Statements,
                Phase7ResolutionHistoryReader.Statements,
                Phase7ResolutionReconciliation.Statements,
                Phase7ReconstructionReader.Statements,
                Phase7ResolutionService.Statements,
            ];

            foreach (var statement in everyStatementSet.SelectMany(set => set))
            {
                foreach (var column in forbidden)
                {
                    Assert.DoesNotContain(column, statement, StringComparison.Ordinal);
                }
            }
        }

        /// <remarks>
        /// Nothing in Phase 7 migrates a workspace as a side effect of being asked for something else. A
        /// workspace at the wrong version is refused, and the only mentions of the migration entry point
        /// in the whole namespace are doc comments saying it is never called.
        /// </remarks>
        [Fact]
        public void Phase7Production_NeverCallsTheMigrationEntryPoint()
        {
            var directory = Path.Combine(FindRepositoryRoot(), "Chatcheology.Data", "Phase7");

            var mentions = Directory.GetFiles(directory, "*.cs")
                .SelectMany(path => File.ReadAllLines(path).Select(line => (path, line)))
                .Where(entry => entry.line.Contains("Initialise", StringComparison.Ordinal))
                .ToList();

            Assert.NotEmpty(mentions);

            foreach (var (path, line) in mentions)
            {
                Assert.StartsWith(
                    "///",
                    line.TrimStart(),
                    StringComparison.Ordinal);
            }
        }

        /// <summary>The repository root, found by walking up to the solution file.</summary>
        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Chatcheology.slnx")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new InvalidOperationException(
                "The repository root was not found by walking up from the test output directory.");
        }

        private static bool IsDataModifying(string statement) =>
            Regex.IsMatch(
                statement,
                @"^\s*(?:INSERT\s+INTO|UPDATE|DELETE\s+FROM)\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <remarks>
        /// The public surface stays narrow: two services, their request and result types, and the
        /// support vocabulary. Everything computational is internal, which is also what makes the
        /// lifecycle seam and the generic-profile tests possible.
        /// </remarks>
        [Fact]
        public void Phase7Namespace_ExposesOnlyTheNarrowPublicSurface()
        {
            var publicTypes = typeof(Phase7EvidenceSnapshotService).Assembly.GetTypes()
                .Where(type => type.IsPublic && type.Namespace == "Chatcheology.Data.Phase7")
                .Select(type => type.Name)
                .Order()
                .ToList();

            Assert.Equal(
                [
                    "OrderConsistencyAnnotation",
                    "Phase7CandidateDisposition",
                    "Phase7CandidateDispositionReader",
                    "Phase7CandidateDispositionRequest",
                    "Phase7CandidateDispositionResult",
                    "Phase7CandidateDispositionService",
                    "Phase7CandidateSupport",
                    "Phase7ConfirmReplacementRequest",
                    "Phase7ConfirmRequest",
                    "Phase7DirectionSupport",
                    "Phase7EvidenceSnapshotService",
                    "Phase7IndependentBasisType",
                    "Phase7OrderSupport",
                    "Phase7RankedCandidateSet",
                    "Phase7ReconstructionReader",
                    "Phase7ReconstructionState",
                    "Phase7ResolutionEventType",
                    "Phase7ResolutionHistoryEntry",
                    "Phase7ResolutionHistoryReader",
                    "Phase7ResolutionProjection",
                    "Phase7ResolutionReconciliation",
                    "Phase7ResolutionService",
                    "Phase7ResolutionStatus",
                    "Phase7ResolutionTransitionResult",
                    "Phase7ResolvedAssetDescriptor",
                    "Phase7ReviewOutcome",
                    "Phase7ReviewRequest",
                    "Phase7ReviewResult",
                    "Phase7ReviewService",
                    "Phase7RevokeRequest",
                    "Phase7RunRequest",
                    "Phase7RunResult",
                    "Phase7RunStatus",
                    "Phase7SupportComparison",
                    "Phase7SupportRanking",
                    "Phase7SupportReader",
                    "Phase7UnresolvedPlaceholderReason",
                ],
                publicTypes);
        }

        [Fact]
        public void Phase7RunResult_CarriesTheRunRecordFieldListStageSevenThreeMustPreserve()
        {
            var properties = typeof(Phase7RunResult)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(property => property.Name)
                .ToList();

            foreach (var required in new[]
            {
                "RunID",
                "Status",
                "StartedAtUtc",
                "CompletedAtUtc",
                "PreRunWorkspaceFileSha256",
                "RankingDimensionSetVersion",
                "ConversationID",
                "LocalParticipantID",
            })
            {
                Assert.Contains(required, properties);
            }
        }

        // ---------------------------------------------------------------------------------------
        // Helpers.
        // ---------------------------------------------------------------------------------------

        private static void AssertWritesNothing(string statement)
        {
            foreach (var verb in new[] { "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE" })
            {
                Assert.False(
                    NamesIdentifier(statement, verb),
                    $"A read-only statement contains {verb}.");
            }
        }

        /// <summary>
        /// The table an <c>INSERT INTO</c> or <c>UPDATE</c> statement writes to.
        /// </summary>
        private static string TargetTableOf(string statement)
        {
            var match = Regex.Match(
                statement,
                @"^\s*(?:INSERT\s+INTO|UPDATE)\s+(\w+)",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            Assert.True(match.Success, $"Statement is neither an INSERT nor an UPDATE: {statement}");

            return match.Groups[1].Value;
        }

        /// <summary>
        /// Whether <paramref name="statement"/> names <paramref name="identifier"/> as a whole word.
        /// </summary>
        /// <remarks>
        /// Whole-word matching matters here rather than being fussy. <c>Attachment</c> is a prefix of
        /// <c>AttachmentEvidenceSnapshot</c>, and <c>MediaAsset</c> and <c>MediaFile</c> are prefixes of
        /// the <c>MediaAssetID</c> and <c>MediaFileID</c> columns a Phase 7 row legitimately holds, so a
        /// substring test would report every correct statement as a violation.
        /// </remarks>
        private static bool NamesIdentifier(string statement, string identifier) =>
            Regex.IsMatch(
                statement,
                $@"\b{Regex.Escape(identifier)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
