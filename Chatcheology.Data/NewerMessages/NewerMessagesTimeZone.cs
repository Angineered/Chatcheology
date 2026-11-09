using System.Globalization;

namespace Chatcheology.Data.NewerMessages
{
    /// <summary>
    /// UTC to local time for newer messages: the Africa/Johannesburg zone (Angie, 2026-11-08, D3).
    /// </summary>
    /// <remarks>
    /// <para>Africa/Johannesburg (South African Standard Time) has been UTC+02:00 with no daylight saving
    /// since 1944-03-19 (tz database). That rule is written out here instead of read from the system's
    /// time zone data, because the tools run with invariant globalization (no ICU, so IANA names do not
    /// resolve on Windows) and because the same instant must give the same local time on every machine.
    /// A test checks it against the system's zone wherever the system has one.</para>
    /// <para>The main workspace's local times are the export's own, which use the same +02:00; so the
    /// hand-off day of the two sources is the same day.</para>
    /// </remarks>
    public static class NewerMessagesTimeZone
    {
        /// <summary>The zone's IANA name, as recorded in the file's marker row.</summary>
        public const string Name = "Africa/Johannesburg";

        /// <summary>The zone's only offset from <see cref="ValidFromUtc"/> on.</summary>
        public static readonly TimeSpan Offset = TimeSpan.FromHours(2);

        /// <summary>The end of the zone's last daylight-saving period (1944-03-19 02:00 local, 00:00 UTC).</summary>
        public static readonly DateTimeOffset ValidFromUtc = new(1944, 3, 19, 0, 0, 0, TimeSpan.Zero);

        /// <summary>The local time of a UTC instant in milliseconds since the epoch, or null when outside the rule's range.</summary>
        public static DateTime? ToLocal(long utcMilliseconds)
        {
            if (utcMilliseconds < ValidFromUtc.ToUnixTimeMilliseconds()
                || utcMilliseconds > DateTimeOffset.MaxValue.ToUnixTimeMilliseconds() - 86_400_000)
            {
                return null;
            }

            return DateTimeOffset.FromUnixTimeMilliseconds(utcMilliseconds).ToOffset(Offset).DateTime;
        }

        /// <summary>The stored local form, <c>yyyy-MM-ddTHH:mm:ss</c> (whole seconds, as the archive keeps it).</summary>
        public static string Format(DateTime local) =>
            local.ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss", CultureInfo.InvariantCulture);
    }
}
