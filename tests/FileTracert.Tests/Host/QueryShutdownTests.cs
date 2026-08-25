using FileTracert.Contracts.Enums;
using FileTracert.Contracts.Search;
using FileTracert.Data;
using FileTracert.Data.Cancellation;
using FileTracert.Data.Entities;
using FileTracert.Tests.Data;
using FileTracert.Tests.Infrastructure;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace FileTracert.Tests.Host;

/// <summary>
/// Step 14b, the wiring half: the token the read guard watches has to be the host's own stop
/// signal, fired at the one moment that is right, or the guarantee is a unit test about a class
/// nobody connected.
///
/// <para>What step 13 saw on the installed service was an <c>sc stop</c> stuck in
/// <c>StopPending</c> for over 270 s against the 30 s <c>ShutdownTimeout</c> of §3, held by a
/// search that could not be stopped. <c>TestServer</c> does not hold its host on in-flight requests
/// the way Kestrel does, so no in-process test can reproduce that wait honestly; what is provable
/// here — and what the wait was made of — is that the host's stop signal reaches a running
/// statement, and that it does so only once every worker has already been stopped. The stopwatch
/// half lives on the hardware, in the closing measurement of this step.</para>
/// </summary>
public sealed class QueryShutdownTests : IAsyncLifetime
{
    private const int Rows = 50_000;
    private const int CancelAtRow = 1_000;

    private readonly ITestOutputHelper _out;
    private FileTracertAppFactory _factory = null!;

    public QueryShutdownTests(ITestOutputHelper output) => _out = output;

    public Task InitializeAsync()
    {
        _factory = new FileTracertAppFactory
        {
            DisableScan = true,
            DisableVolumeSync = true,
            DisableQueue = true,
            DisableDeviceWatcher = true,
            Seed = SeedAsync,
        };
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        var db = _factory.DatabasePath;
        var logs = _factory.LogDatabasePath;
        _factory.Dispose();
        SqliteTestDatabase.Delete(db, logs);
        return Task.CompletedTask;
    }

    private static async Task SeedAsync(FileTracertDbContext ctx, CancellationToken ct)
    {
        var volume = new Volume
        {
            VolumeGuid = @"\\?\Volume{5b14b0a1-14b0-4b14-b014-b014b014b014}\",
            FileSystem = "NTFS",
            ScanEngine = VolumeScanEngine.UsnJournal,
            IsOnline = true,
        };
        ctx.Volumes.Add(volume);
        await ctx.SaveChangesAsync(ct);

        var root = new DirectoryNode
        {
            VolumeId = volume.Id,
            Name = string.Empty,
            MaterializedPath = string.Empty,
            IsMaterialized = true,
        };
        ctx.Directories.Add(root);
        await ctx.SaveChangesAsync(ct);

        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fffffff");
        // Every interpolated value is an int or a value produced right here — nothing user-supplied.
        // The FTS index is filled by the host's own startup backfill, which runs after this seeder.
#pragma warning disable EF1002
        await ctx.Database.ExecuteSqlRawAsync($"""
            WITH RECURSIVE seq(n) AS (
                SELECT 1 UNION ALL SELECT n + 1 FROM seq WHERE n < {Rows})
            INSERT INTO Files
              (VolumeId, DirectoryId, Name, Extension, Category, SizeBytes,
               CreatedUtc, ModifiedUtc, Attributes, IsIncluded, ExcludedByType, ExcludedByRoot,
               ExcludedByScan, IsPresent, LastIndexedUtc, PendingState, RowCreatedUtc, RowUpdatedUtc)
            SELECT {volume.Id}, {root.Id}, 'match' || n || '.bin', 'bin', 'Other', 1024 * n,
                   '{now}', '{now}', 0, 1, 0, 0, 0, 1, '{now}', 'None', '{now}', '{now}'
            FROM seq
            """, ct);
#pragma warning restore EF1002
    }

    /// <summary>Matches every seeded row, filters to none — so the statement walks the whole set.</summary>
    private static FileSearchQuery LongQuery() =>
        new("match", SearchScope.Name, null, ["zzz"], null, null, null, null,
            null, false, SearchSort.Relevance, false, 0, 50);

    [Fact]
    public async Task The_hosts_stop_signal_reaches_a_running_read()
    {
        _ = _factory.Token; // starts the host: migrations, seed, FTS backfill.

        var source = _factory.Services.GetRequiredService<DatabaseShutdownSource>();
        var signal = _factory.Services.GetRequiredService<DatabaseShutdownSignal>();

        signal.Should().BeSameAs(source.Signal,
            "the Host must hand the guard the signal it actually fires, not the do-nothing one " +
            "AddDataServices binds by default");
        signal.Token.IsCancellationRequested.Should().BeFalse();

        using var scope = _factory.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<FileTracertDbContext>();
        var index = scope.ServiceProvider.GetRequiredService<IFileSearchIndex>();

        int full;
        await using (var baseline = await FilesShadowView.InstallAsync(ctx))
        {
            (await index.SearchAsync(LongQuery(), CancellationToken.None)).TotalCount.Should().Be(0);
            full = baseline.Visits;
            _out.WriteLine($"uninterrupted: {full} rows stepped");
            full.Should().BeGreaterThan(CancelAtRow * 10);
        }

        // A request's own token, alive and never cancelled: what stops this read is the host.
        using var request = new CancellationTokenSource();
        await using var view = await FilesShadowView.InstallAsync(
            ctx, row => { if (row == CancelAtRow) source.Stop(); });

        var act = async () => await index.SearchAsync(LongQuery(), request.Token);
        var thrown = (await act.Should().ThrowAsync<OperationCanceledException>()).Which;

        _out.WriteLine($"stopped at row {CancelAtRow}: {view.Visits} rows stepped (of {full})");

        thrown.InnerException.Should().BeOfType<SqliteException>()
            .Which.SqliteErrorCode.Should().Be(SQLitePCL.raw.SQLITE_INTERRUPT);
        view.Visits.Should().BeLessThan(full / 10,
            "a read still stepping when the host stops is what held the service past its "
            + "ShutdownTimeout");
        request.IsCancellationRequested.Should().BeFalse();
    }

    /// <summary>
    /// WHEN it fires is half the design. <c>ApplicationStopping</c> is raised while every worker is
    /// still running on a token that has not been cancelled yet, so a read interrupted there throws
    /// an <see cref="OperationCanceledException"/> the workers' <c>when (ct.IsCancellationRequested)</c>
    /// filters do not match — a clean stop during a scan would be logged as a failure and raise a
    /// user-facing Notification. The signal therefore rides on a hosted service registered third, so
    /// it runs after every worker has stopped.
    ///
    /// <para>The probe is registered last (test services come after the host's own), so it stops
    /// FIRST — standing in for the workers. If it sees the signal already cancelled, the ordering is
    /// wrong.</para>
    /// </summary>
    [Fact]
    public async Task The_signal_fires_after_the_workers_have_stopped_not_before()
    {
        var probe = new StopOrderProbe();
        _factory.ExtraServices = services => services.AddSingleton<IHostedService>(sp =>
        {
            probe.Signal = sp.GetRequiredService<DatabaseShutdownSignal>();
            return probe;
        });

        var host = _factory.RunningHost;
        var signal = _factory.Services.GetRequiredService<DatabaseShutdownSignal>();

        await host.StopAsync(TimeSpan.FromSeconds(30));

        probe.Stopped.Should().BeTrue("the probe has to have taken part in the stop sequence");
        probe.SignalWasCancelled.Should().BeFalse(
            "a worker still stopping must not have its reads interrupted underneath it");
        signal.Token.IsCancellationRequested.Should().BeTrue(
            "and by the end of the stop sequence the signal must have fired");
    }

    private sealed class StopOrderProbe : IHostedService
    {
        public DatabaseShutdownSignal? Signal { get; set; }
        public bool Stopped { get; private set; }
        public bool SignalWasCancelled { get; private set; }

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken)
        {
            Stopped = true;
            SignalWasCancelled = Signal?.Token.IsCancellationRequested ?? false;
            return Task.CompletedTask;
        }
    }
}
