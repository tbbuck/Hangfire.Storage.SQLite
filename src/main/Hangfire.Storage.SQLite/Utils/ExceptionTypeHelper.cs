using System;

namespace Hangfire.Storage.SQLite.Utils
{
    /// <summary>
    /// Identifies exceptions that are safe to catch and swallow in best-effort background work.
    /// Based on the equivalent helper in Hangfire and Hangfire.PostgreSql.
    /// </summary>
    internal static class ExceptionTypeHelper
    {
        private static readonly Type StackOverflowType = typeof(StackOverflowException);
        private static readonly Type OutOfMemoryType = typeof(OutOfMemoryException);

        internal static bool IsCatchableExceptionType(this Exception e)
        {
            var type = e.GetType();
            return type != StackOverflowType
                && type != OutOfMemoryType;
        }
    }
}
