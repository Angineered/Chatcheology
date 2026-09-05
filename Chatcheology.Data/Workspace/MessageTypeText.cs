using Chatcheology.Core.Models;

namespace Chatcheology.Data.Workspace
{
    /// <summary>
    /// Maps <see cref="MessageType"/> to and from the text the workspace stores.
    /// </summary>
    /// <remarks>
    /// Mapped explicitly rather than by <see cref="Enum.ToString()"/> and
    /// <see cref="Enum.Parse{TEnum}(string)"/>, so the stored values depend on neither the enum's
    /// numeric ordering nor its member names. Renaming a member then becomes a compile-time
    /// decision about the database rather than a silent change to data already written.
    /// <para>
    /// One owner rather than one spelling per caller. Until the viewer existed nothing read
    /// <c>Message.MessageType</c> back, so the two spellings lived privately beside the importer
    /// that wrote them; a second spelling in a reader is exactly how a writer and a reader come to
    /// disagree about what is stored, which is the reason <see cref="WorkspaceDateFormats"/> and
    /// <see cref="Media.MediaTypeText"/> are each a single owner too.
    /// </para>
    /// <para>
    /// The two values match the <c>CHECK (MessageType IN ('User', 'System'))</c> constraint on the
    /// column exactly. There is no third.
    /// </para>
    /// </remarks>
    internal static class MessageTypeText
    {
        /// <summary>The stored text for <see cref="MessageType.User"/>.</summary>
        internal const string User = "User";

        /// <summary>The stored text for <see cref="MessageType.System"/>.</summary>
        internal const string System = "System";

        /// <summary>The text the database stores for <paramref name="messageType"/>.</summary>
        internal static string Format(MessageType messageType) => messageType switch
        {
            MessageType.User => User,
            MessageType.System => System,
            _ => throw new ArgumentOutOfRangeException(
                nameof(messageType),
                messageType,
                "There is no stored representation for this message type."),
        };

        /// <summary>The message type <paramref name="text"/> stands for.</summary>
        /// <remarks>
        /// Unrecognised text is a fault rather than an unknown message type. The column's own
        /// <c>CHECK</c> admits exactly two values, so a third is a workspace written by something
        /// that bypassed the constraint; reading it as either of the two would be inventing a fact
        /// about a message this build cannot describe.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The text is not a stored message type.</exception>
        internal static MessageType Parse(string text) => text switch
        {
            User => MessageType.User,
            System => MessageType.System,
            _ => throw new InvalidOperationException(
                $"The workspace stores a Message.MessageType this build does not recognise, of " +
                $"length {text.Length}. The column's CHECK constraint admits only " +
                $"'{User}' and '{System}', so the row was written by something that bypassed it. " +
                $"The operation is abandoned rather than reading the message as a kind it may not be."),
        };
    }
}
