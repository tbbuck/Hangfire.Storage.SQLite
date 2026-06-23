# Hangfire.Storage.SQLite (tbbuck fork)
[![NuGet](https://img.shields.io/nuget/vpre/tbbuck.Hangfire.Storage.SQLite.svg)](https://www.nuget.org/packages/tbbuck.Hangfire.Storage.SQLite)
[![Official Site](https://img.shields.io/badge/site-hangfire.io-blue.svg)](http://hangfire.io)
[![License MIT](https://img.shields.io/badge/license-MIT-green.svg)](http://opensource.org/licenses/MIT)

> ## 🔒 Why this fork exists
>
> This is a **security-maintained fork** of [raisedapp/Hangfire.Storage.SQLite](https://github.com/raisedapp/Hangfire.Storage.SQLite). The original package pulls in a SQLite native engine (`SQLitePCLRaw.bundle_green` → `lib.e_sqlite3 2.1.11`) that is affected by **[CVE-2025-6965](https://github.com/advisories/GHSA-2m69-gcr7-jv3q)** — a High-severity (CVSS 7.2) memory-corruption flaw in SQLite versions before 3.50.2.
>
> This fork removes that dependency and ships the **patched SourceGear SQLite 3.50.4** engine instead, with no public API changes. It is published on NuGet as **[`tbbuck.Hangfire.Storage.SQLite`](https://www.nuget.org/packages/tbbuck.Hangfire.Storage.SQLite)**, and additionally adds [UTC timestamp views](#querying-timestamps-directly-utc-views) and an opt-in [sliding invisibility timeout](#sliding-invisibility-timeout).

## Overview

An Alternative SQLite Storage for Hangfire.

This project was created by abandonment **Hangfire.SQLite** storage (https://github.com/wanlitao/HangfireExtension), as an alternative to use SQLite with Hangfire.

Is production ready? **Yes**

![dashboard_servers](content/dashboard_servers.png)

![dashboard_recurring_jobs](content/dashboard_recurring_jobs.png)

![dashboard_heartbeat](content/dashboard_heartbeat.png)


## Installation

Install the package from NuGet. It is published as a prerelease (it depends on the
prerelease `sqlite-net-pcl 1.11.x` that carries the patched SQLite engine), so include
prereleases when installing:

```
Install-Package tbbuck.Hangfire.Storage.SQLite -IncludePrerelease
```

or with the .NET CLI:

```
dotnet add package tbbuck.Hangfire.Storage.SQLite --prerelease
```

> **.NET Framework note:** the patched SourceGear SQLite engine does not support `AnyCPU` on
> .NET Framework. If you consume this package from a .NET Framework app, set an explicit
> `<PlatformTarget>` (`x64` or `x86`). Modern .NET (Core / 5+) consumers are unaffected — native
> assets are resolved per-RID automatically.

## Usage

This is how you connect to an SQLite instance
```csharp
GlobalConfiguration.Configuration.UseSQLiteStorage();
```

### Example

```csharp
services.AddHangfire(configuration => configuration
            .UseSimpleAssemblyNameTypeSerializer()
            .UseRecommendedSerializerSettings()
            .UseSQLiteStorage());
```

## Options

In the UseSQLiteStorage method you can use an instance of the Hangfire.Storage.SQLite.SQLiteStorageOptions class to specify some options of this plugin.

Below is a description of them:

`Option` | `Default Value`
--- | ---
**QueuePollInterval** |  **TimeSpan.FromSeconds(15)**
**InvisibilityTimeout** |  **TimeSpan.FromMinutes(30)**
**UseSlidingInvisibilityTimeout** | **false** (see [Sliding invisibility timeout](#sliding-invisibility-timeout))
**DistributedLockLifetime** | **TimeSpan.FromSeconds(30)**
**JobExpirationCheckInterval** | **TimeSpan.FromHours(1)**
**CountersAggregateInterval** | **TimeSpan.FromMinutes(5)**
**AutoVacuumSelected** | **AutoVacuum.NONE**, other options: **AutoVacuum.Full** or **AutoVacuum.Incremental** [AutoVacumm Explained](https://www.techonthenet.com/sqlite/auto_vacuum.php)

## Querying timestamps directly (UTC views)

All `DateTime` columns in the Hangfire tables (`ExpireAt`, `CreatedAt`, `FetchedAt`,
`LastHeartbeat`) are stored as **.NET `DateTime` ticks** (100-nanosecond intervals since
`0001-01-01`), not Unix milliseconds. This is invisible when you go through the library,
but it is a footgun for **ad-hoc SQL** run directly against the database: comparing those
columns against a Unix timestamp never matches, so time-windowed queries silently return
lifetime totals instead of erroring.

To convert ticks to a Unix timestamp in raw SQL:

```sql
-- ticks -> ISO-8601 UTC text
datetime((ExpireAt - 621355968000000000) / 10000000.0, 'unixepoch', 'subsec')
```

For convenience, this fork also creates a read-only **`<Table>_utc` companion view** for
every table with timestamp columns. Each view exposes all of the original columns plus a
`<Column>Utc` alias holding the ISO-8601 UTC string. The underlying tables and the
library's own behaviour are unchanged — the views are purely a convenience for direct
querying, and existing databases gain them automatically on next startup.

```sql
-- instead of: SELECT ExpireAt FROM "Job"   (raw ticks)
SELECT ExpireAtUtc, CreatedAtUtc FROM "Job_utc" WHERE ExpireAtUtc > '2026-01-01';
```

## Sliding invisibility timeout

When a worker dequeues a job, the job is hidden from other workers for `InvisibilityTimeout`
(default 30 minutes). If a job runs longer than that, another worker can pick it up and run it
**again** — duplicate execution. Raising `InvisibilityTimeout` only delays recovery when a server
genuinely crashes.

Setting `UseSlidingInvisibilityTimeout = true` fixes this the same way Hangfire's SQL Server and
PostgreSQL providers do: while a worker holds a job, a background process periodically "slides" the
job's fetched timestamp forward. The job stays invisible for as long as the owning worker is alive,
and becomes available again shortly after the worker (or its process) dies — so you can use a much
lower `InvisibilityTimeout` safely with long-running jobs.

```csharp
GlobalConfiguration.Configuration.UseSQLiteStorage("hangfire.db", new SQLiteStorageOptions
{
    UseSlidingInvisibilityTimeout = true,
    InvisibilityTimeout = TimeSpan.FromMinutes(5),
});
```

The keep-alive runs every `InvisibilityTimeout / 5`. It relies on the storage's background processes
running, so it has no effect on servers configured not to run them.

## Thanks

This project is mainly based on **Hangfire.LiteDB** storage by [@codeyu](https://github.com/codeyu) (https://github.com/codeyu/Hangfire.LiteDB)

## License
This project is under MIT license. You can obtain the license copy [here](https://github.com/raisedapp/Hangfire.Storage.SQLite/blob/develop/LICENSE).
