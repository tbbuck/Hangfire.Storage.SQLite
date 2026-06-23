using System;
using System.Collections.Concurrent;
using System.Threading;
using Hangfire.Server;

namespace Hangfire.Storage.SQLite
{
    /// <summary>
    /// Background process that periodically refreshes the fetched timestamp of in-progress jobs
    /// when <see cref="SQLiteStorageOptions.UseSlidingInvisibilityTimeout"/> is enabled, so that
    /// long-running jobs are not re-queued while their worker is still alive.
    /// </summary>
#pragma warning disable CS0618 // IServerComponent is obsolete but still required for older hosts
    internal sealed class SQLiteHeartbeatProcess : IBackgroundProcess, IServerComponent
#pragma warning restore CS0618
    {
        private readonly ConcurrentDictionary<SQLiteFetchedJob, object> _items =
            new ConcurrentDictionary<SQLiteFetchedJob, object>();

        public void Track(SQLiteFetchedJob item)
        {
            _items.TryAdd(item, null);
        }

        public void Untrack(SQLiteFetchedJob item)
        {
            _items.TryRemove(item, out _);
        }

        public void Execute(CancellationToken cancellationToken)
        {
            foreach (var item in _items)
            {
                item.Key.ExecuteKeepAliveQueryIfRequired();
            }

            cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
        }

        public void Execute(BackgroundProcessContext context)
        {
            Execute(context.StoppingToken);
        }
    }
}
