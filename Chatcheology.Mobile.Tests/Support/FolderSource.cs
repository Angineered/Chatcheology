using Chatcheology.Mobile.Import;

namespace Chatcheology.Mobile.Tests.Support
{
    /// <summary>
    /// A fake import source over a local folder, which records every file opened and can fail or call
    /// back on demand. Read only, like the real one.
    /// </summary>
    internal sealed class FolderSource(string folder) : IImportSource
    {
        internal List<string> Opened { get; } = [];

        /// <summary>A path whose opening throws an I/O error.</summary>
        internal string? Unreadable { get; init; }

        /// <summary>Called with each path as it is opened, before it is read.</summary>
        internal Action<string>? OnOpen { get; init; }

        public IReadOnlyList<ImportSourceFile> ListFiles(CancellationToken cancellationToken) =>
            Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Select(path => new ImportSourceFile(
                    Path.GetRelativePath(folder, path).Replace(Path.DirectorySeparatorChar, '/'),
                    new FileInfo(path).Length))
                .ToList();

        public Stream OpenRead(string relativePath)
        {
            Opened.Add(relativePath);
            OnOpen?.Invoke(relativePath);

            if (relativePath == Unreadable)
            {
                throw new IOException("Simulated unreadable file.");
            }

            return new FileStream(
                Path.Combine(folder, relativePath.Replace('/', Path.DirectorySeparatorChar)),
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read);
        }
    }

    /// <summary>A fixed amount of free space.</summary>
    internal sealed class FixedSpace(long bytes) : IStorageSpace
    {
        public long AvailableBytes() => bytes;
    }
}
