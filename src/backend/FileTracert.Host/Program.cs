using FileTracert.Business;
using FileTracert.Data;
using FileTracert.Host.Configuration;
using FileTracert.Host.Infrastructure;
using FileTracert.Host.Workers;
using FileTracert.Platform;

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

builder.Services.AddDataServices(connectionString);
builder.Services.AddPlatformServices();
builder.Services.AddBusinessServices();

builder.Services.AddSingleton<IApiTokenAccessor, ApiTokenAccessor>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddSingleton<IScanScheduler, ScanScheduler>();
builder.Services.AddHostedService<VolumeSyncWorker>();
builder.Services.AddHostedService<ScanWorker>();

builder.Services.AddControllers();

builder.Services.Configure<HostOptions>(o =>
    o.ShutdownTimeout = TimeSpan.FromSeconds(options.ShutdownTimeoutSeconds));

// Bind Kestrel to loopback only, on the fixed configured port. No external binding.
builder.WebHost.UseUrls();
builder.WebHost.ConfigureKestrel(k => k.ListenLocalhost(options.Port));

var app = builder.Build();

// Initialize the DB before serving: migrate + WAL + token. Throws → host stops.
await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync(CancellationToken.None);

app.UseMiddleware<TokenAuthMiddleware>();
app.MapControllers();

app.Run();

/// <summary>Exposed so integration tests can host the app via WebApplicationFactory.</summary>
public partial class Program;
