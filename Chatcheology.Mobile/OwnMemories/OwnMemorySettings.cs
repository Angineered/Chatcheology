using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Chatcheology.Mobile.OwnMemories
{
    /// <summary>
    /// The per-phone settings of the own memories (CH-54), kept in the own memories file, never under
    /// <c>archive.&lt;hash&gt;</c>: a re-import or removing an archive leaves them as they are.
    /// </summary>
    /// <remarks>
    /// Read by <c>ArchiveSession</c> (the layer switch, step 1b) and <c>NotedDateSchedule.ReadOwn</c> (reminders, mutes,
    /// posted day); the alarm receiver records the posted day with <see cref="PostedDay"/>. Keys of <c>OwnSetting</c>: <c>layer</c>
    /// (<c>off</c> when the My memories layer is turned off), <c>reminders</c> (<c>off</c> when their daily
    /// notification is turned off) and <c>postedDay</c> (<c>yyyy-MM-dd</c>). Notify mutes are rows of
    /// <c>OwnNotifyMute</c> by memory identity, removed with the memory.
    /// </remarks>
    public sealed class OwnMemorySettings
    {
        private const string LayerKey = "layer";
        private const string RemindersKey = "reminders";
        private const string PostedDayKey = "postedDay";
        private const string Off = "off";
        private const string DateFormat = "yyyy'-'MM'-'dd";

        private readonly OwnMemoryStore _store;

        internal OwnMemorySettings(OwnMemoryStore store)
        {
            _store = store;
        }

        /// <summary>Whether the My memories layer is on: it is, unless turned off.</summary>
        public bool LayerOn
        {
            get => Get(LayerKey) != Off;
            set => Set(LayerKey, value ? null : Off);
        }

        /// <summary>Whether the daily own memories notification is on: it is, unless turned off.</summary>
        public bool RemindersOn
        {
            get => Get(RemindersKey) != Off;
            set => Set(RemindersKey, value ? null : Off);
        }

        /// <summary>The last day (by the phone's date) the own memories notification was posted, or <see langword="null"/>.</summary>
        public DateOnly? PostedDay
        {
            get => DateOnly.TryParseExact(Get(PostedDayKey), DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day
                : null;
            set => Set(PostedDayKey, value?.ToString(DateFormat, CultureInfo.InvariantCulture));
        }

        /// <summary>Whether the memory <paramref name="id"/> is muted (not notified).</summary>
        public bool IsMuted(Guid id)
        {
            using var connection = _store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM OwnNotifyMute WHERE MemoryGuid = $guid);";
            command.Parameters.AddWithValue("$guid", OwnMemoryStore.GuidText(id));
            return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 1;
        }

        /// <summary>Every muted memory's identity.</summary>
        public IReadOnlySet<Guid> Muted()
        {
            using var connection = _store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MemoryGuid FROM OwnNotifyMute;";
            using var reader = command.ExecuteReader();
            var muted = new HashSet<Guid>();

            while (reader.Read())
            {
                muted.Add(Guid.ParseExact(reader.GetString(0), "D"));
            }

            return muted;
        }

        /// <summary>Mutes or unmutes the memory <paramref name="id"/>.</summary>
        /// <returns>False when the memory is not there (nothing is stored).</returns>
        public bool SetMuted(Guid id, bool muted)
        {
            using var connection = _store.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$guid", OwnMemoryStore.GuidText(id));
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM OwnMemory WHERE MemoryGuid = $guid);";

            if (Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                return false;
            }

            command.CommandText = muted
                ? "INSERT OR IGNORE INTO OwnNotifyMute (MemoryGuid) VALUES ($guid);"
                : "DELETE FROM OwnNotifyMute WHERE MemoryGuid = $guid;";
            command.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }

        private string? Get(string key)
        {
            using var connection = _store.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT SettingValue FROM OwnSetting WHERE SettingKey = $key;";
            command.Parameters.AddWithValue("$key", key);
            return command.ExecuteScalar() as string;
        }

        private void Set(string key, string? value)
        {
            using var connection = _store.Open();
            using var command = connection.CreateCommand();
            command.Parameters.AddWithValue("$key", key);

            if (value is null)
            {
                command.CommandText = "DELETE FROM OwnSetting WHERE SettingKey = $key;";
            }
            else
            {
                command.CommandText =
                    "INSERT INTO OwnSetting (SettingKey, SettingValue) VALUES ($key, $value) " +
                    "ON CONFLICT (SettingKey) DO UPDATE SET SettingValue = excluded.SettingValue;";
                command.Parameters.AddWithValue("$value", value);
            }

            command.ExecuteNonQuery();
        }
    }
}
