using FileTracert.Contracts.Platform;
using FileTracert.HardwareSmoke;
using FileTracert.Platform;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

// ── hardware-smoke harness entry point ───────────────────────────────────────
// Opt-in: does nothing unless HardwareSmoke.Enabled is true in appsettings and the guard-rails
// pass. Exercises the REAL file mover on real files, operating only on duplicates of the
// configured Source, with deletes going to the Recycle Bin (reversible).

SQLitePCL.Batteries.Init();

var config = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false)
    .Build();

var options = config.GetSection(HardwareSmokeOptions.SectionName).Get<HardwareSmokeOptions>()
              ?? new HardwareSmokeOptions();

void Report(string line) => Console.WriteLine($"[smoke] {line}");

if (!options.Enabled)
{
    Report("HardwareSmoke is disabled (Enabled=false). Nothing to do.");
    return 0;
}

// Bind the platform ports through the public DI wiring (the concrete Win32 types are internal).
var platform = new ServiceCollection().AddLogging().AddPlatformServices().BuildServiceProvider();
var probe = platform.GetRequiredService<IVolumeProbe>();
var mover = platform.GetRequiredService<IFileMover>();

// Resolve production WatchedRoots so the guard can refuse to touch catalogued data.
var mainDbPath = ResolveMainDbPath(config);
var prod = ProductionRootsReader.Read(mainDbPath, probe);
if (!prod.CouldVerify)
{
    Report($"REFUSING: the production database at '{mainDbPath}' exists but could not be read — " +
           "cannot verify the target areas are clear of catalogued data.");
    return 2;
}

var resolver = new VolumePathResolver(probe);
var runner = new HardwareSmokeRunner(mover, resolver, Report);

bool ran = runner.Run(options, prod.RootPaths);
return ran ? 0 : 1;

// Same convention as the service: explicit override, else %LOCALAPPDATA%\FileTracert\filetracert.db.
static string ResolveMainDbPath(IConfiguration config)
{
    var overridePath = config["HardwareSmoke:MainDatabasePath"];
    if (!string.IsNullOrWhiteSpace(overridePath))
        return Path.GetFullPath(overridePath);

    return Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FileTracert", "filetracert.db");
}
