using System.Globalization;
using System.Runtime.ExceptionServices;
using Chatcheology.Archive;
using Chatcheology.Mobile.Conversation;

namespace Chatcheology.Mobile.Gallery
{
    /// <summary>
    /// A run of whole months of the gallery (CH-66): the photos from <see cref="FirstIndex"/> on, in gallery
    /// order, ending on the last photo under a month heading, so a later batch never continues a heading.
    /// </summary>
    public sealed class GalleryBatch
    {
        internal GalleryBatch(int firstIndex, IReadOnlyList<AttachmentCard> photos, IReadOnlyList<GalleryMonth> months)
        {
            FirstIndex = firstIndex;
            Photos = photos;
            Months = months;
        }

        /// <summary>The index of its first photo in the whole gallery.</summary>
        public int FirstIndex { get; }

        /// <summary>Its photos, in gallery order.</summary>
        public IReadOnlyList<AttachmentCard> Photos { get; }

        /// <summary>Its photos under their month headings, in gallery order.</summary>
        public IReadOnlyList<GalleryMonth> Months { get; }

        /// <summary>
        /// Its rows of the list, to be added after the rows of the batches before it: the same rows
        /// <see cref="PhotoGallery.Rows"/> has for these months, photo indexes counted from the whole gallery.
        /// </summary>
        public IReadOnlyList<GalleryRow> Rows(int columns)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(columns);

            return PhotoGallery.RowsOf(Months, FirstIndex, columns);
        }
    }

    /// <summary>
    /// The photo gallery built newest months first (CH-66): the newest batch is ready after reading only the
    /// newest pages, and older months follow in batches, in the background, until the whole gallery is there.
    /// </summary>
    /// <remarks>
    /// <para><strong>Same gallery.</strong> The batches put together are exactly the gallery
    /// <see cref="PhotoGallery.Build(IEnumerable{ArchivePage}, ConversationTimeline, CultureInfo?, CancellationToken)"/>
    /// makes: the same photos, order, month headings and cards. A batch ends only where a month heading ends.</para>
    /// <para><strong>Shared.</strong> One load per session (<see cref="ArchiveSession.GetGalleryLoad"/>). Every
    /// screen reads it from the first batch on (<see cref="NextAsync"/>), so a gallery closed and opened again
    /// mid-load neither repeats nor loses a photo. A screen that stops reading does not stop the load; closing
    /// the session does, and then every reader is told so.</para>
    /// <para><strong>Threads.</strong> The load runs on a worker thread with a reader of its own, which it
    /// closes when done, failed or cancelled; it never touches the session's reader or any view.</para>
    /// </remarks>
    public sealed class PhotoGalleryLoad
    {
        /// <summary>
        /// Photos in the first batch, at least (rounded up to whole months): about eight screens of three-wide
        /// rows, so the reader cannot reach its end before the next batch is there.
        /// </summary>
        public const int FirstBatchPhotos = 150;

        /// <summary>Photos in each later batch, at least (rounded up to whole months).</summary>
        public const int LaterBatchPhotos = 500;

        private readonly object _lock = new();
        private readonly List<GalleryBatch> _batches = [];
        private readonly List<AttachmentCard> _photos = [];
        private readonly TaskCompletionSource<PhotoGallery> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private PhotoGallery? _gallery;
        private Exception? _failure;

        private PhotoGalleryLoad()
        {
            // A failure nobody waits for is still observed, so it is never reported as unobserved.
            _completion.Task.ContinueWith(
                task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        /// <summary>The whole gallery once every batch is in; until then, or after a failure, <see langword="null"/>.</summary>
        public PhotoGallery? Gallery
        {
            get
            {
                lock (_lock)
                {
                    return _gallery;
                }
            }
        }

        /// <summary>The whole gallery, when every batch is in. Faults or is cancelled as the load is.</summary>
        public Task<PhotoGallery> Completion => _completion.Task;

        /// <summary>How many batches are in so far.</summary>
        public int BatchCount
        {
            get
            {
                lock (_lock)
                {
                    return _batches.Count;
                }
            }
        }

        /// <summary>
        /// Starts loading the gallery of the archive in <paramref name="folder"/> with a reader of its own,
        /// newest months first. <paramref name="cancellationToken"/> stops it (the session closing).
        /// </summary>
        public static PhotoGalleryLoad Start(
            string folder, long localParticipantID, LowQualityIndex lowQuality, Stars? stars, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrEmpty(folder);
            ArgumentNullException.ThrowIfNull(lowQuality);

            return Start(token => ReadBatches(folder, localParticipantID, lowQuality, stars, token), cancellationToken);
        }

        /// <summary>
        /// Starts a load whose batches come from <paramref name="batches"/>, enumerated once on a worker thread.
        /// </summary>
        public static PhotoGalleryLoad Start(Func<CancellationToken, IEnumerable<GalleryBatch>> batches, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(batches);

            var load = new PhotoGalleryLoad();

            // Not given the token: a load cancelled before it starts must still run far enough to say so.
            _ = Task.Run(() => load.Run(batches, cancellationToken), CancellationToken.None);

            return load;
        }

        /// <summary>
        /// Batch <paramref name="index"/> (0 is the newest) once it is in, or <see langword="null"/> when the
        /// gallery is complete with fewer batches. A screen reads 0, 1, 2… until <see langword="null"/>.
        /// </summary>
        /// <exception cref="OperationCanceledException">
        /// <paramref name="cancellationToken"/> was cancelled (only this wait stops), or the load was.
        /// </exception>
        /// <exception cref="Exception">The load failed: the reader's exception.</exception>
        public async Task<GalleryBatch?> NextAsync(int index, CancellationToken cancellationToken = default)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);

            while (true)
            {
                Task published;

                lock (_lock)
                {
                    if (index < _batches.Count)
                    {
                        return _batches[index];
                    }

                    if (_failure is { } failure)
                    {
                        if (failure is OperationCanceledException)
                        {
                            throw new OperationCanceledException("The gallery stopped loading.", failure);
                        }

                        ExceptionDispatchInfo.Throw(failure);
                    }

                    if (_gallery is not null)
                    {
                        return null;
                    }

                    published = _published.Task;
                }

                await published.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>Sets the star flag of every photo loaded so far from <paramref name="stars"/> (after a toggle).</summary>
        public void RefreshStars(Stars stars)
        {
            ArgumentNullException.ThrowIfNull(stars);

            AttachmentCard[] photos;

            lock (_lock)
            {
                photos = [.. _photos];
            }

            foreach (var photo in photos)
            {
                stars.Apply(photo);
            }
        }

        /// <summary>
        /// The gallery of <paramref name="pagesNewestFirst"/> in batches of whole months, newest first: the first
        /// of at least <paramref name="firstBatchPhotos"/> photos, the others of at least
        /// <paramref name="laterBatchPhotos"/> (fewer only for the last). Cards follow <paramref name="timeline"/>'s
        /// rules, as <see cref="PhotoGallery.Build(IEnumerable{ArchivePage}, ConversationTimeline, CultureInfo?, CancellationToken)"/>.
        /// </summary>
        /// <param name="pagesNewestFirst">The conversation's pages, the newest page first; each page's messages in chat order.</param>
        /// <param name="timeline">The chat's card rules (its reader is used for the media paths only).</param>
        /// <param name="culture">The month headings' language; the current culture when null.</param>
        /// <param name="firstBatchPhotos">The fewest photos in the first batch.</param>
        /// <param name="laterBatchPhotos">The fewest photos in each later batch.</param>
        /// <param name="cancellationToken">Stops between pages.</param>
        public static IEnumerable<GalleryBatch> Batches(
            IEnumerable<ArchivePage> pagesNewestFirst,
            ConversationTimeline timeline,
            CultureInfo? culture = null,
            int firstBatchPhotos = FirstBatchPhotos,
            int laterBatchPhotos = LaterBatchPhotos,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(pagesNewestFirst);
            ArgumentNullException.ThrowIfNull(timeline);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(firstBatchPhotos);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(laterBatchPhotos);

            return BatchesCore(pagesNewestFirst, timeline, culture ?? CultureInfo.CurrentCulture, firstBatchPhotos, laterBatchPhotos, cancellationToken);
        }

        /// <summary>
        /// The conversation's pages, newest first (each page's messages still in chat order), with the checks
        /// <see cref="ArchiveReader.ReadAllPages"/> makes: sequence numbers strictly falling, none below 1, and
        /// exactly as many messages as the table holds.
        /// </summary>
        /// <exception cref="ArchiveFormatException">The messages cannot all be read in order.</exception>
        public static IEnumerable<ArchivePage> ReadPagesNewestFirst(ArchiveReader reader)
        {
            ArgumentNullException.ThrowIfNull(reader);

            return ReadPagesNewestFirstCore(reader);
        }

        private static IEnumerable<ArchivePage> ReadPagesNewestFirstCore(ArchiveReader reader)
        {
            var total = reader.ReadMessageCount();
            var read = 0;
            int? lowest = null;

            // "Before" cannot reach the largest sequence number there can be, so that one is asked for first.
            var top = reader.ReadPageFrom(int.MaxValue, 1);
            var page = top.Messages.Count > 0 ? top : reader.ReadPageBefore(int.MaxValue, ArchiveReader.MaximumPageSize);

            while (page.Messages.Count > 0)
            {
                for (var i = page.Messages.Count - 1; i >= 0; i--)
                {
                    var sequenceNumber = page.Messages[i].SequenceNumber;

                    if (sequenceNumber >= lowest || ++read > total)
                    {
                        throw new ArchiveFormatException("The archive's messages are not in a strictly rising sequence order.");
                    }

                    if (sequenceNumber < 1)
                    {
                        throw new ArchiveFormatException(
                            "Some of the archive's messages have a sequence number below 1 and cannot be read in order.");
                    }

                    lowest = sequenceNumber;
                }

                yield return page;

                if (!page.HasEarlier || lowest is not { } before)
                {
                    break;
                }

                page = reader.ReadPageBefore(before, ArchiveReader.MaximumPageSize);
            }

            if (read != total)
            {
                throw new ArchiveFormatException("The archive's messages are not in a strictly rising sequence order.");
            }
        }

        private static IEnumerable<GalleryBatch> ReadBatches(
            string folder, long localParticipantID, LowQualityIndex lowQuality, Stars? stars, CancellationToken cancellationToken)
        {
            // Closed when the batches are all read, and when the load fails or is cancelled (foreach disposes).
            using var reader = ArchiveReader.Open(folder);
            var timeline = new ConversationTimeline(reader, localParticipantID, lowQuality, stars);

            foreach (var batch in Batches(ReadPagesNewestFirst(reader), timeline, null, FirstBatchPhotos, LaterBatchPhotos, cancellationToken))
            {
                yield return batch;
            }
        }

        private static IEnumerable<GalleryBatch> BatchesCore(
            IEnumerable<ArchivePage> pagesNewestFirst,
            ConversationTimeline timeline,
            CultureInfo culture,
            int firstBatchPhotos,
            int laterBatchPhotos,
            CancellationToken cancellationToken)
        {
            var pending = new List<AttachmentCard>();
            var firstIndex = 0;

            foreach (var page in pagesNewestFirst)
            {
                cancellationToken.ThrowIfCancellationRequested();

                for (var m = page.Messages.Count - 1; m >= 0; m--)
                {
                    var message = page.Messages[m];

                    // Only an attachment whose media is an image can be a photo: a message without one is not
                    // built at all, which spares a file check for each of its other attachments.
                    if (!HasImageMedia(message, page.Media))
                    {
                        continue;
                    }

                    var shown = timeline.BuildMessageRow(message, page.Media).ShownAttachments;

                    for (var a = shown.Count - 1; a >= 0; a--)
                    {
                        var photo = shown[a];

                        if (photo.Kind != AttachmentCardKind.Image)
                        {
                            continue;
                        }

                        // A batch is closed only where a month heading ends: before a photo of another month.
                        if (pending.Count >= (firstIndex == 0 ? firstBatchPhotos : laterBatchPhotos) && MonthOf(pending[^1]) != MonthOf(photo))
                        {
                            yield return Batch(firstIndex, pending, culture);
                            firstIndex += pending.Count;
                            pending = [];
                        }

                        pending.Add(photo);
                    }
                }
            }

            if (pending.Count > 0)
            {
                yield return Batch(firstIndex, pending, culture);
            }
        }

        private static bool HasImageMedia(ArchiveMessage message, IReadOnlyDictionary<string, ArchiveMedia> media)
        {
            foreach (var attachment in message.Attachments)
            {
                if (attachment.MediaSha256 is { } sha && media.TryGetValue(sha, out var payload) && payload.MediaType == "Image")
                {
                    return true;
                }
            }

            return false;
        }

        private static (int Year, int Month) MonthOf(AttachmentCard photo) => (photo.MessageTimeLocal.Year, photo.MessageTimeLocal.Month);

        private static GalleryBatch Batch(int firstIndex, List<AttachmentCard> photos, CultureInfo culture) =>
            new(firstIndex, photos, PhotoGallery.Group(photos, culture));

        private void Run(Func<CancellationToken, IEnumerable<GalleryBatch>> batches, CancellationToken cancellationToken)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var batch in batches(cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Publish(batch);
                }

                cancellationToken.ThrowIfCancellationRequested();
                Finish(null);
            }
            catch (Exception exception)
            {
                Finish(exception);
            }
        }

        private void Publish(GalleryBatch batch)
        {
            TaskCompletionSource published;

            lock (_lock)
            {
                if (batch.FirstIndex != _photos.Count)
                {
                    throw new InvalidOperationException("The gallery's batches are not consecutive.");
                }

                _batches.Add(batch);
                _photos.AddRange(batch.Photos);
                published = _published;
                _published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            published.TrySetResult();
        }

        private void Finish(Exception? failure)
        {
            TaskCompletionSource published;
            PhotoGallery? gallery = null;

            lock (_lock)
            {
                if (failure is null)
                {
                    gallery = _gallery = PhotoGallery.FromBatches(_batches);
                }
                else
                {
                    _failure = failure;
                }

                published = _published;
            }

            published.TrySetResult();

            if (gallery is not null)
            {
                _completion.TrySetResult(gallery);
            }
            else if (failure is OperationCanceledException canceled)
            {
                _completion.TrySetCanceled(canceled.CancellationToken);
            }
            else
            {
                _completion.TrySetException(failure!);
            }
        }
    }
}
