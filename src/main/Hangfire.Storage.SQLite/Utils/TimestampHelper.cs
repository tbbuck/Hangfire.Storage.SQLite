using System;

namespace Hangfire.Storage.SQLite.Utils
{
    /// <summary>
    /// Monotonic timestamp helper used to throttle heartbeat (keep-alive) queries.
    /// Based on the equivalent helper in Hangfire and Hangfire.PostgreSql.
    /// </summary>
    internal static class TimestampHelper
    {
        public static long GetTimestamp()
        {
            return Environment.TickCount;
        }

        public static TimeSpan Elapsed(long timestamp)
        {
            return Elapsed(GetTimestamp(), timestamp);
        }

        public static TimeSpan Elapsed(long now, long timestamp)
        {
            // unchecked int subtraction so the value remains correct across Environment.TickCount wrap-around.
            return TimeSpan.FromMilliseconds(unchecked((int)now - (int)timestamp));
        }
    }
}
