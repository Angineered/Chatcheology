using Chatcheology.Data.Phase7;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// Asks <see cref="Phase7ReconstructionReader"/> about the attachments of a page, in bounded
    /// batches.
    /// </summary>
    /// <remarks>
    /// <para><strong>Composition, not authority.</strong> Every answer here is Phase 7's, returned
    /// unchanged. This exists only because a page of messages is not a shape that reader takes:
    /// it may carry no attachment at all, or more than one statement should bind at once, and both
    /// are the caller's problem to solve rather than reasons to widen a closed reader.</para>
    /// <para><strong>An empty page asks nothing.</strong> The batch read deliberately refuses an
    /// empty list, because an empty result would read as "none of them has anything" — a statement
    /// about attachments rather than the absence of any. Most messages carry no attachment, so a
    /// page with none is the ordinary case, and the right response is not to ask.</para>
    /// <para><strong>A page is not bounded by its message count.</strong> The schema permits several
    /// attachments on one message, so a page of a hundred and fifty messages is not a page of a
    /// hundred and fifty attachments. Batches are bounded here on their own terms.</para>
    /// <para>The reader it holds carries mutable internal state and is not thread-safe, so one
    /// instance of this belongs to one workspace session, whose access is serialised.</para>
    /// </remarks>
    public sealed class ConversationReconstructionReader
    {
        /// <summary>
        /// How many attachments one batch read asks about.
        /// </summary>
        /// <remarks>
        /// A bound on parameters per statement, chosen well inside every SQLite variable limit this
        /// package has shipped with. It is not derived from any archive, and it changes nothing a
        /// caller sees except how many statements are issued.
        /// </remarks>
        internal const int BatchSize = 500;

        private readonly Phase7ReconstructionReader _reader = new();

        /// <summary>
        /// The current reconstruction state of every attachment on a page, by attachment.
        /// </summary>
        /// <param name="attachmentIDs">
        /// The page's attachments, distinct and in page order. An empty list asks nothing and
        /// returns nothing.
        /// </param>
        /// <exception cref="ArgumentException">
        /// The same attachment appears twice. The underlying read is positional, so a duplicate
        /// would make the mapping back ambiguous.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Some attachment does not exist, or is recorded as resolved to an asset that is not
        /// there. Both are Phase 7's judgements, reported unchanged.
        /// </exception>
        public IReadOnlyDictionary<long, Phase7ReconstructionState> Read(
            SqliteConnection connection, IReadOnlyList<long> attachmentIDs)
        {
            ArgumentNullException.ThrowIfNull(connection);
            ArgumentNullException.ThrowIfNull(attachmentIDs);

            if (attachmentIDs.Distinct().Count() != attachmentIDs.Count)
            {
                throw new ArgumentException(
                    "The same attachment appears more than once on this page. A page lists each " +
                    "attachment at one message and one ordinal, so a repeat is a caller mistake " +
                    "rather than a workspace state.",
                    nameof(attachmentIDs));
            }

            var states = new Dictionary<long, Phase7ReconstructionState>(attachmentIDs.Count);

            for (var offset = 0; offset < attachmentIDs.Count; offset += BatchSize)
            {
                var batch = attachmentIDs.Skip(offset).Take(BatchSize).ToList();

                foreach (var state in _reader.Read(connection, batch))
                {
                    states[state.AttachmentID] = state;
                }
            }

            return states;
        }
    }
}
