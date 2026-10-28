namespace Chatcheology.Data.Viewer
{
    /// <summary>
    /// Every message of one conversation as a sequence number and a stored local time, in conversation
    /// order (CH-42 step 5: what memory rows are placed among).
    /// </summary>
    /// <param name="SequenceNumbers">The sequence numbers, rising.</param>
    /// <param name="TimesLocal">Each message's stored local wall-clock time, in the same order.</param>
    public sealed record ConversationMessageTimes(IReadOnlyList<int> SequenceNumbers, IReadOnlyList<DateTime> TimesLocal);
}
