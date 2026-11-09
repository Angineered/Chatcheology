namespace Chatcheology.Data.NewerMessages
{
    /// <summary>
    /// The rule step 3b's archive export will use to number newer messages and attachments, fixed here so the
    /// numbers never shift across refreshes (the phone's refresh digest, CH-71 step 2, covers attachment ids,
    /// sequence numbers and low-quality links, and refuses a refresh in which any earlier one moved).
    /// </summary>
    /// <remarks>
    /// <para><strong>Timeline.</strong> Older exports, then the workspace, then newer messages in
    /// <c>NewerMessageID</c> order. Newer rows are append-only and their ids come from <c>AUTOINCREMENT</c> in
    /// msgstore order, so every earlier newer message keeps its place and a refresh only adds at the end.</para>
    /// <para><strong>Ids.</strong> The archive export numbers older-export messages and attachments right above the
    /// workspace conversation's own maximum, in timeline order (<c>ArchiveCombinedSource</c>). Newer ones go right
    /// above those: <c>archive id = base + NewerMessageID</c> (or <c>+ NewerAttachmentID</c>), where the base is the
    /// highest archive id the workspace and older-export parts use (the older-export base plus the number of shown
    /// older messages, or older attachments). Those parts do not change between refreshes (D1: the checked past
    /// stays as it is), so the base and every newer id stay fixed. Gaps are allowed (ids need only be unique and
    /// stable); <c>AUTOINCREMENT</c> never reuses an id.</para>
    /// <para><strong>Sequence numbers.</strong> <c>older shown count + workspace count + position among newer
    /// messages</c> (1-based, by <c>NewerMessageID</c>).</para>
    /// <para><strong>States and links</strong> never change after a row is written (no update path), so a newer
    /// attachment's state, media SHA-256 and low-quality link are the same in every later archive.</para>
    /// </remarks>
    public static class NewerMessagesArchiveIds
    {
        /// <summary>The archive message id of a newer message.</summary>
        /// <param name="highestEarlierMessageID">The highest archive message id the workspace and older-export parts use.</param>
        public static long MessageID(long highestEarlierMessageID, long newerMessageID) =>
            checked(highestEarlierMessageID + newerMessageID);

        /// <summary>The archive attachment id of a newer attachment.</summary>
        /// <param name="highestEarlierAttachmentID">The highest archive attachment id the workspace and older-export parts use.</param>
        public static long AttachmentID(long highestEarlierAttachmentID, long newerAttachmentID) =>
            checked(highestEarlierAttachmentID + newerAttachmentID);

        /// <summary>The archive sequence number of a newer message.</summary>
        /// <param name="earlierMessages">The shown older-export messages plus the workspace conversation's messages.</param>
        /// <param name="position">The message's 1-based position among the newer messages, by <c>NewerMessageID</c>.</param>
        public static int SequenceNumber(int earlierMessages, int position)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(earlierMessages);
            ArgumentOutOfRangeException.ThrowIfLessThan(position, 1);

            return checked(earlierMessages + position);
        }
    }
}
