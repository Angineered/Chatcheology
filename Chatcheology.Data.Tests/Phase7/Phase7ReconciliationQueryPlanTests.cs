using System.Text.RegularExpressions;
using Chatcheology.Data.Phase7;
using Chatcheology.Data.Workspace;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Tests.Phase7
{
    /// <summary>
    /// Pins the execution <em>shape</em> of the two reconciliation checks that touch the media-link
    /// side, so the quadratic plan they once had cannot come back.
    /// </summary>
    /// <remarks>
    /// These two checks needed a regression test that a functional test cannot provide, because the old
    /// statements were <strong>semantically correct</strong>: they returned the right answer and passed
    /// every functional assertion, while scanning all of <c>MediaAssetFile</c> once per candidate row.
    /// The frozen v3 schema indexes that table only by <c>UNIQUE (MediaFileID)</c> and its integer
    /// primary key — there is no index on <c>MediaAssetID</c>, which is the same absence that made
    /// <c>MediaEvidenceIndex</c> build an in-memory index rather than query per attachment — and SQLite
    /// creates no automatic index for the shapes those statements had. The cost was paid inside the
    /// publication transaction, with the writer reservation held and every other conforming writer
    /// blocked.
    /// <para>
    /// So the property under test is the plan, not the answer and not the clock. There is deliberately
    /// <strong>no wall-clock threshold</strong> anywhere here: a timing assertion would be flaky on a
    /// loaded machine and would say nothing about why. What is asserted instead is that the heavy side
    /// is materialised once and then probed, and that the per-candidate rescan is absent.
    /// </para>
    /// <para>
    /// Plan text is a SQLite implementation detail, so it is normalised for case and spacing and only
    /// four structural properties are asserted. The provider is pinned at
    /// <c>Microsoft.Data.Sqlite 10.0.11</c>; if a future upgrade renames a plan step, this test is
    /// supposed to fail loudly and be re-read rather than pass quietly.
    /// </para>
    /// </remarks>
    public class Phase7ReconciliationQueryPlanTests
    {
        [Fact]
        public void CheckEight_MaterialisesTheDatedAssetKeySetAndProbesIt() =>
            AssertMaterialisedAndProbed(
                Phase7RunReconciliation.CandidateWithoutExactDateCopyStatement,
                cteName: "DATEDASSETKEY");

        [Fact]
        public void CheckTwelve_MaterialisesTheContradictingKeySetAndProbesIt() =>
            AssertMaterialisedAndProbed(
                Phase7RunReconciliation.DirectionInvariantStatement,
                cteName: "CONTRADICTINGKEY");

        /// <remarks>
        /// The four properties, and why each one matters.
        /// <list type="number">
        /// <item>The key set is <c>MATERIALIZE</c>d — computed once into SQLite's own temporary store,
        /// which is what makes the media-link scan happen once rather than per candidate.</item>
        /// <item>No <c>CORRELATED</c> step exists. That is the signature of check 8's old shape, whose
        /// <c>NOT EXISTS</c> was re-evaluated for every candidate row.</item>
        /// <item>The <c>MediaAssetFile</c> scan appears <em>before</em> the <c>AttachmentCandidate</c>
        /// lookup in plan order. This is the property that catches check 12's old shape, where
        /// <c>MediaAssetFile</c> sat inside the nested loop driven by the candidate table. A plain
        /// "is there a scan of the link table" assertion could not distinguish the two, because the
        /// corrected plan scans it too — once, up front.</item>
        /// <item>The materialised set is reached by a <c>SEARCH</c>, not a <c>SCAN</c> — it is probed by
        /// key rather than walked.</item>
        /// </list>
        /// </remarks>
        private static void AssertMaterialisedAndProbed(string statement, string cteName)
        {
            var plan = ReadQueryPlan(statement);
            var rendered = string.Join("\n  ", plan);

            var materialise = plan.FindIndex(
                line => line.StartsWith("MATERIALIZE " + cteName, StringComparison.Ordinal));

            Assert.True(
                materialise >= 0,
                $"The plan does not materialise {cteName}, so the media-link side is not computed once:"
                    + $"\n  {rendered}");

            Assert.DoesNotContain(
                plan,
                line => line.Contains("CORRELATED", StringComparison.Ordinal));

            var linkScan = plan.FindIndex(
                line => line.StartsWith("SCAN L", StringComparison.Ordinal));

            Assert.True(
                linkScan >= 0,
                $"The plan does not scan MediaAssetFile at all, so this test is no longer measuring "
                    + $"what it was written for:\n  {rendered}");

            var candidateLookup = plan.FindIndex(
                line => line.Contains("ATTACHMENTCANDIDATE", StringComparison.Ordinal));

            Assert.True(
                candidateLookup >= 0,
                $"The plan does not name AttachmentCandidate's index, so the ordering assertion below "
                    + $"cannot be made:\n  {rendered}");

            Assert.True(
                linkScan < candidateLookup,
                $"MediaAssetFile is scanned at plan step {linkScan + 1}, after AttachmentCandidate at "
                    + $"step {candidateLookup + 1}, which is the per-candidate rescan shape this check "
                    + $"was restructured to remove:\n  {rendered}");

            Assert.Contains(
                plan,
                line => line.StartsWith("SEARCH K", StringComparison.Ordinal));
        }

        /// <summary>
        /// <c>EXPLAIN QUERY PLAN</c> for one statement, against a populated synthetic v3 workspace,
        /// normalised for case and spacing.
        /// </summary>
        /// <remarks>
        /// The workspace holds a completed run, so the Phase 7 tables the statements read are populated
        /// rather than empty. Only synthetic temporary databases are used.
        /// </remarks>
        private static List<string> ReadQueryPlan(string statement)
        {
            using var fixture = new Phase7TestFixture();
            var date = Phase7TestFixture.FirstDate;

            fixture.AddOutgoingGroup(date, memberCount: 2, positionCount: 3);
            fixture.AddTokenAsset(date.AddDays(-1), isSent: true, 60);

            var result = fixture.Run();

            Assert.True(result.CandidateRowCount > 0);

            using var connection = WorkspaceDatabase.OpenReadOnlyConnection(fixture.DatabasePath);
            using var command = connection.CreateCommand();

            command.CommandText = "EXPLAIN QUERY PLAN " + statement;
            command.Parameters.AddWithValue("$runID", result.RunID);
            command.Parameters.AddWithValue("$localParticipantID", result.LocalParticipantID);

            var plan = new List<string>();

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                plan.Add(Normalise(reader.GetString(3)));
            }

            Assert.NotEmpty(plan);

            return plan;
        }

        /// <summary>
        /// Upper-cased with runs of whitespace collapsed, so the assertions are not brittle for
        /// cosmetic plan text.
        /// </summary>
        private static string Normalise(string line) =>
            Regex.Replace(line, @"\s+", " ", RegexOptions.CultureInvariant)
                .Trim()
                .ToUpperInvariant();
    }
}
