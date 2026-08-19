using System.Globalization;
using Chatcheology.Data.Matching;

namespace Chatcheology.Data.Tests.Matching
{
    /// <summary>
    /// Shared fictional values and the one call the matching tests make.
    /// </summary>
    internal static class MatchingTestData
    {
        /// <summary>The date most fixtures put their message on.</summary>
        internal static readonly DateOnly MessageDate = new(2026, 1, 5);

        /// <summary>The day before <see cref="MessageDate"/>.</summary>
        internal static DateOnly PreviousDay => MessageDate.AddDays(-1);

        /// <summary>The day after <see cref="MessageDate"/>.</summary>
        internal static DateOnly NextDay => MessageDate.AddDays(1);

        /// <summary>A date far from any fixture's message, for unrelated copies.</summary>
        internal static readonly DateOnly UnrelatedDate = new(2025, 6, 1);

        /// <summary>
        /// A distinct, well-formed, entirely fictional SHA-256 for <paramref name="number"/>.
        /// </summary>
        internal static string Hash(int number) =>
            number.ToString("X4", CultureInfo.InvariantCulture).PadLeft(64, '0');

        /// <summary>
        /// Runs the analysis, collecting every attachment the sink is handed.
        /// </summary>
        internal static MatchingRun Analyse(
            MatchingTestWorkspace workspace,
            long? localParticipantID = null,
            long conversationID = MatchingTestWorkspace.ConversationID)
        {
            var analyses = new List<AttachmentMatchAnalysis>();

            var census = new WorkspaceMatchingService().Analyse(
                workspace.DatabasePath,
                new MatchAnalysisRequest(conversationID, localParticipantID),
                analyses.Add);

            return new MatchingRun(census, analyses);
        }

        /// <summary>
        /// Runs the analysis and returns the single attachment's evidence, for the many fixtures
        /// that hold exactly one.
        /// </summary>
        internal static AttachmentMatchAnalysis AnalyseOne(
            MatchingTestWorkspace workspace, long? localParticipantID = null)
        {
            var run = Analyse(workspace, localParticipantID);

            return Assert.Single(run.Analyses);
        }

        /// <summary>What one analysis run produced.</summary>
        internal sealed record MatchingRun(
            MatchAnalysisCensus Census, IReadOnlyList<AttachmentMatchAnalysis> Analyses);
    }
}
