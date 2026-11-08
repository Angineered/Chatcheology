namespace MsgstoreImport;

/// <summary>
/// The fixed actor labels and statement text this tool writes into Phase 7 events, in one place.
/// </summary>
/// <remarks>
/// <para><strong>These must match text already stored in real Phase 7 events.</strong> Earlier batches
/// wrote them into the real workspace, and the tool reads them back: the day-sequence rule recognises
/// earlier import confirmations by <see cref="MsgstoreImport"/> and <see cref="ReviewedNameSize"/>
/// (and the reviewed statement by <see cref="ReviewedStatementPattern"/>), and the review-missing rule
/// finds its own reviews by <see cref="AcceptedMissingList"/>. Changing a value here would make the
/// tool stop recognising those events. They are deliberately not command-line options.</para>
/// </remarks>
internal static class ActorLabels
{
    /// <summary>Actor of every msgstore import confirmation (CH-2 onward).</summary>
    internal const string MsgstoreImport = "Angie (msgstore import)";

    /// <summary>Actor of the confirmations from the reviewed name+size list.</summary>
    internal const string ReviewedNameSize = "Angie (reviewed name+size list)";

    /// <summary>Actor of the accepted-missing reviews (CH-13).</summary>
    internal const string AcceptedMissingList = "Angie (accepted missing list)";

    /// <summary>
    /// The start of a reviewed name+size confirmation's basis text; the review list's 12-hex tag and
    /// <c>"); "</c> follow.
    /// </summary>
    internal const string ReviewedStatementPrefix = "Angie inspected the file and confirmed it (review list ";

    /// <summary>
    /// <see cref="ReviewedStatementPrefix"/> as a regular expression, followed by the review-list tag and
    /// the record phrase whose key_id it captures.
    /// </summary>
    internal const string ReviewedStatementPattern =
        @"^Angie inspected the file and confirmed it \(review list [0-9a-f]{12}\); WhatsApp msgstore record \(message key_id (\S+?)\), ";
}
