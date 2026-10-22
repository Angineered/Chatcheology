using System.Globalization;

namespace Chatcheology.Mobile.Settings
{
    /// <summary>
    /// A small key-value store for the app's own state. On Android it is the app's private preferences
    /// (excluded from backup); in tests, a dictionary.
    /// </summary>
    /// <remarks>Never written inside an archive folder.</remarks>
    public interface ISettingsStore
    {
        /// <summary>The value stored under <paramref name="key"/>, or <see langword="null"/>.</summary>
        string? Get(string key);

        /// <summary>Stores <paramref name="value"/> under <paramref name="key"/>; <see langword="null"/> removes it.</summary>
        void Set(string key, string? value);
    }

    /// <summary>What the app remembers about one archive, keyed by its <c>CONTENTS.sha256</c> hash.</summary>
    /// <remarks>
    /// <para>The hash is the archive's identity: a re-exported archive has a new one, so everything here
    /// starts again from the archive's own defaults. Values are numbers and dates only; never a name, a
    /// path or message text.</para>
    /// <para>Keys: <c>archive.&lt;hash&gt;.me</c> (participant id), <c>.position</c> (sequence number at
    /// the top of the screen), <c>.lastOpenedUtc</c>, <c>.validatedUtc</c>, <c>.validation</c> (checks
    /// passed, of checks run), <c>.starredMessages</c> (sequence numbers), <c>.starredAttachments</c>
    /// (attachment ids, each with its message's sequence number so it can be read without the whole
    /// conversation), <c>.onThisDay</c> ("off" when turned off) and <c>.onThisDayShown</c> (the last
    /// day the card was shown).</para>
    /// <para>Stars (CH-38) are private to the phone and never written into the archive. Removing the
    /// archive forgets them with everything else; an installed archive with the same hash has them.</para>
    /// </remarks>
    public sealed class ArchiveSettings
    {
        private const string Prefix = "archive.";

        private static readonly string[] Names =
        [
            "me", "position", "lastOpenedUtc", "validatedUtc", "validation",
            "starredMessages", "starredAttachments", "onThisDay", "onThisDayShown",
        ];

        private readonly ISettingsStore _store;

        /// <summary>Settings kept in <paramref name="store"/>.</summary>
        public ArchiveSettings(ISettingsStore store)
        {
            ArgumentNullException.ThrowIfNull(store);

            _store = store;
        }

        /// <summary>The participant chosen as "me", or <see langword="null"/> when the archive's own choice stands.</summary>
        public long? GetMe(string contentsSha256) => Long(Get(contentsSha256, "me"));

        /// <summary>Stores the participant chosen as "me"; <see langword="null"/> returns to the archive's choice.</summary>
        public void SetMe(string contentsSha256, long? participantID) =>
            Set(contentsSha256, "me", participantID?.ToString(CultureInfo.InvariantCulture));

        /// <summary>The sequence number that was at the top of the screen, or <see langword="null"/>.</summary>
        public int? GetPosition(string contentsSha256) => Long(Get(contentsSha256, "position")) is { } value
            and >= 1 and <= int.MaxValue ? (int)value : null;

        /// <summary>Stores the sequence number at the top of the screen.</summary>
        public void SetPosition(string contentsSha256, int sequenceNumber) =>
            Set(contentsSha256, "position", sequenceNumber.ToString(CultureInfo.InvariantCulture));

        /// <summary>When the archive was last opened, or <see langword="null"/>.</summary>
        public DateTime? GetLastOpenedUtc(string contentsSha256) => Utc(Get(contentsSha256, "lastOpenedUtc"));

        /// <summary>Stores when the archive was opened.</summary>
        public void SetLastOpenedUtc(string contentsSha256, DateTime utc) => Set(contentsSha256, "lastOpenedUtc", Format(utc));

        /// <summary>The last validation's date and checks passed of checks run, or <see langword="null"/>.</summary>
        public (DateTime Utc, int Passed, int Checks)? GetValidation(string contentsSha256)
        {
            var when = Utc(Get(contentsSha256, "validatedUtc"));
            var parts = Get(contentsSha256, "validation")?.Split('/');

            return when is { } utc && parts is [var passed, var checks]
                   && int.TryParse(passed, NumberStyles.None, CultureInfo.InvariantCulture, out var p)
                   && int.TryParse(checks, NumberStyles.None, CultureInfo.InvariantCulture, out var c)
                ? (utc, p, c)
                : null;
        }

        /// <summary>Stores a validation's date and checks passed of checks run.</summary>
        public void SetValidation(string contentsSha256, DateTime utc, int passed, int checks)
        {
            Set(contentsSha256, "validatedUtc", Format(utc));
            Set(contentsSha256, "validation", string.Create(CultureInfo.InvariantCulture, $"{passed}/{checks}"));
        }

        /// <summary>
        /// The last validation as Settings &gt; About shows it: the result, the checks passed of checks run,
        /// and the local date and time; "Not recorded" when there is none.
        /// </summary>
        public static string DescribeValidation((DateTime Utc, int Passed, int Checks)? validation)
        {
            if (validation is not { } value)
            {
                return "Not recorded";
            }

            var result = value.Passed == value.Checks && value.Checks > 0 ? "Passed" : "Did not pass";
            var when = value.Utc.ToLocalTime().ToString("d MMMM yyyy, HH:mm", CultureInfo.CurrentCulture);

            return $"{result}: {value.Passed.ToString(CultureInfo.CurrentCulture)} of {value.Checks.ToString(CultureInfo.CurrentCulture)} checks, {when}";
        }

        /// <summary>The starred messages (CH-38), by sequence number, in ascending order.</summary>
        public IReadOnlyList<int> GetStarredMessages(string contentsSha256) =>
            (Get(contentsSha256, "starredMessages") ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => Long(part))
                .OfType<long>()
                .Where(value => value is >= 1 and <= int.MaxValue)
                .Select(value => (int)value)
                .Distinct()
                .Order()
                .ToList();

        /// <summary>Stores the starred messages; none removes the key.</summary>
        public void SetStarredMessages(string contentsSha256, IEnumerable<int> sequenceNumbers)
        {
            ArgumentNullException.ThrowIfNull(sequenceNumbers);

            var list = sequenceNumbers.Distinct().Order().ToList();

            Set(
                contentsSha256,
                "starredMessages",
                list.Count == 0 ? null : string.Join(',', list.Select(n => n.ToString(CultureInfo.InvariantCulture))));
        }

        /// <summary>The starred attachments (CH-38), with their messages' sequence numbers, in that order.</summary>
        public IReadOnlyList<StarredAttachment> GetStarredAttachments(string contentsSha256)
        {
            var stars = new List<StarredAttachment>();

            foreach (var part in (Get(contentsSha256, "starredAttachments") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.Split(':') is [var sequence, var attachment]
                    && int.TryParse(sequence, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number >= 1
                    && long.TryParse(attachment, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id))
                {
                    stars.Add(new StarredAttachment(number, id));
                }
            }

            return Ordered(stars);
        }

        /// <summary>Stores the starred attachments; none removes the key.</summary>
        public void SetStarredAttachments(string contentsSha256, IEnumerable<StarredAttachment> attachments)
        {
            ArgumentNullException.ThrowIfNull(attachments);

            var list = Ordered(attachments);

            Set(
                contentsSha256,
                "starredAttachments",
                list.Count == 0
                    ? null
                    : string.Join(',', list.Select(star => string.Create(CultureInfo.InvariantCulture, $"{star.SequenceNumber}:{star.AttachmentID}"))));
        }

        /// <summary>Whether On this day (CH-39) is on for the archive: it is, unless turned off in Settings.</summary>
        public bool GetOnThisDayEnabled(string contentsSha256) => Get(contentsSha256, "onThisDay") != "off";

        /// <summary>Turns On this day on or off for the archive.</summary>
        public void SetOnThisDayEnabled(string contentsSha256, bool enabled) =>
            Set(contentsSha256, "onThisDay", enabled ? null : "off");

        /// <summary>The last day (by the phone's date) the On this day card was shown, or <see langword="null"/>.</summary>
        public DateOnly? GetOnThisDayShown(string contentsSha256) =>
            DateOnly.TryParseExact(Get(contentsSha256, "onThisDayShown"), "yyyy'-'MM'-'dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : null;

        /// <summary>Stores the day the On this day card was shown.</summary>
        public void SetOnThisDayShown(string contentsSha256, DateOnly day) =>
            Set(contentsSha256, "onThisDayShown", day.ToString("yyyy'-'MM'-'dd", CultureInfo.InvariantCulture));

        /// <summary>Forgets everything stored for the archive.</summary>
        public void Forget(string contentsSha256)
        {
            foreach (var name in Names)
            {
                Set(contentsSha256, name, null);
            }
        }

        private string? Get(string contentsSha256, string name) => _store.Get(Key(contentsSha256, name));

        private void Set(string contentsSha256, string name, string? value) => _store.Set(Key(contentsSha256, name), value);

        private static string Key(string contentsSha256, string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(contentsSha256);

            return Prefix + contentsSha256 + "." + name;
        }

        private static long? Long(string? text) =>
            long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

        private static List<StarredAttachment> Ordered(IEnumerable<StarredAttachment> stars) =>
            stars.Distinct().OrderBy(star => star.SequenceNumber).ThenBy(star => star.AttachmentID).ToList();

        private static string Format(DateTime utc) =>
            utc.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'", CultureInfo.InvariantCulture);

        private static DateTime? Utc(string? text) =>
            DateTime.TryParseExact(
                text,
                "yyyy'-'MM'-'dd'T'HH':'mm':'ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var value)
                ? value
                : null;
    }

    /// <summary>A starred attachment (CH-38): the attachment, and the message it belongs to.</summary>
    /// <param name="SequenceNumber">Its message's sequence number, so it is found without reading the whole conversation.</param>
    /// <param name="AttachmentID">The attachment.</param>
    public readonly record struct StarredAttachment(int SequenceNumber, long AttachmentID);
}
