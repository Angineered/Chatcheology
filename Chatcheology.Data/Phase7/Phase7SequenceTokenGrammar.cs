using Chatcheology.Data.Media;

namespace Chatcheology.Data.Phase7
{
    /// <summary>
    /// Stage 7.1's own statement of the approved four-digit sequence-token grammar, and of the rule
    /// that a dated file's name and its stored <c>FileDate</c> must agree.
    /// </summary>
    /// <remarks>
    /// The fourth statement of one rule, and stated here rather than shared for the reason the
    /// repository already gives twice — <c>WaSequenceScopeCensusService.StripExtension</c>
    /// ("a second copy of the Stage A rule, kept here rather than shared, because sharing it would
    /// mean editing the Stage A census whose evidence is preserved") and
    /// <c>WithinDirectionAssignmentCensusService.ReadSupportedToken</c> ("stated here rather than
    /// shared, because extracting a helper would mean editing a frozen path whose real-run evidence
    /// is preserved; the grammar-equivalence tests are what contain the drift").
    /// <para>
    /// The marker itself is <em>not</em> restated: <see cref="WhatsAppNameMarker.TryLocate"/> is the
    /// shared locator Stage A, Stage B1 and Stage B2B all already call, and it is reused unchanged.
    /// What is restated is only the suffix rule and the marker-agreement requirement.
    /// </para>
    /// <para>
    /// The rule, character for character with the committed one: the whole remainder after
    /// <c>-WA</c>, with the <em>recorded</em> <c>Extension</c> removed only when the name genuinely
    /// ends with it (ordinal, ignoring case), must be exactly four ASCII digits; the token is the
    /// fixed-width integer those digits spell, which orders identically to the digit string.
    /// </para>
    /// <para>
    /// Drift is contained by <c>Phase7SequenceTokenGrammarEquivalenceTests</c>, the fourth
    /// grammar-equivalence class, which requires this statement and the committed censuses to agree
    /// on every name in the existing corpora.
    /// </para>
    /// </remarks>
    internal static class Phase7SequenceTokenGrammar
    {
        /// <summary>The approved grammar's width: exactly four ASCII digits after <c>-WA</c>.</summary>
        internal const int SupportedTokenLength = 4;

        /// <summary>
        /// Locates the marker in <paramref name="fileName"/> and requires the date it encodes to be
        /// the file's stored <paramref name="fileDate"/>.
        /// </summary>
        /// <param name="suffixStart">The index of the first character after <c>-WA</c>.</param>
        /// <remarks>
        /// A dated file whose name carries no locatable marker, or whose marker encodes a different
        /// date, is a workspace this run refuses rather than an occurrence with unsupported grammar.
        /// The reason is not squeamishness: the token is read from the name while the date is joined
        /// from the column, so if the two disagree about which characters are a date then this run and
        /// the committed Phase 5 classifier disagree about the workspace — which is not a fact about
        /// any attachment. It mirrors
        /// <c>WithinDirectionAssignmentCensusService.RequireMarkerAgreeingWithFileDate</c> and rests
        /// on the Phase 6 closure's own <c>DatedFilesWithNoLocatableMarker = 0</c> equivalence proof.
        /// </remarks>
        /// <exception cref="InvalidOperationException">
        /// The name carries no locatable marker, or the marker's date is not the stored one. Nothing
        /// has been published and the workspace is unchanged.
        /// </exception>
        internal static void RequireMarkerAgreeingWithFileDate(
            long mediaFileID, string fileName, DateOnly fileDate, out int suffixStart)
        {
            if (!WhatsAppNameMarker.TryLocate(fileName, out suffixStart, out var markerDate))
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} carries a FileDate but no locatable -YYYYMMDD-WA " +
                    $"marker, so this run and the committed classifier disagree about which " +
                    $"characters are a date. That is a fact about the workspace rather than about " +
                    $"any attachment, so it is refused rather than recorded as unsupported grammar. " +
                    $"Nothing has been published and the workspace is unchanged.");
            }

            if (markerDate != fileDate)
            {
                throw new InvalidOperationException(
                    $"MediaFile {mediaFileID} records a FileDate that is not the date its own name " +
                    $"encodes. The token is read from the name and the date is joined from the " +
                    $"column, so a workspace where the two disagree cannot be snapshotted. Nothing " +
                    $"has been published and the workspace is unchanged.");
            }
        }

        /// <summary>
        /// Reads the approved four-digit token from a name whose marker has already been located.
        /// </summary>
        /// <param name="fileName">The recovered file name.</param>
        /// <param name="extension">
        /// The <em>recorded</em> extension, or <see langword="null"/> where the workspace records
        /// none. It is removed only when the name really ends with it: where the recorded extension
        /// is not how the name ends, nothing is removed, so the remainder is not four digits and the
        /// grammar is unsupported.
        /// </param>
        /// <param name="suffixStart">
        /// The index of the first character after <c>-WA</c>, from
        /// <see cref="RequireMarkerAgreeingWithFileDate"/>.
        /// </param>
        /// <param name="token">The token the four digits spell, 0 to 9999.</param>
        /// <returns>Whether the suffix is the approved shape.</returns>
        internal static bool TryReadSupportedToken(
            string fileName, string? extension, int suffixStart, out ushort token)
        {
            ArgumentNullException.ThrowIfNull(fileName);

            token = 0;

            var remainder = fileName[suffixStart..];

            var extensionMatchesEnding =
                extension is not null
                && fileName.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

            var suffix = extensionMatchesEnding && remainder.Length >= extension!.Length
                ? remainder[..^extension.Length]
                : remainder;

            if (suffix.Length != SupportedTokenLength)
            {
                return false;
            }

            foreach (var character in suffix)
            {
                if (!char.IsAsciiDigit(character))
                {
                    return false;
                }
            }

            // Fixed width, so the numeric value orders identically to the digit string.
            token = (ushort)(
                ((suffix[0] - '0') * 1000)
                + ((suffix[1] - '0') * 100)
                + ((suffix[2] - '0') * 10)
                + (suffix[3] - '0'));

            return true;
        }
    }
}
