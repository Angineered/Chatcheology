namespace Chatcheology.Data.ArchiveExport
{
    /// <summary>
    /// The fixed wordings a newer-messages placeholder row may carry (CH-71 D6): one per kind of item the archive cannot
    /// show yet, never the item's content.
    /// </summary>
    /// <remarks>
    /// The msgstore import writes exactly these (<c>tools\MsgstoreImport\NewerKinds.cs</c>) and the phone recognises
    /// exactly these (<c>Chatcheology.Mobile</c> <c>ConversationRow.NotShownWordings</c>); tests on both sides check the
    /// lists agree. The archive export refuses a placeholder row whose text is anything else (CH-71 step 5), because a
    /// placeholder that carried other text would be shown muted as if it were a fixed wording.
    /// </remarks>
    public static class NewerPlaceholderWordings
    {
        /// <summary>Every fixed placeholder wording, compared ordinally.</summary>
        public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
        {
            "Poll (not shown in this archive)",
            "Location (not shown in this archive)",
            "Contact card (not shown in this archive)",
            "Call (not shown in this archive)",
            "View-once media (not shown in this archive)",
            "Deleted message (not shown in this archive)",
            "Message type not shown in this archive",
        };
    }
}
