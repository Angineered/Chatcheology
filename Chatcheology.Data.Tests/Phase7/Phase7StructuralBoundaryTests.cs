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
        /// Reconciliation legitimately <em>reads</em> pre-existing tables and the Stage 7.2 event tables
        /// — checking they are still empty is one of its jobs. What it may never do is write, so that is
        /// what is asserted here.
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
        /// Reflection over the whole namespace, so a resolution, confirmation, revocation, anchoring,
        /// scoring, weighting or thresholding member cannot be added later without this failing.
        /// </remarks>
        [Fact]
        public void Phase7Namespace_ExposesNoResolutionOperation()
        {
            string[] forbidden =
            [
                "Resolve", "Confirm", "Revoke", "Anchor", "Score", "Weight", "Threshold", "Automatic",
            ];

            var members = typeof(Phase7EvidenceSnapshotService).Assembly.GetTypes()
                .Where(type => type.IsPublic && type.Namespace == "Chatcheology.Data.Phase7")
                .SelectMany(
                    type => type.GetMembers().Select(member => $"{type.Name}.{member.Name}"))
                .ToList();

            Assert.NotEmpty(members);

            foreach (var member in members)
            {
                foreach (var word in forbidden)
                {
                    Assert.DoesNotContain(word, member, StringComparison.OrdinalIgnoreCase);
                }
            }
        }

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
                    "Phase7CandidateSupport",
                    "Phase7DirectionSupport",
                    "Phase7EvidenceSnapshotService",
                    "Phase7OrderSupport",
                    "Phase7RankedCandidateSet",
                    "Phase7RunRequest",
                    "Phase7RunResult",
                    "Phase7RunStatus",
                    "Phase7SupportComparison",
                    "Phase7SupportRanking",
                    "Phase7SupportReader",
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
