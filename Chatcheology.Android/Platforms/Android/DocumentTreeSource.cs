using Android.Content;
using Android.Provider;
using Chatcheology.Mobile.Import;
using AndroidUri = Android.Net.Uri;

namespace Chatcheology.Android
{
    /// <summary>
    /// The folder picked with the system picker, read through the ContentResolver and DocumentsContract
    /// as an <see cref="IImportSource"/>. Read only: nothing is written, renamed or deleted.
    /// </summary>
    /// <remarks>
    /// <para>No path is ever guessed: files are found by listing the tree's documents and opened by their
    /// document addresses. The app holds no storage permission; the picker's grant covers this folder
    /// only, for as long as the app runs (it is not made persistent).</para>
    /// <para>Every Java exception (a provider's <c>SecurityException</c>, <c>FileNotFoundException</c>,
    /// <c>IllegalArgumentException</c> and so on) is translated into an <see cref="IOException"/>, the
    /// importer's "source unreadable", both when listing and opening and while a stream is read.</para>
    /// <para>CH-58: with <see cref="ImportReadPath.MediaUri"/> (Android 10+, photo-location permission
    /// granted) each file is opened through the media store's address for the same document, which returns
    /// a photo's location bytes as they are; when that address is null or fails, the document address is
    /// used as before. Only counts of each are kept, never an address.</para>
    /// </remarks>
    internal sealed class DocumentTreeSource : IImportSource
    {
        private static readonly string[] Columns =
        [
            DocumentsContract.Document.ColumnDocumentId,
            DocumentsContract.Document.ColumnDisplayName,
            DocumentsContract.Document.ColumnMimeType,
            DocumentsContract.Document.ColumnSize,
        ];

        private readonly Context _context;
        private readonly ContentResolver _resolver;
        private readonly AndroidUri _tree;
        private readonly ImportReadPath _readPath;
        private readonly Dictionary<string, string> _documents = new(StringComparer.Ordinal);

        internal DocumentTreeSource(Context context, AndroidUri tree, ImportReadPath readPath)
        {
            _context = context;
            _resolver = context.ContentResolver ?? throw new InvalidOperationException("No content resolver.");
            _tree = tree;
            _readPath = readPath;
        }

        /// <summary>How many files were opened through each address.</summary>
        internal ImportReadCounts ReadCounts { get; } = new();

        /// <inheritdoc />
        public IReadOnlyList<ImportSourceFile> ListFiles(CancellationToken cancellationToken)
        {
            var files = new List<ImportSourceFile>();
            _documents.Clear();

            try
            {
                var folders = new Stack<(string DocumentId, string Prefix)>();
                folders.Push((DocumentsContract.GetTreeDocumentId(_tree) ?? throw new IOException("The picked folder has no document."), string.Empty));

                while (folders.Count > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var (documentId, prefix) = folders.Pop();
                    var children = DocumentsContract.BuildChildDocumentsUriUsingTree(_tree, documentId)
                        ?? throw new IOException("The picked folder could not be listed.");

                    using var cursor = _resolver.Query(children, Columns, null, null, null)
                        ?? throw new IOException("The picked folder could not be listed.");

                    while (cursor.MoveToNext())
                    {
                        var id = cursor.GetString(0);
                        var name = cursor.GetString(1);

                        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
                        {
                            continue;
                        }

                        var path = prefix + name;

                        if (cursor.GetString(2) == DocumentsContract.Document.MimeTypeDir)
                        {
                            folders.Push((id, path + "/"));
                        }
                        else
                        {
                            files.Add(new ImportSourceFile(path, cursor.IsNull(3) ? 0 : cursor.GetLong(3)));
                            _documents.TryAdd(path, id);
                        }
                    }
                }
            }
            catch (Java.Lang.Throwable exception)
            {
                throw new IOException("The picked folder could not be read.", exception);
            }

            return files;
        }

        /// <inheritdoc />
        public Stream OpenRead(string relativePath)
        {
            if (!_documents.TryGetValue(relativePath, out var documentId))
            {
                throw new IOException("The file is not in the picked folder.");
            }

            try
            {
                var uri = DocumentsContract.BuildDocumentUriUsingTree(_tree, documentId)
                    ?? throw new IOException("The file could not be opened.");

                if (_readPath == ImportReadPath.MediaUri && OpenThroughMediaStore(uri) is { } media)
                {
                    ReadCounts.CountMediaUri();
                    return media;
                }

                return new TranslatingStream(_resolver.OpenInputStream(uri) ?? throw new IOException("The file could not be opened."));
            }
            catch (Java.Lang.Throwable exception)
            {
                throw new IOException("The file could not be opened.", exception);
            }
        }

        /// <summary>
        /// The file opened through the media store's address for the document (Android 10+), or null when
        /// there is none or it cannot be opened: the caller then uses the document address and counts it.
        /// </summary>
        private TranslatingStream? OpenThroughMediaStore(AndroidUri document)
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(29))
            {
                ReadCounts.CountFallback();
                return null;
            }

            try
            {
                if (MediaStore.GetMediaUri(_context, document) is { } media && _resolver.OpenInputStream(media) is { } stream)
                {
                    return new TranslatingStream(stream);
                }
            }
            catch (Java.Lang.Throwable)
            {
                // Not in the media store, or not readable through it: the document address is used.
            }

            ReadCounts.CountFallback();
            return null;
        }

        /// <summary>A read-only stream that turns any Java exception into an <see cref="IOException"/>.</summary>
        private sealed class TranslatingStream(Stream inner) : Stream
        {
            public override bool CanRead => true;

            public override bool CanSeek => false;

            public override bool CanWrite => false;

            public override long Length => throw new NotSupportedException();

            public override long Position
            {
                get => throw new NotSupportedException();
                set => throw new NotSupportedException();
            }

            public override int Read(byte[] buffer, int offset, int count)
            {
                try
                {
                    return inner.Read(buffer, offset, count);
                }
                catch (Java.Lang.Throwable exception)
                {
                    throw new IOException("The file could not be read.", exception);
                }
            }

            public override void Flush()
            {
            }

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    try
                    {
                        inner.Dispose();
                    }
                    catch (Java.Lang.Throwable)
                    {
                        // Closing a stream that was only read: nothing to report.
                    }
                }

                base.Dispose(disposing);
            }
        }
    }

    /// <summary>The free space of the app's private storage.</summary>
    internal sealed class AppStorageSpace(string filesFolder) : IStorageSpace
    {
        public long AvailableBytes() => new global::Android.OS.StatFs(filesFolder).AvailableBytes;
    }
}
