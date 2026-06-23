using Hangfire.Server;
using Hangfire.Storage.SQLite.Entities;
using Hangfire.Storage.SQLite.Test.Utils;
using System;
using System.Linq;
using System.Threading;
using Xunit;

namespace Hangfire.Storage.SQLite.Test
{
    public class SlidingInvisibilityTimeoutFacts
    {
        private const string Queue = "default";

        [Fact]
        public void HeartbeatProcess_IsRegistered_OnlyWhenSlidingEnabled()
        {
            var enabled = ConnectionUtils.CreateStorage(new SQLiteStorageOptions { UseSlidingInvisibilityTimeout = true });
            var disabled = ConnectionUtils.CreateStorage(new SQLiteStorageOptions { UseSlidingInvisibilityTimeout = false });

            Assert.Contains(enabled.GetStorageWideProcesses(), p => p is SQLiteHeartbeatProcess);
            Assert.DoesNotContain(disabled.GetStorageWideProcesses(), p => p is SQLiteHeartbeatProcess);
            Assert.NotNull(enabled.HeartbeatProcess);
            Assert.Null(disabled.HeartbeatProcess);
        }

        [Fact]
        public void KeepAlive_SlidesFetchedAt_Forward_ForJobWeStillOwn()
        {
            var storage = ConnectionUtils.CreateStorage(new SQLiteStorageOptions
            {
                UseSlidingInvisibilityTimeout = true,
                InvisibilityTimeout = TimeSpan.FromMilliseconds(50), // heartbeat interval = 10ms
            });

            using var connection = storage.CreateAndOpenConnection();
            var fetchedAt = DateTime.UtcNow.AddMinutes(-1);
            var id = CreateJobQueueRecord(connection, 1, fetchedAt);
            var job = new SQLiteFetchedJob(storage, connection, id, 1, Queue, fetchedAt);

            Thread.Sleep(100); // exceed the heartbeat interval
            job.ExecuteKeepAliveQueryIfRequired();

            var refreshed = connection.JobQueueRepository.First(x => x.Id == id).FetchedAt;
            Assert.True(refreshed > fetchedAt, $"FetchedAt should have slid forward from {fetchedAt:O} but was {refreshed:O}.");
        }

        [Fact]
        public void KeepAlive_DoesNotResurrect_AJobFetchedByAnotherWorker()
        {
            var storage = ConnectionUtils.CreateStorage(new SQLiteStorageOptions
            {
                UseSlidingInvisibilityTimeout = true,
                InvisibilityTimeout = TimeSpan.FromMilliseconds(50),
            });

            using var connection = storage.CreateAndOpenConnection();
            var fetchedAt = DateTime.UtcNow.AddMinutes(-1);
            var id = CreateJobQueueRecord(connection, 1, fetchedAt);
            var job = new SQLiteFetchedJob(storage, connection, id, 1, Queue, fetchedAt);

            // Another worker steals the job by setting a different fetched timestamp.
            var stolenAt = DateTime.UtcNow;
            var row = connection.JobQueueRepository.First(x => x.Id == id);
            row.FetchedAt = stolenAt;
            connection.Database.Update(row);

            Thread.Sleep(100);
            job.ExecuteKeepAliveQueryIfRequired();

            // The keep-alive must not have overwritten the other worker's timestamp.
            var after = connection.JobQueueRepository.First(x => x.Id == id).FetchedAt;
            Assert.Equal(stolenAt, after);

            // And we must not be able to remove a job we no longer own.
            job.RemoveFromQueue();
            Assert.Equal(1, connection.JobQueueRepository.Count());
        }

        [Fact]
        public void RemoveFromQueue_WithSliding_OnlyRemovesWhenStillOwned()
        {
            var storage = ConnectionUtils.CreateStorage(new SQLiteStorageOptions { UseSlidingInvisibilityTimeout = true });

            using var connection = storage.CreateAndOpenConnection();
            var fetchedAt = DateTime.UtcNow;
            var id = CreateJobQueueRecord(connection, 1, fetchedAt);
            var job = new SQLiteFetchedJob(storage, connection, id, 1, Queue, fetchedAt);

            // Still owned -> removal succeeds.
            job.RemoveFromQueue();
            Assert.Equal(0, connection.JobQueueRepository.Count());
        }

        [Fact]
        public void Requeue_WithSliding_ReleasesTheJob()
        {
            var storage = ConnectionUtils.CreateStorage(new SQLiteStorageOptions { UseSlidingInvisibilityTimeout = true });

            using var connection = storage.CreateAndOpenConnection();
            var fetchedAt = DateTime.UtcNow;
            var id = CreateJobQueueRecord(connection, 1, fetchedAt);
            var job = new SQLiteFetchedJob(storage, connection, id, 1, Queue, fetchedAt);

            job.Requeue();

            var record = connection.JobQueueRepository.First(x => x.Id == id);
            Assert.Equal(DateTime.MinValue, record.FetchedAt);
        }

        private static int CreateJobQueueRecord(HangfireDbContext connection, int jobId, DateTime fetchedAt)
        {
            var jobQueue = new JobQueue
            {
                JobId = jobId,
                Queue = Queue,
                FetchedAt = fetchedAt,
            };

            connection.Database.Insert(jobQueue);

            return jobQueue.Id;
        }
    }
}
