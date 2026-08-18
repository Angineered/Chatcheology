namespace Chatcheology.Data.Media
{
    /// <summary>
    /// Source-type values this build understands well enough to read extra evidence from.
    /// </summary>
    /// <remarks>
    /// <c>MediaSource.SourceType</c> is free text in the schema, and any value may be stored. This
    /// class does not restrict what a source may be; it names the one layout whose folder and file
    /// naming conventions are known, so that conventions are applied only where they actually hold.
    /// <para>
    /// A source type not named here is inventoried in full. It simply yields no direction and no
    /// naming-derived date, because for an unknown layout those would be guesses rather than
    /// evidence.
    /// </para>
    /// </remarks>
    public static class MediaSourceTypes
    {
        /// <summary>
        /// A WhatsApp media directory tree, whose <c>Sent</c> folders and <c>-YYYYMMDD-WA</c> file
        /// names carry meaning this build can read.
        /// </summary>
        public const string WhatsAppMediaDirectory = "WhatsAppMediaDirectory";
    }
}
