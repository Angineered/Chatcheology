namespace Chatcheology.Data.Media
{
    /// <summary>
    /// The broad kind of content a media file carries.
    /// </summary>
    /// <remarks>
    /// Deliberately coarse. These are the distinctions that can be drawn from a file extension
    /// alone, which is all the evidence this phase reads; nothing here inspects file contents or
    /// MIME signatures.
    /// <para>
    /// There is no <c>Sticker</c> member. A sticker is still image content, and whether a file
    /// played a sticker role is a fact about where it was stored rather than about what it is, so
    /// it belongs to later evidence rather than to this vocabulary. An animated GIF is likewise
    /// <see cref="Image"/> rather than <see cref="Video"/>.
    /// </para>
    /// <para>
    /// There is no <c>Invalid</c> or <c>Corrupt</c> member either. A file that cannot be classified
    /// is <see cref="Unknown"/>, which records the absence of evidence without asserting that the
    /// file is broken.
    /// </para>
    /// </remarks>
    public enum MediaType
    {
        /// <summary>Still or animated image content.</summary>
        Image,

        /// <summary>Moving-picture content.</summary>
        Video,

        /// <summary>Sound content, including voice notes.</summary>
        Audio,

        /// <summary>Document, archive or contact-card content.</summary>
        Document,

        /// <summary>
        /// No reliable classification. Either the extension is not one this phase recognises, or
        /// the file is empty and its extension therefore describes content that is not there.
        /// </summary>
        Unknown,
    }
}
