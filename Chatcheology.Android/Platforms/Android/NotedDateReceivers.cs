using Android.App;
using Android.Content;
using AndroidLog = Android.Util.Log;

namespace Chatcheology.Android
{
    /// <summary>
    /// The noted-date alarm (CH-47): posts the day's notifications and schedules the next. Not exported: only
    /// the app's own <see cref="PendingIntent"/>, which the system delivers on the app's behalf, reaches it.
    /// </summary>
    [BroadcastReceiver(Name = "org.chatcheology.viewer.NotedDateAlarmReceiver", Exported = false, Enabled = true)]
    public sealed class NotedDateAlarmReceiver : BroadcastReceiver
    {
        /// <inheritdoc />
        public override void OnReceive(Context? context, Intent? intent)
        {
            var day = intent?.GetStringExtra(NotedDateNotifier.DayExtra);

            Run(this, () =>
            {
                NotedDateNotifier.PostDue(day);
                NotedDateNotifier.Reschedule();
            });
        }

        /// <summary>Runs <paramref name="work"/> off the main thread and tells the system when it is done. Never throws.</summary>
        internal static void Run(BroadcastReceiver receiver, Action work)
        {
            var pending = receiver.GoAsync();

            Task.Run(() =>
            {
                try
                {
                    work();
                }
                catch (Exception exception)
                {
                    AndroidLog.Warn(AppState.LogTag, $"noted-dates receiver failed: {exception.GetType().Name}");
                }
                finally
                {
                    pending?.Finish();
                }
            });
        }
    }

    /// <summary>
    /// Schedules the noted-date alarm again after the phone restarts (alarms do not survive a restart).
    /// </summary>
    /// <remarks>
    /// Not exported. <c>BOOT_COMPLETED</c> is sent by the system, which may deliver to any receiver of the app
    /// whether or not it is exported; <c>android:exported="false"</c> only keeps other apps out. The intent filter
    /// is needed for the system to find it; the action is checked again here.
    /// </remarks>
    [BroadcastReceiver(Name = "org.chatcheology.viewer.NotedDateBootReceiver", Exported = false, Enabled = true)]
    [IntentFilter([Intent.ActionBootCompleted])]
    public sealed class NotedDateBootReceiver : BroadcastReceiver
    {
        /// <inheritdoc />
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action != Intent.ActionBootCompleted)
            {
                return;
            }

            NotedDateAlarmReceiver.Run(this, () => NotedDateNotifier.Reschedule());
        }
    }
}
