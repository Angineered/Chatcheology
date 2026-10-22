namespace Chatcheology.Mobile.Import
{
    /// <summary>One file in the folder an archive is imported from.</summary>
    /// <param name="RelativePath">Its path below the folder, with <c>/</c> between folder names.</param>
    /// <param name="Length">Its size in bytes.</param>
    public sealed record ImportSourceFile(string RelativePath, long Length);

    /// <summary>
    /// The folder an archive is imported from, read only. On Android it is the folder picked with the
    /// system picker, read through the ContentResolver; in tests, a fake.
    /// </summary>
    /// <remarks>Nothing is ever written to, renamed in or deleted from the source.</remarks>
    public interface IImportSource
    {
        /// <summary>Every file below the folder, at any depth.</summary>
        IReadOnlyList<ImportSourceFile> ListFiles(CancellationToken cancellationToken);

        /// <summary>Opens a file the listing named, for reading.</summary>
        /// <exception cref="IOException">The file cannot be opened or read.</exception>
        Stream OpenRead(string relativePath);
    }

    /// <summary>The free space where the app keeps its archives.</summary>
    public interface IStorageSpace
    {
        /// <summary>The bytes the app can still write to its private storage.</summary>
        long AvailableBytes();
    }

    /// <summary>What an import is doing.</summary>
    public enum ImportStage
    {
        /// <summary>Copying the listed files into staging; can be cancelled.</summary>
        Copying,

        /// <summary>Checking the copy with the archive validator; cannot be cancelled, no progress.</summary>
        Checking,
    }

    /// <summary>How far an import has got. Counts only.</summary>
    /// <param name="Stage">Copying or checking.</param>
    /// <param name="BytesCopied">Bytes copied so far.</param>
    /// <param name="TotalBytes">Bytes to copy in all.</param>
    /// <param name="FilesCopied">Files copied so far.</param>
    /// <param name="TotalFiles">Files to copy in all.</param>
    public sealed record ImportProgress(ImportStage Stage, long BytesCopied, long TotalBytes, int FilesCopied, int TotalFiles);
}
