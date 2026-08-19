namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// Which way a message travelled, as far as the analysis is entitled to say.
    /// </summary>
    /// <remarks>
    /// Direction is never read from a message on its own. A WhatsApp export names senders but does
    /// not mark which participant exported it, so the only way to know whether a message was sent
    /// or received is for the caller to state which participant is the local user. Without that
    /// statement every message here is <see cref="Unknown"/>, and no evidence is invented to fill
    /// the gap.
    /// </remarks>
    public enum MessageDirection
    {
        /// <summary>
        /// No local participant was supplied, or the message has no sender at all — a system
        /// notice. Not a claim that the direction is unknowable, only that it is unknown here.
        /// </summary>
        Unknown,

        /// <summary>The sender is the participant the caller named as the local user.</summary>
        Outgoing,

        /// <summary>The sender is some other participant in the conversation.</summary>
        Incoming,
    }
}
