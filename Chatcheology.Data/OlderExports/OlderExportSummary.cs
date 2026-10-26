using Chatcheology.Core.Models;

namespace Chatcheology.Data.OlderExports
{
    /// <summary>
    /// The dry-run aggregates of one older export: counts, the layout and the period only.
    /// </summary>
    /// <remarks>
    /// Holds no message text, sender name, file name or path, so it can be printed by default.
    /// Sender names are available only through <see cref="CountBySender"/>, which a caller must ask
    /// for explicitly.
    /// </remarks>
    public sealed class OlderExportSummary
    {
        /// <summary>The detected layout, or null for an export with no messages.</summary>
        public required WhatsAppHeaderShape? Shape { get; init; }

        /// <summary>The earliest message timestamp, or null when there are no messages.</summary>
        public required DateTime? First { get; init; }

        /// <summary>The latest message timestamp, or null when there are no messages.</summary>
        public required DateTime? Last { get; init; }

        /// <summary>All logical messages.</summary>
        public required int MessageCount { get; init; }

        /// <summary>Logical messages per calendar year of their timestamp, in year order.</summary>
        public required IReadOnlyList<KeyValuePair<int, int>> MessagesPerYear { get; init; }

        /// <summary>System messages (no sender).</summary>
        public required int SystemMessageCount { get; init; }

        /// <summary><c>(file attached)</c> messages whose file is in the export's own folder.</summary>
        public required int FileAttachedPresentCount { get; init; }

        /// <summary><c>(file attached)</c> messages whose file is not in the export's own folder.</summary>
        public required int FileAttachedAbsentCount { get; init; }

        /// <summary>
        /// Of the absent ones, how many a file of exactly that name in the fallback folder would
        /// resolve, or null when no fallback folder was given.
        /// </summary>
        public required int? FileAttachedAbsentButInFallbackCount { get; init; }

        /// <summary>Exact <c>&lt;Media omitted&gt;</c> messages.</summary>
        public required int MediaOmittedCount { get; init; }

        /// <summary>Distinct user-message sender names, compared exactly.</summary>
        public required int DistinctSenderCount { get; init; }

        /// <summary>
        /// Summarises <paramref name="export"/>. With <paramref name="fallbackFolder"/>, also
        /// counts the absent file names a file of exactly that name in that folder would resolve.
        /// </summary>
        public static OlderExportSummary From(OlderExportSource export, string? fallbackFolder = null)
        {
            ArgumentNullException.ThrowIfNull(export);

            var messages = export.Messages;

            HashSet<string>? fallbackNames = null;

            if (fallbackFolder is not null)
            {
                if (!Directory.Exists(fallbackFolder))
                {
                    throw new DirectoryNotFoundException(
                        "The fallback media folder does not exist. Nothing has been read.");
                }

                fallbackNames = OlderExportSource.ListFallbackMediaFileNames(fallbackFolder);
            }

            int present = 0, absent = 0, inFallback = 0;

            foreach (var message in messages)
            {
                var name = message.AttachedFileName;

                if (name is null)
                {
                    continue;
                }

                if (export.MediaFileNames.Contains(name))
                {
                    present++;
                    continue;
                }

                absent++;

                if (fallbackNames is not null && fallbackNames.Contains(name))
                {
                    inFallback++;
                }
            }

            return new OlderExportSummary
            {
                Shape = export.Shape,
                First = messages.Count == 0 ? null : messages.Min(message => message.MessageDateTime),
                Last = messages.Count == 0 ? null : messages.Max(message => message.MessageDateTime),
                MessageCount = messages.Count,
                MessagesPerYear = messages
                    .GroupBy(message => message.MessageDateTime.Year)
                    .OrderBy(group => group.Key)
                    .Select(group => new KeyValuePair<int, int>(group.Key, group.Count()))
                    .ToList(),
                SystemMessageCount = messages.Count(message => message.MessageType == MessageType.System),
                FileAttachedPresentCount = present,
                FileAttachedAbsentCount = absent,
                FileAttachedAbsentButInFallbackCount = fallbackNames is null ? null : inFallback,
                MediaOmittedCount = messages.Count(message => message.IsMediaPlaceholder),
                DistinctSenderCount = OlderExportImporter.DistinctSenders(messages).Count,
            };
        }

        /// <summary>
        /// The sender names of <paramref name="export"/> with their message counts, most messages
        /// first. Personal data: for the person choosing the roles, shown only on explicit request.
        /// </summary>
        public static IReadOnlyList<KeyValuePair<string, int>> CountBySender(OlderExportSource export)
        {
            ArgumentNullException.ThrowIfNull(export);

            return export.Messages
                .Where(message => message.MessageType == MessageType.User && message.Sender is not null)
                .GroupBy(message => message.Sender!, StringComparer.Ordinal)
                .OrderByDescending(group => group.Count())
                .ThenBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new KeyValuePair<string, int>(group.Key, group.Count()))
                .ToList();
        }
    }
}
