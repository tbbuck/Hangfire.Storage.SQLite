using Hangfire.Storage.SQLite.Entities;
using Hangfire.Storage.SQLite.Test.Utils;
using System;
using Xunit;

namespace Hangfire.Storage.SQLite.Test
{
    public class Issue79ReproFacts
    {
        [Fact]
        // The exact scenario from upstream issue #79 (shortened): the public Acquire overload's
        // heartbeat must not race the consumer connection.
        public void Use_Connection_When_Heartbeat_Fires()
        {
            using var database = ConnectionUtils.CreateConnection();

            using var slock = SQLiteDistributedLock.Acquire("resource1", TimeSpan.FromSeconds(10), database,
                new SQLiteStorageOptions { DistributedLockLifetime = TimeSpan.FromSeconds(1) }); // heartbeat ~200ms

            var start = DateTime.UtcNow;
            while (DateTime.UtcNow - start < TimeSpan.FromSeconds(8))
            {
                database.Database.Insert(new JobParameter
                {
                    ExpireAt = start.AddSeconds(15),
                    JobId = 13,
                    Name = "MyParameter",
                    Value = "MyValue",
                });
            }
        }
    }
}
