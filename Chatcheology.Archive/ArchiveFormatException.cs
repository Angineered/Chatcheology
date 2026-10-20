namespace Chatcheology.Archive
{
    /// <summary>
    /// The folder or database is not an archive this build can read: missing, incomplete, damaged,
    /// or written by a newer format version.
    /// </summary>
    /// <remarks>
    /// The message is a fixed phrase. It never carries a path, a name or any archive content.
    /// </remarks>
    public sealed class ArchiveFormatException(string message) : Exception(message);
}
