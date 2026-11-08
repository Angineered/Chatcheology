using System.Globalization;
using Android.App;
using Android.Content;
using Android.OS;
using AndroidX.Core.App;
using Chatcheology.Mobile.Library;
using Chatcheology.Mobile.Notifications;
using Chatcheology.Mobile.OwnMemories;
using Chatcheology.Mobile.Settings;
using AndroidLog = Android.Util.Log;

namespace Chatcheology.Android
{
    /// <summary>
    /// Noted-date notifications on the phone (CH-47 step 3). The schedule itself is
    /// <see cref="NotedDateSchedule"/>; this posts, schedules and cancels.
    /// </summary>
    /// <remarks>
    /// <para><strong>Alarm.</strong> One inexact alarm (<c>setAndAllowWhileIdle</c>, no exact-alarm permission)
    /// for the next moment over every archive; <see cref="NotedDateAlarmReceiver"/> posts that day's notifications
    /// and schedules the next; <see cref="NotedDateBootReceiver"/> schedules again after a restart. Rescheduled on
    /// app start and after any change to noted dates, mutes, the On this day switch, the time, an import or a
    /// removal.</para>
    /// <para><strong>Privacy.</strong> The notification says only "A noted date today" or "N noted dates today"
    /// (<see cref="NotedDateSchedule.TextFor"/>): no title and no conversation name, so nothing personal reaches a
    /// lock screen whatever its setting; the titles are on the card after the tap. It is still posted as
    /// <c>VISIBILITY_PRIVATE</c> on a channel whose lock-screen visibility is private, with a public version
    /// that says the same. The log says counts only.</para>
    /// <para><strong>Once a day per archive.</strong> Each archive's posted day is kept in its settings; an alarm
    /// that fires for today posts only the archives not yet posted today and records them, even when
    /// notifications are off (so a late-today catch-up does not repeat every minute).</para>
    /// <para><strong>My memories (CH-54).</strong> The phone owner's own yearly memories have one notification of their
    /// own per phone (<see cref="NotedDateSchedule.OwnNotificationId"/>, "A memory today" / "N memories today"), posted
    /// once a day (the posted day is kept in the own store). A tap opens the last-opened archive at today's On this day
    /// card, or the start screen when none was opened. When the own store is busy past its retries, a short retry alarm
    /// is set (<see cref="NotedDateSchedule.WithRetry"/>) instead of leaving the reminders silent.</para>
    /// </remarks>
    internal static class NotedDateNotifier
    {
        /// <summary>The notification channel's identifier.</summary>
        internal const string ChannelId = "noted-dates";

        /// <summary>The extra on the alarm's intent: the day it was scheduled for, <c>yyyy-MM-dd</c>.</summary>
        internal const string DayExtra = "org.chatcheology.viewer.extra.NOTED_DAY";

        /// <summary>The extra on a tap's intent: the archive's folder name.</summary>
        internal const string ArchiveExtra = "org.chatcheology.viewer.extra.ARCHIVE";

        private const string DayFormat = "yyyy'-'MM'-'dd";

        private static readonly object Lock = new();

        private static Context Context => global::Android.App.Application.Context;

        /// <summary>
        /// Schedules again off the UI thread and, when something can notify, asks for permission to notify
        /// (Android 13+, once). Never throws.
        /// </summary>
        internal static async Task UpdateAsync()
        {
            try
            {
                var notifying = await Task.Run(Reschedule);

                if (notifying)
                {
                    await MainThread.InvokeOnMainThreadAsync(AskPermissionOnceAsync);
                }
            }
            catch (Exception exception)
            {
                AndroidLog.Warn(AppState.LogTag, $"noted-dates reschedule failed: {exception.GetType().Name}");
            }
        }

        /// <summary>
        /// Reads every installed archive's noted dates and sets the alarm for the next moment, or cancels it when
        /// nothing can notify. Returns whether any noted date can notify. Reads every archive: off the UI thread.
        /// </summary>
        internal static bool Reschedule()
        {
            lock (Lock)
            {
                var settings = new ArchiveSettings(new AppState.PreferencesStore());
                var archives = Read(settings);
                var own = NotedDateSchedule.ReadOwnWithRetry(AppState.OwnStore, archives, settings);
                var now = DateTime.Now;
                var next = NotedDateSchedule.WithRetry(NotedDateSchedule.Next(archives, own.Reminders, Notifications.Time, now), own.MayPass, now);
                var alarms = (AlarmManager?)Context.GetSystemService(Context.AlarmService);

                if (alarms is not null)
                {
                    if (next is null)
                    {
                        alarms.Cancel(AlarmIntent(null));
                    }
                    else
                    {
                        var utc = NotedDateSchedule.ToUtc(next.LocalMoment, TimeZoneInfo.Local);
                        var millis = new DateTimeOffset(utc).ToUnixTimeMilliseconds();

                        alarms.SetAndAllowWhileIdle(AlarmType.RtcWakeup, millis, AlarmIntent(next.Day));
                    }
                }

                AndroidLog.Info(AppState.LogTag, "noted-dates " + NotedDateSchedule.DescribeCounts(archives, own.Reminders, next));

                return archives.Any(archive => archive.CanNotify) || own.Reminders?.CanNotify == true;
            }
        }

        /// <summary>
        /// The alarm fired: posts the notifications of <paramref name="scheduledDay"/> when that is still today
        /// (a late alarm after midnight posts nothing), read afresh so a change since scheduling counts.
        /// </summary>
        internal static void PostDue(string? scheduledDay)
        {
            lock (Lock)
            {
                var now = DateTime.Now;
                var today = DateOnly.FromDateTime(now);

                if (!DateOnly.TryParseExact(scheduledDay, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) || day != today)
                {
                    AndroidLog.Info(AppState.LogTag, "noted-dates posted=0 stale=yes");
                    return;
                }

                // Step 2 review S1: a retry alarm before today's time posts nothing; the reschedule after this sets the real one.
                if (!NotedDateSchedule.MayPostNow(day, Notifications.Time, now))
                {
                    AndroidLog.Info(AppState.LogTag, "noted-dates posted=0 early=yes");
                    return;
                }

                var settings = new ArchiveSettings(new AppState.PreferencesStore());
                var archives = Read(settings);
                var due = NotedDateSchedule.DueOn(archives, today);
                var manager = Manager;

                // CH-54: the own memories' one notification, read afresh. A store that cannot be read now posts
                // nothing for it here; the reschedule after this sets a retry.
                var own = NotedDateSchedule.ReadOwnWithRetry(AppState.OwnStore, archives, settings);
                var dueOwn = NotedDateSchedule.OwnDueOn(own.Reminders, today);

                // Today is done for these archives, posted or not: one per archive per day, and no catch-up loop
                // while notifications are off.
                foreach (var notification in due)
                {
                    settings.SetNotedDatesPostedThrough(notification.ContentsSha256, today);
                }

                if (dueOwn is not null)
                {
                    try
                    {
                        AppState.OwnStore.Settings.PostedDay = today;
                    }
                    catch (Exception exception) when (exception is OwnMemoriesStoreException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
                    {
                        // Not recorded: it may be posted once more today, never lost.
                        AndroidLog.Info(AppState.LogTag, "noted-dates own-posted-day=unrecorded");
                    }
                }

                var posted = due.Count + (dueOwn is null ? 0 : 1);

                if (manager is null || !manager.AreNotificationsEnabled())
                {
                    AndroidLog.Info(AppState.LogTag, $"noted-dates posted=0 due={posted} notifications=off");
                    return;
                }

                EnsureChannel();

                foreach (var notification in due)
                {
                    manager.Notify(NotedDateSchedule.NotificationId(notification.InstallId), Build(notification));
                }

                if (dueOwn is not null)
                {
                    manager.Notify(NotedDateSchedule.OwnNotificationId, BuildOwn(dueOwn));
                }

                AndroidLog.Info(AppState.LogTag, $"noted-dates posted={posted} own={(dueOwn is null ? 0 : 1)}");
            }
        }

        /// <summary>Removes an archive's notification when it is removed (the alarm is rescheduled separately).</summary>
        internal static void Cancel(string installId)
        {
            try
            {
                Manager?.Cancel(NotedDateSchedule.NotificationId(installId));
            }
            catch (Exception exception) when (exception is Java.Lang.Exception or ArgumentException)
            {
                // Nothing to cancel.
            }
        }

        /// <summary>Whether the app may post notifications (the permission on Android 13+, and the system switch).</summary>
        internal static bool NotificationsEnabled => Manager?.AreNotificationsEnabled() == true;

        private static NotificationManagerCompat? Manager => NotificationManagerCompat.From(Context);

        /// <summary>Opens the system's notification settings for this app.</summary>
        internal static void OpenSystemSettings()
        {
            var intent = new Intent(global::Android.Provider.Settings.ActionAppNotificationSettings)
                .PutExtra(global::Android.Provider.Settings.ExtraAppPackage, Context.PackageName)
                .AddFlags(ActivityFlags.NewTask);

            Context.StartActivity(intent);
        }

        /// <summary>The app's notification settings, in its private preferences.</summary>
        internal static NotificationSettings Notifications { get; } = new(new AppState.PreferencesStore());

        private static IReadOnlyList<ArchiveNotedDates> Read(ArchiveSettings settings) =>
            NotedDateSchedule.Read(new ArchiveLibrary(FileSystem.AppDataDirectory), settings);

        /// <summary>Android 13+: asks once for permission to notify. A refusal is the system's to remember; Settings says so.</summary>
        private static async Task AskPermissionOnceAsync()
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(33) || Notifications.PermissionAsked || NotificationsEnabled)
            {
                return;
            }

            Notifications.PermissionAsked = true;

            var status = await Permissions.RequestAsync<Permissions.PostNotifications>();
            AndroidLog.Info(AppState.LogTag, $"noted-dates permission={(status == PermissionStatus.Granted ? "granted" : "refused")}");
        }

        private static PendingIntent AlarmIntent(DateOnly? day)
        {
            var intent = new Intent(Context, typeof(NotedDateAlarmReceiver));

            if (day is { } value)
            {
                intent.PutExtra(DayExtra, value.ToString(DayFormat, CultureInfo.InvariantCulture));
            }

            return PendingIntent.GetBroadcast(Context, 0, intent, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        }

        private static void EnsureChannel()
        {
            var manager = (NotificationManager?)Context.GetSystemService(Context.NotificationService);

            if (manager is null || manager.GetNotificationChannel(ChannelId) is not null)
            {
                return;
            }

            var channel = new NotificationChannel(ChannelId, "Noted dates", NotificationImportance.Default)
            {
                Description = "A reminder on the morning of a noted date.",
                LockscreenVisibility = NotificationVisibility.Private,
            };

            manager.CreateNotificationChannel(channel);
        }

        private static Notification Build(DueNotification due) =>
            Build(NotedDateSchedule.NotificationId(due.InstallId), due.Text, due.InstallId);

        /// <summary>
        /// The own memories notification (CH-54): generic text, never a title; a tap opens the last-opened archive at
        /// today's On this day card, or, with none, the start screen (no archive extra).
        /// </summary>
        private static Notification BuildOwn(DueOwnNotification due) =>
            Build(NotedDateSchedule.OwnNotificationId, due.Text, due.TapInstallId);

        private static Notification Build(int id, string text, string? installId)
        {
            var open = new Intent(Context, typeof(MainActivity))
                .SetFlags(ActivityFlags.NewTask | ActivityFlags.SingleTop | ActivityFlags.ClearTop);

            if (installId is not null)
            {
                open.PutExtra(ArchiveExtra, installId);
            }

            var tap = PendingIntent.GetActivity(Context, id, open, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);

            // Nothing sensitive is in it (no title, no conversation), so the public version says the same.
            var publicBuilder = new NotificationCompat.Builder(Context, ChannelId);
            publicBuilder.SetSmallIcon(Resource.Drawable.ic_stat_chatcheology);
            publicBuilder.SetContentTitle(text);
            publicBuilder.SetContentIntent(tap);
            publicBuilder.SetAutoCancel(true);

            var builder = new NotificationCompat.Builder(Context, ChannelId);
            builder.SetSmallIcon(Resource.Drawable.ic_stat_chatcheology);
            builder.SetContentTitle(text);
            builder.SetContentIntent(tap);
            builder.SetAutoCancel(true);
            builder.SetCategory(NotificationCompat.CategoryReminder);
            builder.SetVisibility(NotificationCompat.VisibilityPrivate);
            builder.SetPublicVersion(publicBuilder.Build());

            return builder.Build()!;
        }
    }
}
