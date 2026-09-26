using System.IO;
using System.Text.RegularExpressions;

namespace Chatcheology.Desktop.Tests
{
    /// <summary>
    /// That decisions leave through Phase 7 and nowhere else.
    /// </summary>
    /// <remarks>
    /// A source scan rather than a behavioural test, because the property is an absence: no
    /// presentation or read-model code writes a decision, or any other row. Behaviour can only show
    /// that the paths taken today are clean; this shows that no other path exists to take.
    /// </remarks>
    public class WriteBoundaryTests
    {
        private static readonly string[] DecisionTables =
        [
            "AttachmentReviewEvent",
            "AttachmentCandidateDispositionEvent",
            "AttachmentResolutionEvent",
        ];

        /// <remarks>
        /// The viewer holds a read-only connection and hands every decision to a Phase 7 service by
        /// database path. It has no writable connection to issue SQL on, and no SQL to issue.
        /// </remarks>
        [Fact]
        public void TheDesktopProject_ContainsNoDataModifyingSql()
        {
            var offenders = Scan(Project("Chatcheology.Desktop"), DataModifying);

            Assert.Empty(offenders);
        }

        /// <remarks>
        /// Named <em>in a statement</em>. A Phase 7 DTO's own property — an event identifier carried
        /// through to a history line — is the read-model being consumed, which is the opposite of
        /// the thing this forbids, so the pattern requires the SQL context that would make it a
        /// table reference.
        /// </remarks>
        [Fact]
        public void TheDesktopProject_NamesNoDecisionTableInAStatement()
        {
            var offenders = Scan(
                Project("Chatcheology.Desktop"),
                new Regex(
                    @"(?:FROM|INTO|JOIN|UPDATE)\s+(?:" + string.Join('|', DecisionTables) + @")",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

            Assert.Empty(offenders);
        }

        [Fact]
        public void TheViewerReadModel_NamesNoDecisionTableInAStatement()
        {
            var directory = Path.Combine(Project("Chatcheology.Data"), "Viewer");

            var offenders = Scan(
                directory,
                new Regex(
                    @"(?:FROM|INTO|JOIN|UPDATE)\s+(?:" + string.Join('|', DecisionTables) + @")",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

            Assert.Empty(offenders);
        }

        /// <remarks>
        /// The review read-model is a consumer of Phase 7. The moment it writes — or even names a
        /// decision table in a statement of its own — it has stopped being one.
        /// </remarks>
        [Fact]
        public void TheViewerReadModel_ContainsNoDataModifyingSql()
        {
            var directory = Path.Combine(Project("Chatcheology.Data"), "Viewer");

            Assert.True(Directory.Exists(directory), directory);

            Assert.Empty(Scan(directory, DataModifying));
        }

        /// <remarks>
        /// The viewer never opens a writable connection. Everything it reads comes through
        /// <c>OpenReadOnlyConnection</c>, whose guarantee is SQLite's rather than a promise the
        /// viewer makes about itself.
        /// </remarks>
        [Fact]
        public void TheDesktopProject_OpensNoWritableConnection()
        {
            var offenders = Scan(
                Project("Chatcheology.Desktop"),
                new Regex(
                    @"WorkspaceDatabase\.OpenConnection|SqliteConnectionStringBuilder|new SqliteConnection",
                    RegexOptions.CultureInvariant));

            Assert.Empty(offenders);
        }

        /// <remarks>
        /// And the scan is not vacuous: the same shape does match a project that legitimately
        /// writes, so a real statement in the scanned directories would demonstrably be visible.
        /// </remarks>
        [Fact]
        public void TheScan_FindsTheWritesThatDoExistElsewhere()
        {
            var phase7 = Path.Combine(Project("Chatcheology.Data"), "Phase7");

            Assert.NotEmpty(Scan(phase7, DataModifying));
        }

        private static Regex DataModifying => new(
            @"\b(INSERT\s+INTO|UPDATE\s+\w+\s+SET|DELETE\s+FROM)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static List<string> Scan(string directory, Regex pattern)
        {
            var offenders = new List<string>();

            foreach (var file in Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                // Build output is not source.
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                if (pattern.IsMatch(File.ReadAllText(file)))
                {
                    offenders.Add(Path.GetFileName(file));
                }
            }

            return offenders;
        }

        private static string Project(string name) =>
            Path.Combine(FindRepositoryRoot(), name);

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
                "The repository root could not be found from the test output directory.");
        }
    }
}
