using System;

namespace Hangfire.Storage.SQLite
{
    /// <summary>
    /// 
    /// </summary>
    public class SQLiteJobQueueProvider : IPersistentJobQueueProvider
    {
        private readonly SQLiteStorageOptions _storageOptions;

        /// <summary>
        /// 
        /// </summary>
        /// <param name="storageOptions"></param>
        /// <exception cref="ArgumentNullException"></exception>
        public SQLiteJobQueueProvider(SQLiteStorageOptions storageOptions)
        {
            _storageOptions = storageOptions ?? throw new ArgumentNullException(nameof(storageOptions));
        }

        /// <summary>
        /// Owning storage, set by <see cref="SQLiteStorage"/>. Used so fetched jobs can open dedicated
        /// connections for sliding-invisibility-timeout heartbeats.
        /// </summary>
        internal SQLiteStorage Storage { get; set; }

        public IPersistentJobQueue GetJobQueue(HangfireDbContext connection)
        {
            return new SQLiteJobQueue(Storage, connection, _storageOptions);
        }

        public IPersistentJobQueueMonitoringApi GetJobQueueMonitoringApi(HangfireDbContext connection)
        {
            return new SQLiteJobQueueMonitoringApi(connection);
        }
    }
}
