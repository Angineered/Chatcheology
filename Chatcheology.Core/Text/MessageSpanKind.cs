namespace Chatcheology.Core.Text
{
    /// <summary>
    /// What a run of message text is, as far as presentation is concerned.
    /// </summary>
    /// <remarks>
    /// Three kinds, and deliberately no more. Phone numbers, hashtags, mentions, locations and
    /// markdown are all things a chat client recognises and this viewer does not: each one is a
    /// guess about what a piece of somebody's message meant, and a viewer whose job is to show what
    /// was written does not get to make those.
    /// </remarks>
    public enum MessageSpanKind
    {
        /// <summary>Ordinary text, shown exactly as written.</summary>
        Text,

        /// <summary>A web address the reader may follow.</summary>
        WebLink,

        /// <summary>An email address the reader may write to.</summary>
        EmailAddress,
    }
}
