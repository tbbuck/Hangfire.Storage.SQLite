using Hangfire.Logging;
using Hangfire.Storage.SQLite.Entities;
using SQLite;
using System;
using System.Diagnostics;
using System.Threading;

namespace Hangfire.Storage.SQLite
{
    /// <summary>
    /// Represents distibuted lock implementation for SQLite
    /// </summary>
    public class SQLiteDistributedLock : IDisposable
    {
        private static readonly ILog Logger = LogProvider.For<SQLiteDistributedLock>();

        private readonly string _resource;
        private readonly string _resourceKey;

        private readonly HangfireDbContext _dbContext;

        private readonly SQLiteStorageOptions _storageOptions;

        // When available, the heartbeat uses its own dedicated connection from the storage pool
        // instead of the consumer's (NoMutex, not thread-safe) connection. See issue #79.
        private readonly SQLiteStorage _storage;

        private Timer _heartbeatTimer;

        private bool _completed;

        private string EventWaitHandleName => string.Intern($@"{GetType().FullName}.{_resource}");

        public event Action<bool> Heartbeat;

        /// <summary>
        /// Creates SQLite distributed lock
        /// </summary>
        /// <param name="resource">Lock resource</param>
        /// <param name="database">Lock database</param>
        /// <param name="storageOptions">Database options</param>
        /// <exception cref="DistributedLockTimeoutException">Thrown if lock is not acuired within the timeout</exception>
        private SQLiteDistributedLock(string resource,
            HangfireDbContext database,
            SQLiteStorageOptions storageOptions,
            SQLiteStorage storage)
        {
            _resource = resource ?? throw new ArgumentNullException(nameof(resource));
            _dbContext = database ?? throw new ArgumentNullException(nameof(database));
            _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
            _storage = storage;
            _resourceKey = Guid.NewGuid().ToString();

            if (string.IsNullOrEmpty(resource))
            {
                throw new ArgumentException($@"The {nameof(resource)} cannot be empty", nameof(resource));
            }
        }

        public static SQLiteDistributedLock Acquire(
            string resource,
            TimeSpan timeout,
            HangfireDbContext database,
            SQLiteStorageOptions storageOptions)
        {
            return Acquire(resource, timeout, database, storageOptions, null);
        }

        /// <summary>
        /// Creates SQLite distributed lock, using a dedicated connection from <paramref name="storage"/>
        /// for the heartbeat so that the timer thread never shares the caller's (non-thread-safe)
        /// connection. See issue #79.
        /// </summary>
        internal static SQLiteDistributedLock Acquire(
            string resource,
            TimeSpan timeout,
            HangfireDbContext database,
            SQLiteStorageOptions storageOptions,
            SQLiteStorage storage)
        {
            if (timeout.TotalSeconds > int.MaxValue)
            {
                throw new ArgumentException($"The timeout specified is too large. Please supply a timeout equal to or less than {int.MaxValue} seconds", nameof(timeout));
            }

            var slock = new SQLiteDistributedLock(resource, database, storageOptions, storage);

            slock.Acquire(timeout);
            slock.StartHeartBeat();

            return slock;
        }

        /// <summary>
        /// Disposes the object
        /// </summary>
        /// <exception cref="DistributedLockTimeoutException"></exception>
        public void Dispose()
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            _heartbeatTimer?.Dispose();
            Release();
        }

        private bool TryAcquireLock()
        {
            Cleanup();
            try
            {
                var distributedLock = new DistributedLock
                {
                    Id = Guid.NewGuid().ToString(),
                    Resource = _resource,
                    ResourceKey = _resourceKey,
                    ExpireAt = DateTime.UtcNow.Add(_storageOptions.DistributedLockLifetime)
                };

                return _dbContext.Database.Insert(distributedLock) == 1;
            }
            catch (SQLiteException e) when (e.Result == SQLite3.Result.Constraint)
            {
                return false;
            }
        }

        private void Acquire(TimeSpan timeout)
        {
            var sw = Stopwatch.StartNew();
            do
            {
                if (TryAcquireLock())
                {
                    return;
                }

                var waitTime = (int)timeout.TotalMilliseconds / 10;
                // either wait for the event to be raised, or timeout
                lock (EventWaitHandleName)
                {
                    Monitor.Wait(EventWaitHandleName, waitTime);
                }
            } while (sw.Elapsed <= timeout);

            throw new DistributedLockTimeoutException(_resource);
        }

        /// <summary>
        /// Release the lock
        /// </summary>
        /// <exception cref="DistributedLockTimeoutException"></exception>
        private void Release()
        {
            Retry.Twice((retry) =>
            {

                // Remove resource lock (if it's still ours)
                var count = _dbContext.DistributedLockRepository.Delete(_ => _.Resource == _resource && _.ResourceKey == _resourceKey);
                if (count != 0)
                {
                    lock (EventWaitHandleName)
                        Monitor.Pulse(EventWaitHandleName);
                }
            });
        }

        private void Cleanup()
        {
            try
            {
                Retry.Twice((_) =>
                {
                    // Delete expired locks (of any owner)
                    _dbContext.DistributedLockRepository.
                        Delete(x => x.Resource == _resource && x.ExpireAt < DateTime.UtcNow);
                });
            }
            catch (Exception ex)
            {
                Logger.ErrorFormat("Unable to clean up locks on the resource '{0}'. {1}", _resource, ex);
            }
        }

        /// <summary>
        /// Starts database heartbeat
        /// </summary>
        private void StartHeartBeat()
        {
            TimeSpan timerInterval = TimeSpan.FromMilliseconds(_storageOptions.DistributedLockLifetime.TotalMilliseconds / 5);

            _heartbeatTimer = new Timer(state =>
            {
                // stop timer
                _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                // Timer callback may be invoked after the Dispose method call,
                // but since we use the resource key, we will not disturb other owners.
                try
                {
                    var newExpiry = DateTime.UtcNow.Add(_storageOptions.DistributedLockLifetime);

                    bool didUpdate;
                    if (_storage != null)
                    {
                        // Run the heartbeat on a dedicated connection so the timer thread never
                        // touches the consumer's connection concurrently (issue #79).
                        using (var heartbeatContext = _storage.CreateAndOpenConnection())
                        {
                            didUpdate = UpdateExpiration(heartbeatContext.DistributedLockRepository, newExpiry);
                        }
                    }
                    else
                    {
                        // Legacy path (no storage available): falls back to the shared connection.
                        didUpdate = UpdateExpiration(_dbContext.DistributedLockRepository, newExpiry);
                    }

                    Heartbeat?.Invoke(didUpdate);
                    if (!didUpdate)
                    {
                        Logger.ErrorFormat("Unable to update heartbeat on the resource '{0}'. The resource is not locked or is locked by another owner.", _resource);

                        // if we no longer have a lock, stop the heartbeat immediately
                        _heartbeatTimer?.Dispose();
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.ErrorFormat("Unable to update heartbeat on the resource '{0}'. {1}", _resource, ex);
                }
                // restart timer
                _heartbeatTimer?.Change(timerInterval, timerInterval);
            }, null, timerInterval, timerInterval);
        }

        private bool UpdateExpiration(TableQuery<DistributedLock> tableQuery, DateTime expireAt)
        {
            var expireColumn = tableQuery.Table.FindColumnWithPropertyName(nameof(DistributedLock.ExpireAt)).Name;
            var resourceColumn = tableQuery.Table.FindColumnWithPropertyName(nameof(DistributedLock.Resource)).Name;
            var resourceKeyColumn = tableQuery.Table.FindColumnWithPropertyName(nameof(DistributedLock.ResourceKey)).Name;
            var table = tableQuery.Table.TableName;

            var command = tableQuery.Connection.CreateCommand($@"UPDATE ""{table}""
                SET ""{expireColumn}"" = ?
                WHERE ""{resourceColumn}"" = ?
                AND ""{resourceKeyColumn}"" = ?",
                expireAt,
                _resource,
                _resourceKey);

            return command.ExecuteNonQuery() != 0;
        }
    }
}