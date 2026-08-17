namespace Chatcheology.Core.Models
{
    /// <summary>
    /// The kind of logical message a <see cref="ParsedMessage"/> represents.
    /// </summary>
    /// <remarks>
    /// Deliberately minimal. Only the distinctions the supported export layout actually forces
    /// are represented; finer system-message subtypes are not inferred.
    /// </remarks>
    public enum MessageType
    {
        /// <summary>
        /// A message written by a participant. <see cref="ParsedMessage.Sender"/> is never null.
        /// </summary>
        User,

        /// <summary>
        /// A system-generated message rather than a participant-authored message.
        /// <see cref="ParsedMessage.Sender"/> is always null.
        /// </summary>
        System,
    }
}
