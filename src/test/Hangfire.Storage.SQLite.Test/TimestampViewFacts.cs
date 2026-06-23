using Hangfire.Storage.SQLite.Entities;
using Hangfire.Storage.SQLite.Test.Utils;
using System;
using System.Globalization;
using Xunit;

namespace Hangfire.Storage.SQLite.Test
{
    public class TimestampViewFacts
    {
        [Fact]
        public void Init_CreatesUtcViews_ForAllTimestampTables()
        {
            var connection = ConnectionUtils.CreateConnection();

            var viewCount = connection.Database.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'view' AND name LIKE '%\\_utc' ESCAPE '\\'");

            Assert.Equal(11, viewCount);
        }

        [Fact]
        public void JobUtcView_ExposesTicks_AsIso8601Utc()
        {
            var connection = ConnectionUtils.CreateConnection();

            var createdAt = new DateTime(2026, 6, 23, 12, 34, 56, 789, DateTimeKind.Utc);
            var expireAt = new DateTime(2030, 1, 2, 3, 4, 5, 250, DateTimeKind.Utc);

            var job = new HangfireJob
            {
                StateName = "Enqueued",
                InvocationData = "{}",
                Arguments = "[]",
                CreatedAt = createdAt,
                ExpireAt = expireAt,
            };
            connection.Database.Insert(job);

            var createdAtUtc = connection.Database.ExecuteScalar<string>(
                "SELECT CreatedAtUtc FROM \"Job_utc\" WHERE Id = ?", job.Id);
            var expireAtUtc = connection.Database.ExecuteScalar<string>(
                "SELECT ExpireAtUtc FROM \"Job_utc\" WHERE Id = ?", job.Id);

            AssertRoughlyEqual(createdAt, createdAtUtc);
            AssertRoughlyEqual(expireAt, expireAtUtc);
        }

        [Fact]
        public void UtcView_ExposesUnderlyingRawTickColumn_Unchanged()
        {
            var connection = ConnectionUtils.CreateConnection();

            var expireAt = new DateTime(2031, 5, 6, 7, 8, 9, DateTimeKind.Utc);
            var job = new HangfireJob
            {
                StateName = "Enqueued",
                InvocationData = "{}",
                Arguments = "[]",
                CreatedAt = DateTime.UtcNow,
                ExpireAt = expireAt,
            };
            connection.Database.Insert(job);

            // The view must still surface the original tick value (via SELECT *), not just the converted one.
            var rawTicks = connection.Database.ExecuteScalar<long>(
                "SELECT ExpireAt FROM \"Job_utc\" WHERE Id = ?", job.Id);

            Assert.Equal(expireAt.Ticks, rawTicks);
        }

        private static void AssertRoughlyEqual(DateTime expectedUtc, string actualText)
        {
            Assert.False(string.IsNullOrWhiteSpace(actualText));

            var parsed = DateTime.Parse(actualText, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

            // The view rounds ticks to millisecond precision, so allow a 1ms tolerance.
            Assert.True((parsed - expectedUtc).Duration() <= TimeSpan.FromMilliseconds(1),
                $"Expected ~{expectedUtc:O} but view returned '{actualText}'.");
        }
    }
}
