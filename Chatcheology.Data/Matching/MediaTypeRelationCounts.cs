using Chatcheology.Data.Media;

namespace Chatcheology.Data.Matching
{
    /// <summary>
    /// Exact-date candidate relationships counted by the candidate asset's content kind.
    /// </summary>
    /// <remarks>
    /// Descriptive only. The supported export does not reveal what an omitted item was, so
    /// <c>Attachment.ExpectedMediaType</c> is null and no candidate is filtered, excluded or
    /// preferred by type. This exists to say what the exact-date pools are actually made of, which
    /// is what a later decision about type evidence would have to be argued from.
    /// </remarks>
    public sealed class MediaTypeRelationCounts
    {
        /// <summary>Relationships whose candidate asset is an image.</summary>
        public required int Image { get; init; }

        /// <summary>Relationships whose candidate asset is a video.</summary>
        public required int Video { get; init; }

        /// <summary>Relationships whose candidate asset is audio.</summary>
        public required int Audio { get; init; }

        /// <summary>Relationships whose candidate asset is a document.</summary>
        public required int Document { get; init; }

        /// <summary>Relationships whose candidate asset has no reliable classification.</summary>
        public required int Unknown { get; init; }

        /// <summary>Every kind added together.</summary>
        public int Total => Image + Video + Audio + Document + Unknown;

        /// <summary>The count recorded for <paramref name="mediaType"/>.</summary>
        public int this[MediaType mediaType] => mediaType switch
        {
            MediaType.Image => Image,
            MediaType.Video => Video,
            MediaType.Audio => Audio,
            MediaType.Document => Document,
            MediaType.Unknown => Unknown,
            _ => throw new ArgumentOutOfRangeException(
                nameof(mediaType), mediaType, "Unhandled media type."),
        };
    }
}
