using FileTracert.Business;
using FileTracert.Contracts.Logging;
using FileTracert.Data;
using FileTracert.Data.Cancellation;
using FileTracert.Data.Logging;
using FileTracert.Host.Configuration;
using FileTracert.Host.Infrastructure;
using FileTracert.Host.Logging;
using FileTracert.Host.Realtime;
using FileTracert.Host.Workers;
using FileTracert.Platform;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

// Required for the SQLite native provider used by EF Core and BulkExtensions.
SQLitePCL.Batteries.Init();

var builder = WebApplication.CreateBuilder(args);

// Run identically as a console app (dev) and as a Windows Service (prod).
builder.Services.AddWindowsService(o => o.ServiceName = "FileTracert");

builder.Services.Configure<FileTracertOptions>(
    builder.Configuration.GetSection(FileTracertOptions.SectionName));
var options = builder.Configuration
    .GetSection(FileTracertOptions.SectionName)
    .Get<FileTracertOptions>() ?? new FileTracertOptions();

// Database lives in %ProgramData%\FileTracert (or an explicit override): machine-wide, because
// the service runs as LocalSystem and a per-user folder would hand it a different, empty catalog.
// See DatabaseLocation for the full reasoning.
var databasePath = DatabaseLocation.Resolve(options);
var connectionString = DatabaseLocation.ConnectionString(databasePath);

// Dedicated log database + queued, non-blocking SQLite logging provider. The store
// is bootstrapped before anything writes; the console sink (added by the default
// builder) stays active so early-startup logs are never lost. A runtime level
// switch gates every provider so changing the level takes effect without a restart.
// 14b — the read guard's "the process is going down" token. Built here, before anything that needs
// it: the log store is constructed before the container exists and takes it as an argument. The
// hosted service below is what fires it, and WHEN it fires is the whole point — see
// ReadCancellationLifetime. Registered third, so it stops after every worker and before Kestrel's
// request drain.
var readCancellation = new DatabaseShutdownSource();
builder.Services.AddSingleton(readCancellation);
builder.Services.AddSingleton(readCancellation.Signal);

var logStore = new SqliteLogStore(
    DatabaseLocation.ConnectionString(DatabaseLocation.ResolveLogs(databasePath)),
    readCancellation.Signal);
logStore.EnsureSchema();
var logProcessor = builder.AddSqliteLogging(
    logStore,
    new LogLevelSwitch(),
    TimeSpan.FromSeconds(options.LogDrainTimeoutSeconds));

builder.Services.AddHostedService<ReadCancellationLifetime>();

builder.Services.AddDataServices(connectionString);
builder.Services.AddPlatformServices();
builder.Services.AddBusinessServices();

builder.Services.AddSingleton<IApiTokenAccessor, ApiTokenAccessor>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<IScanScheduler, ScanScheduler>();
builder.Services.AddSingleton<VolumeSyncCycle>();
builder.Services.AddHostedService<VolumeSyncWorker>();
builder.Services.AddHostedService<DeviceWatcherWorker>();
builder.Services.AddHostedService<ScanWorker>();
builder.Services.AddHostedService<QueueProcessorWorker>();
builder.Services.AddHostedService<LogRetentionWorker>();
builder.Services.AddHostedService<WalCheckpointWorker>();

// Real-time push (§7). The hub is server → client only; the payload records live in Contracts
// and Business publishes through the port, never through IHubContext (§3). Enums travel as names,
// exactly like the Web API above, so the TypeScript client sees one spelling for both.
builder.Services.AddSignalR().AddJsonProtocol(o =>
    o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
// Replace, not Add: AddBusinessServices bound the no-op port for compositions with no transport.
builder.Services.Replace(
    ServiceDescriptor.Singleton<FileTracert.Contracts.Realtime.IRealtimePublisher, SignalRRealtimePublisher>());

builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
        // Serialize enums (e.g. NotificationSeverity) as their names, not integers,
        // so the API contract stays readable on the client.
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds);

    // Two guarantees of this host are ORDERING guarantees, and both are silently void if the
    // hosted services stop in parallel: the log queue drains last (11c) and the read-cancellation
    // signal fires only once every worker is stopped (14b). Stated rather than inherited from
    // whatever the current default happens to be — a default that changes would turn both into
    // races, and a race that loses looks exactly like a rare flaky test.
    o.ServicesStopConcurrently = false;
    o.ServicesStartConcurrently = false;
});

// Bind Kestrel to loopback only, on the fixed configured port. No external binding.
builder.WebHost.UseUrls();
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(options.Port));

var app = builder.Build();

try
{
    // Initialize the DB before serving: migrate + WAL + token. Throws → host stops.
    await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);

    app.UseMiddleware<TokenAuthMiddleware>();

    // Serve the built Angular SPA (wwwroot) as static assets; client routes fall back
    // to a token-injected index.html. Dev uses ng-serve + proxy instead, fetching the
    // token from the Development-only endpoint below.
    app.UseStaticFiles();

    app.MapControllers();
    app.MapHub<FileTracertHub>("/hubs/events");
    app.MapDevTokenEndpoint();
    app.MapSpaFallback();

    await app.RunAsync();
}
finally
{
    // LogFlushService drains at the end of the stop sequence — but a host that fails to START
    // never runs a stop sequence, and a migration that throws is exactly when the queued records
    // are worth having. Drained here too, from the instance the composition root owns: after
    // RunAsync the container is already disposed and could not hand it back. DrainAsync is
    // idempotent, so the normal path pays nothing.
    await logProcessor.DrainAsync();
}

/// <summary>Exposed so integration tests can host the app via WebApplicationFactory.</summary>
public partial class Program;
