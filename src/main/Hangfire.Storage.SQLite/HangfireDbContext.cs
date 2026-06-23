using Hangfire.Logging;
using Hangfire.Storage.SQLite.Entities;
using SQLite;
using System;
using System.Threading;

namespace Hangfire.Storage.SQLite
{
    /// <summary>
    /// Represents SQLite database context for Hangfire
    /// </summary>
    public class HangfireDbContext : IDisposable
    {
        private readonly ILog Logger = LogProvider.For<HangfireDbContext>();

        /// <summary>
        /// 
        /// </summary>
        public SQLiteConnection Database { get; private set; }

        /// <summary>
        /// 
        /// </summary>
        public SQLiteStorageOptions StorageOptions { get; private set; }

        /// <summary>
        /// SQLite database connection identifier
        /// </summary>
        public string ConnectionId { get; }


        /// <summary>
        /// Starts SQLite database using a connection string for file system database
        /// </summary>
        /// <param name="connection">the database path</param>
        /// <param name="logger"></param>
        /// <param name="prefix">Table prefix</param>
        internal HangfireDbContext(SQLiteConnection connection, string prefix = "hangfire")
        {
            Database = connection;

            ConnectionId = Guid.NewGuid().ToString();
        }

        /// <summary>
        /// Initializes initial tables schema for Hangfire
        /// </summary>
        public void Init(SQLiteStorageOptions storageOptions)
        {
            StorageOptions = storageOptions;

            TryFewTimesDueToConcurrency(() => InitializePragmas(storageOptions));
            TryFewTimesDueToConcurrency(() => Database.CreateTable<AggregatedCounter>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<Counter>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<HangfireJob>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<HangfireList>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<Hash>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<JobParameter>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<JobQueue>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<HangfireServer>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<Set>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<State>());
            TryFewTimesDueToConcurrency(() => Database.CreateTable<DistributedLock>());

            foreach (var view in TimestampViews)
            {
                TryFewTimesDueToConcurrency(() => Database.Execute(BuildTimestampViewSql(view.Table, view.DateColumns)));
            }

            void TryFewTimesDueToConcurrency(Action action, int times = 10)
            {
                var current = 0;
                while (current < times)
                {
                    try
                    {
                        action();
                        return;
                    }
                    catch (SQLiteException e) when (e.Result == SQLite3.Result.Locked)
                    {
                        // This can happen if too many connections are opened
                        // at the same time, trying to create tables
                        Thread.Sleep(10);
                    }
                    current++;
                }
            }
        }


        private void InitializePragmas(SQLiteStorageOptions storageOptions)
        {
            try
            {
                Database.ExecuteScalar<string>($"PRAGMA journal_mode = {storageOptions.JournalMode}", Array.Empty<object>());
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, () => $"Error set journal mode. Details: {ex}");
            }

            try
            {
                Database.ExecuteScalar<string>($"PRAGMA auto_vacuum = '{(int)storageOptions.AutoVacuumSelected}'", Array.Empty<object>());
            }
            catch (Exception ex)
            {
                Logger.Log(LogLevel.Error, () => $"Error set auto vacuum mode. Details: {ex}");
            }
        }

        /// <summary>
        /// Number of <see cref="DateTime"/> ticks at the Unix epoch (1970-01-01T00:00:00Z),
        /// i.e. <c>new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks</c>.
        /// </summary>
        private const long UnixEpochTicks = 621355968000000000L;

        /// <summary>
        /// Tables holding UTC timestamps (stored as <see cref="DateTime"/> ticks) and the
        /// columns to surface as ISO-8601 text in the companion <c>*_utc</c> views.
        /// </summary>
        private static readonly (string Table, string[] DateColumns)[] TimestampViews =
        {
            ("AggregatedCounter", new[] { "ExpireAt" }),
            ("Counter", new[] { "ExpireAt" }),
            ("DistributedLock", new[] { "ExpireAt" }),
            ("Job", new[] { "CreatedAt", "ExpireAt" }),
            ("List", new[] { "ExpireAt" }),
            ("Hash", new[] { "ExpireAt" }),
            ("Server", new[] { "LastHeartbeat" }),
            ("JobQueue", new[] { "FetchedAt" }),
            ("Set", new[] { "ExpireAt" }),
            ("JobParameter", new[] { "ExpireAt" }),
            ("State", new[] { "CreatedAt", "ExpireAt" }),
        };

        /// <summary>
        /// Builds an idempotent <c>CREATE VIEW IF NOT EXISTS "&lt;table&gt;_utc"</c> statement that
        /// exposes every column of the underlying table plus a <c>&lt;column&gt;Utc</c> alias for each
        /// tick-valued <see cref="DateTime"/> column, converted to an ISO-8601 UTC string.
        /// These read-only views exist purely as a convenience for ad-hoc SQL against the database;
        /// the library itself continues to read and write raw ticks.
        /// </summary>
        private static string BuildTimestampViewSql(string table, string[] dateColumns)
        {
            var conversions = new string[dateColumns.Length];
            for (var i = 0; i < dateColumns.Length; i++)
            {
                var column = dateColumns[i];
                // ticks -> Unix seconds (fractional) -> ISO-8601 text with sub-second precision.
                conversions[i] =
                    $"datetime((\"{column}\" - {UnixEpochTicks}) / 10000000.0, 'unixepoch', 'subsec') AS \"{column}Utc\"";
            }

            return $"CREATE VIEW IF NOT EXISTS \"{table}_utc\" AS SELECT *, {string.Join(", ", conversions)} FROM \"{table}\"";
        }

        public TableQuery<AggregatedCounter> AggregatedCounterRepository => Database.Table<AggregatedCounter>();

        public TableQuery<Counter> CounterRepository => Database.Table<Counter>();

        public TableQuery<HangfireJob> HangfireJobRepository => Database.Table<HangfireJob>();

        public TableQuery<HangfireList> HangfireListRepository => Database.Table<HangfireList>();

        public TableQuery<Hash> HashRepository => Database.Table<Hash>();

        public TableQuery<JobParameter> JobParameterRepository => Database.Table<JobParameter>();

        public TableQuery<JobQueue> JobQueueRepository => Database.Table<JobQueue>();

        public TableQuery<HangfireServer> HangfireServerRepository => Database.Table<HangfireServer>();

        public TableQuery<Set> SetRepository => Database.Table<Set>();

        public TableQuery<State> StateRepository => Database.Table<State>();

        public TableQuery<DistributedLock> DistributedLockRepository => Database.Table<DistributedLock>();

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                Database?.Dispose();
                Database = null;
            }
        }

        public void Dispose()
        {
            Dispose(true);
        }
    }
}
