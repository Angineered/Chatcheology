namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Maps <see cref="MediaType"/> to and from the text the workspace stores.
    /// </summary>
    /// <remarks>
    /// Mapped explicitly rather than by <see cref="Enum.ToString()"/> and
    /// <see cref="Enum.Parse{TEnum}(string)"/>, so the stored values depend on neither the enum's
    /// numeric ordering nor its member names. Renaming a member then becomes a compile-time
    /// decision about the database rather than a silent change to data already written.
    /// </remarks>
    internal static class MediaTypeText
    {
        internal const string Image = "Image";
        internal const string Video = "Video";
        internal const string Audio = "Audio";
        internal const string Document = "Document";
        internal const string Unknown = "Unknown";

        /// <summary>The text the database stores for <paramref name="mediaType"/>.</summary>
        internal static string Format(MediaType mediaType) => mediaType switch
        {
            MediaType.Image => Image,
            MediaType.Video => Video,
            MediaType.Audio => Audio,
            MediaType.Document => Document,
            MediaType.Unknown => Unknown,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mediaType),
                mediaType,
                "There is no stored representation for this media type."),
        };

        /// <summary>The media type <paramref name="text"/> stands for.</summary>
        /// <remarks>
        /// Unrecognised text is a fault rather than an unknown media type. Schema version 2 puts no
        /// <c>CHECK</c> on the column, so a value this build does not know is a workspace written
        /// by something else; reading it as <see cref="MediaType.Unknown"/> would quietly reclassify
        /// somebody else's data as unclassifiable.
        /// </remarks>
        /// <exception cref="InvalidOperationException">The text is not a stored media type.</exception>
        internal static MediaType Parse(string text) => text switch
        {
            Image => MediaType.Image,
            Video => MediaType.Video,
            Audio => MediaType.Audio,
            Document => MediaType.Document,
            Unknown => MediaType.Unknown,
            _ => throw new InvalidOperationException(
                $"The workspace stores a MediaFile or MediaAsset media type this build does not " +
                $"recognise, of length {text.Length}. The operation is abandoned rather than " +
                $"treating an unrecognised classification as an unclassified one."),
        };
    }
}
