using Hangfire.Storage.SQLite.Entities;
using Hangfire.Storage.SQLite.Test.Utils;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using Xunit;

namespace Hangfire.Storage.SQLite.Test
{
    public class SQLiteDistributedLockHeartbeatFacts
    {
        [Fact]
        public void Heartbeat_RenewsExpiration_OnADedicatedConnection()
        {
            var options = new SQLiteStorageOptions { DistributedLockLifetime = TimeSpan.FromSeconds(1) }; // beat ~200ms
            var storage = ConnectionUtils.CreateStorage(options);
            using var consumer = storage.CreateAndOpenConnection();

            DateTime ReadExpireAt()
            {
                using var reader = storage.CreateAndOpenConnection();
                return reader.DistributedLockRepository.First(x => x.Resource == "res-hb").ExpireAt;
            }

            using (SQLiteDistributedLock.Acquire("res-hb", TimeSpan.FromSeconds(5), consumer, options, storage))
            {
                var before = ReadExpireAt();
                Thread.Sleep(700); // allow a few heartbeats to fire
                var after = ReadExpireAt();

                Assert.True(after > before, $"Heartbeat should have renewed ExpireAt; before={before:O} after={after:O}");
            }

            // After dispose, the lock row is released.
            using var check = storage.CreateAndOpenConnection();
            Assert.Empty(check.DistributedLockRepository.Where(x => x.Resource == "res-hb").ToList());
        }

        [Fact]
        public void Lock_Heartbeat_DoesNotShareConsumerConnection_UnderConcurrentUse()
        {
            // Regression for issue #79: the heartbeat timer must run on its own connection so it never
            // races the consumer's (NoMutex, non-thread-safe) connection. Uses a file-backed DB (WAL)
            // so genuine concurrent writes from both connections are exercised.
            var dbPath = Path.Combine(Path.GetTempPath(), $"hf_lock_{Guid.NewGuid():n}.db");
            try
            {
                var options = new SQLiteStorageOptions { DistributedLockLifetime = TimeSpan.FromSeconds(1) };
                var storage = new SQLiteStorage(dbPath, options);

                using (var consumer = storage.CreateAndOpenConnection())
                using (SQLiteDistributedLock.Acquire("res-stress", TimeSpan.FromSeconds(5), consumer, options, storage))
                {
                    // Hammer the consumer connection while the heartbeat fires concurrently.
                    var deadline = DateTime.UtcNow.AddSeconds(2);
                    var n = 0;
                    while (DateTime.UtcNow < deadline)
                    {
                        consumer.Database.Insert(new JobParameter
                        {
                            JobId = 1,
                            Name = $"p{n++}",
                            Value = "v",
                            ExpireAt = DateTime.UtcNow.AddMinutes(5),
                        });
                    }

                    Assert.True(n > 0);
                }

                storage.Dispose();
            }
            finally
            {
                foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
                {
                    try { File.Delete(f); } catch { /* best effort */ }
                }
            }
        }
    }
}
