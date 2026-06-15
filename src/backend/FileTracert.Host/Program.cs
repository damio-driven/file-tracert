using FileTracert.Business;
using FileTracert.Contracts.Logging;
using FileTracert.Data;
using FileTracert.Data.Logging;
using FileTracert.Host.Configuration;
using FileTracert.Host.Infrastructure;
using FileTracert.Host.Logging;
using FileTracert.Host.Workers;
using FileTracert.Platform;
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

// Database lives in %LOCALAPPDATA%\FileTracert (or an explicit override).
var databasePath = DatabaseLocation.Resolve(options);
var connectionString = DatabaseLocation.ConnectionString(databasePath);

// Dedicated log database + queued, non-blocking SQLite logging provider. The store
// is bootstrapped before anything writes; the console sink (added by the default
// builder) stays active so early-startup logs are never lost. A runtime level
// switch gates every provider so changing the level takes effect without a restart.
var logStore = new SqliteLogStore(DatabaseLocation.ConnectionString(DatabaseLocation.ResolveLogs(databasePath)));
logStore.EnsureSchema();
var logLevelSwitch = new LogLevelSwitch();
var logProcessor = new SqliteLogProcessor(logStore);
builder.Services.AddSingleton<ILogStore>(logStore);
builder.Services.AddSingleton(logLevelSwitch);
builder.Services.AddSingleton(logProcessor);
builder.Logging.SetMinimumLevel(LogLevel.Trace);
builder.Logging.AddFilter((_, level) => level >= logLevelSwitch.Current);
builder.Logging.AddProvider(new SqliteLoggerProvider(logProcessor, logLevelSwitch));

builder.Services.AddDataServices(connectionString);
builder.Services.AddPlatformServices();
builder.Services.AddBusinessServices();

builder.Services.AddSingleton<IApiTokenAccessor, ApiTokenAccessor>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<IScanScheduler, ScanScheduler>();
builder.Services.AddHostedService<VolumeSyncWorker>();
builder.Services.AddHostedService<ScanWorker>();
builder.Services.AddHostedService<LogRetentionWorker>();

builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
        // Serialize enums (e.g. NotificationSeverity) as their names, not integers,
        // so the API contract stays readable on the client.
        o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.Configure<HostOptions>(o =>
    o.ShutdownTimeout = TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds));

// Bind Kestrel to loopback only, on the fixed configured port. No external binding.
builder.WebHost.UseUrls();
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(options.Port));

var app = builder.Build();

// Initialize the DB before serving: migrate + WAL + token. Throws → host stops.
await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);

app.UseMiddleware<TokenAuthMiddleware>();

// Serve the built Angular SPA (wwwroot) as static assets; client routes fall back
// to a token-injected index.html. Dev uses ng-serve + proxy instead, fetching the
// token from the Development-only endpoint below.
app.UseStaticFiles();

app.MapControllers();
app.MapDevTokenEndpoint();
app.MapSpaFallback();

app.Run();

/// <summary>Exposed so integration tests can host the app via WebApplicationFactory.</summary>
public partial class Program;
