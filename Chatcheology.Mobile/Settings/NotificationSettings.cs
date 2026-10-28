using System.Globalization;

namespace Chatcheology.Mobile.Settings
{
    /// <summary>
    /// The app's own notification settings (CH-47): one for the whole app, not per archive.
    /// </summary>
    /// <remarks>
    /// Keys: <c>app.notedDates.time</c> (the local time of day noted-date notifications are posted,
    /// <c>HH:mm</c>; 09:00 when none is stored or the value cannot be read),
    /// <c>app.notedDates.permissionAsked</c> (<c>yes</c> once Android 13+ has been asked for permission to
    /// notify, so it is asked only once; a refusal is then the system's to remember) and
    /// <c>app.notedDates.postedThrough</c> (the last day whose notifications were posted, <c>yyyy-MM-dd</c>, so a
    /// day is not notified twice). Removing an archive does not touch them.
    /// </remarks>
    public sealed class NotificationSettings
    {
        /// <summary>The time used until one is chosen: 09:00.</summary>
        public static readonly TimeOnly DefaultTime = new(9, 0);

        private const string TimeKey = "app.notedDates.time";
        private const string AskedKey = "app.notedDates.permissionAsked";
        private const string PostedKey = "app.notedDates.postedThrough";
        private const string DayFormat = "yyyy'-'MM'-'dd";

        private readonly ISettingsStore _store;

        /// <summary>Settings kept in <paramref name="store"/>.</summary>
        public NotificationSettings(ISettingsStore store)
        {
            ArgumentNullException.ThrowIfNull(store);

            _store = store;
        }

        /// <summary>The local time of day noted dates notify; whole minutes.</summary>
        public TimeOnly Time
        {
            get => TimeOnly.TryParseExact(_store.Get(TimeKey), "HH':'mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time
                : DefaultTime;
            set => _store.Set(TimeKey, value == DefaultTime ? null : value.ToString("HH':'mm", CultureInfo.InvariantCulture));
        }

        /// <summary>Whether permission to notify has already been asked for once.</summary>
        public bool PermissionAsked
        {
            get => _store.Get(AskedKey) == "yes";
            set => _store.Set(AskedKey, value ? "yes" : null);
        }

        /// <summary>The last day whose notifications were posted, or <see langword="null"/>.</summary>
        public DateOnly? PostedThrough
        {
            get => DateOnly.TryParseExact(_store.Get(PostedKey), DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : null;
            set => _store.Set(PostedKey, value?.ToString(DayFormat, CultureInfo.InvariantCulture));
        }
    }
}
