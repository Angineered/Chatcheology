using Chatcheology.Mobile.Settings;

namespace Chatcheology.Mobile.Conversation
{
    /// <summary>
    /// The starred messages and photos of one archive (CH-38), kept in the app's settings under the
    /// archive's identity, never in the archive.
    /// </summary>
    /// <remarks>
    /// <para>A message is starred by its sequence number (a long press in the chat); a photo by its
    /// attachment (the star in the photo viewer). Each is starred on its own: starring a photo does not
    /// star its message.</para>
    /// <para>Pages are built on worker threads while a star is toggled on the UI thread, so every read and
    /// write takes a lock. <see cref="Changed"/> is raised on the thread that toggled.</para>
    /// </remarks>
    public sealed class Stars
    {
        private readonly ArchiveSettings _settings;
        private readonly string _identity;
        private readonly object _lock = new();
        private readonly HashSet<int> _messages;
        private readonly Dictionary<long, int> _attachments;

        /// <summary>The stars stored for the archive <paramref name="contentsSha256"/>.</summary>
        public Stars(ArchiveSettings settings, string contentsSha256)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentException.ThrowIfNullOrWhiteSpace(contentsSha256);

            _settings = settings;
            _identity = contentsSha256;
            _messages = [.. settings.GetStarredMessages(contentsSha256)];
            _attachments = settings.GetStarredAttachments(contentsSha256)
                .GroupBy(star => star.AttachmentID)
                .ToDictionary(group => group.Key, group => group.First().SequenceNumber);
        }

        /// <summary>Raised after a star is added or removed.</summary>
        public event EventHandler? Changed;

        /// <summary>How many messages and photos are starred.</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                {
                    return _messages.Count + _attachments.Count;
                }
            }
        }

        /// <summary>Whether the message <paramref name="sequenceNumber"/> is starred.</summary>
        public bool IsMessageStarred(int sequenceNumber)
        {
            lock (_lock)
            {
                return _messages.Contains(sequenceNumber);
            }
        }

        /// <summary>Whether the attachment <paramref name="attachmentID"/> is starred.</summary>
        public bool IsAttachmentStarred(long attachmentID)
        {
            lock (_lock)
            {
                return _attachments.ContainsKey(attachmentID);
            }
        }

        /// <summary>The starred messages, by sequence number.</summary>
        public IReadOnlyList<int> Messages
        {
            get
            {
                lock (_lock)
                {
                    return _messages.Order().ToList();
                }
            }
        }

        /// <summary>The starred attachments with their messages' sequence numbers.</summary>
        public IReadOnlyList<StarredAttachment> Attachments
        {
            get
            {
                lock (_lock)
                {
                    return _attachments
                        .Select(pair => new StarredAttachment(pair.Value, pair.Key))
                        .OrderBy(star => star.SequenceNumber)
                        .ThenBy(star => star.AttachmentID)
                        .ToList();
                }
            }
        }

        /// <summary>Stars or unstars the message <paramref name="sequenceNumber"/>. Returns whether it is starred now.</summary>
        public bool SetMessage(int sequenceNumber, bool starred)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequenceNumber);

            lock (_lock)
            {
                var changed = starred ? _messages.Add(sequenceNumber) : _messages.Remove(sequenceNumber);

                if (!changed)
                {
                    return starred;
                }

                _settings.SetStarredMessages(_identity, _messages);
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return starred;
        }

        /// <summary>
        /// Stars or unstars the attachment <paramref name="attachmentID"/> of the message
        /// <paramref name="sequenceNumber"/>. Returns whether it is starred now.
        /// </summary>
        public bool SetAttachment(int sequenceNumber, long attachmentID, bool starred)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequenceNumber);

            lock (_lock)
            {
                var changed = starred ? _attachments.TryAdd(attachmentID, sequenceNumber) : _attachments.Remove(attachmentID);

                if (!changed)
                {
                    return starred;
                }

                _settings.SetStarredAttachments(_identity, _attachments.Select(pair => new StarredAttachment(pair.Value, pair.Key)));
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return starred;
        }

        /// <summary>Sets the star flags of <paramref name="row"/> and its attachment cards from the stars.</summary>
        public void Apply(MessageRow row)
        {
            ArgumentNullException.ThrowIfNull(row);

            row.IsStarred = IsMessageStarred(row.SequenceNumber);

            foreach (var card in row.Attachments)
            {
                card.IsStarred = IsAttachmentStarred(card.AttachmentID);
            }
        }

        /// <summary>Sets the star flag of <paramref name="card"/> from the stars.</summary>
        public void Apply(AttachmentCard card)
        {
            ArgumentNullException.ThrowIfNull(card);

            card.IsStarred = IsAttachmentStarred(card.AttachmentID);
        }
    }
}
