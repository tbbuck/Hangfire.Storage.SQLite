using Hangfire.Logging;
using Hangfire.Storage.SQLite.Utils;
using System;
using System.Linq;
using System.Threading;

namespace Hangfire.Storage.SQLite
{
    /// <summary>
    ///
    /// </summary>
    public class SQLiteFetchedJob : IFetchedJob
    {
        private const string JobQueueTable = "JobQueue";

        private readonly ILog _logger = LogProvider.For<SQLiteFetchedJob>();

        private readonly HangfireDbContext _dbContext;
        private readonly SQLiteStorage _storage;
        private readonly int _id;

        private readonly bool _useSlidingInvisibilityTimeout;
        private readonly object _syncRoot = new object();
        private readonly TimeSpan _interval;
        private long _lastHeartbeat;
        private DateTime _fetchedAt;

        private bool _disposed;
        private bool _released;
        private bool _removedFromQueue;
        private bool _requeued;

        /// <summary>
        /// Constructs fetched job by database connection, identifier, job ID and queue
        /// </summary>
        /// <param name="connection">Database connection</param>
        /// <param name="id">Identifier</param>
        /// <param name="jobId">Job ID</param>
        /// <param name="queue">Queue name</param>
        public SQLiteFetchedJob(HangfireDbContext connection, int id, int? jobId, string queue)
            : this(null, connection, id, jobId, queue, DateTime.MinValue)
        {
        }

        /// <summary>
        /// Constructs fetched job with sliding-invisibility-timeout support.
        /// </summary>
        /// <param name="storage">Owning storage (used to open dedicated connections for heartbeats)</param>
        /// <param name="connection">Database connection</param>
        /// <param name="id">Identifier</param>
        /// <param name="jobId">Job ID</param>
        /// <param name="queue">Queue name</param>
        /// <param name="fetchedAt">The fetched timestamp assigned at dequeue time</param>
        internal SQLiteFetchedJob(SQLiteStorage storage, HangfireDbContext connection, int id, int? jobId, string queue, DateTime fetchedAt)
        {
            _dbContext = connection ?? throw new ArgumentNullException(nameof(connection));
            _storage = storage;
            _id = id;
            JobId = jobId.HasValue ? jobId.Value.ToString() : throw new ArgumentNullException(nameof(jobId));
            Queue = queue ?? throw new ArgumentNullException(nameof(queue));
            _fetchedAt = fetchedAt;

            _useSlidingInvisibilityTimeout = storage != null && storage.StorageOptions.UseSlidingInvisibilityTimeout;
            if (_useSlidingInvisibilityTimeout)
            {
                _lastHeartbeat = TimestampHelper.GetTimestamp();
                _interval = TimeSpan.FromSeconds(storage.StorageOptions.InvisibilityTimeout.TotalSeconds / 5);
                storage.HeartbeatProcess.Track(this);
            }
        }

        /// <summary>
        /// Job ID
        /// </summary>
        public string JobId { get; }

        /// <summary>
        /// Queue name
        /// </summary>
        public string Queue { get; }

        /// <summary>
        /// Removes fetched job from a queue
        /// </summary>
        public void RemoveFromQueue()
        {
            lock (_syncRoot)
            {
                if (_useSlidingInvisibilityTimeout)
                {
                    if (_released)
                    {
                        return;
                    }

                    // Fence on the fetched timestamp: only remove the row if we still own it.
                    _dbContext.Database.Execute(
                        $"DELETE FROM \"{JobQueueTable}\" WHERE \"Id\" = ? AND \"FetchedAt\" = ?", _id, _fetchedAt);
                }
                else
                {
                    _dbContext
                        .JobQueueRepository
                        .Delete(_ => _.Id == _id);
                }

                _removedFromQueue = true;
            }
        }

        /// <summary>
        /// Puts fetched job into a queue
        /// </summary>
        public void Requeue()
        {
            lock (_syncRoot)
            {
                if (_useSlidingInvisibilityTimeout)
                {
                    if (_released)
                    {
                        return;
                    }

                    // Fence on the fetched timestamp: only release the row if we still own it.
                    _dbContext.Database.Execute(
                        $"UPDATE \"{JobQueueTable}\" SET \"FetchedAt\" = ? WHERE \"Id\" = ? AND \"FetchedAt\" = ?",
                        DateTime.MinValue, _id, _fetchedAt);

                    _released = true;
                    _requeued = true;
                }
                else
                {
                    var jobQueue = _dbContext.JobQueueRepository.FirstOrDefault(_ => _.Id == _id);

                    if (jobQueue != null)
                    {
                        jobQueue.FetchedAt = DateTime.MinValue;
                        _dbContext.Database.Update(jobQueue);

                        _requeued = true;
                    }
                }
            }
        }

        /// <summary>
        /// Refreshes the fetched timestamp of this in-progress job, if enough time has elapsed
        /// since the last refresh. Called periodically by <see cref="SQLiteHeartbeatProcess"/>.
        /// Uses a dedicated pooled connection because it runs on the heartbeat thread, never the
        /// worker's connection.
        /// </summary>
        internal void ExecuteKeepAliveQueryIfRequired()
        {
            var now = TimestampHelper.GetTimestamp();

            if (TimestampHelper.Elapsed(now, Interlocked.Read(ref _lastHeartbeat)) < _interval)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (_released || _requeued || _removedFromQueue)
                {
                    return;
                }

                var newFetchedAt = DateTime.UtcNow;

                try
                {
                    int rows;
                    using (var ctx = _storage.CreateAndOpenConnection())
                    {
                        rows = ctx.Database.Execute(
                            $"UPDATE \"{JobQueueTable}\" SET \"FetchedAt\" = ? WHERE \"Id\" = ? AND \"FetchedAt\" = ?",
                            newFetchedAt, _id, _fetchedAt);
                    }

                    if (rows == 0)
                    {
                        _logger.Log(LogLevel.Warn,
                            () => $"Background job queue item '{_id}' (job '{JobId}') was fetched by another worker, will not execute keep-alive.");
                        _released = true;
                    }
                    else
                    {
                        _fetchedAt = newFetchedAt;
                        _logger.Log(LogLevel.Trace, () => $"Keep-alive query for job queue item '{_id}' sent.");
                    }

                    Interlocked.Exchange(ref _lastHeartbeat, now);
                }
                catch (Exception ex) when (ex.IsCatchableExceptionType())
                {
                    _logger.Log(LogLevel.Debug, () => $"Unable to execute keep-alive query for job queue item '{_id}'.", ex);
                }
            }
        }

        /// <summary>
        /// Disposes the object
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;

            if (_useSlidingInvisibilityTimeout)
            {
                _storage.HeartbeatProcess.Untrack(this);
            }

            lock (_syncRoot)
            {
                if (!_removedFromQueue && !_requeued)
                {
                    Requeue();
                }
            }
        }
    }
}
