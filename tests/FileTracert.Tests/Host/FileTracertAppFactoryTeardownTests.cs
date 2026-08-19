using FluentAssertions;

namespace FileTracert.Tests.Host;

/// <summary>
/// The teardown contract of <see cref="FileTracertAppFactory"/> after step 11i.
/// <para>
/// Releasing only its own pool is only safe if it is also <em>enough</em>: a targeted
/// <c>ClearPool</c> works on the connection string, so it frees the file only when it names
/// the pool EF Core and the log store really opened. Get that wrong and nothing fails loudly —
/// the deletes just start missing, and %TEMP% fills with locked databases one test at a time.
/// This test is the alarm for that silence.
/// </para>
/// </summary>
public sealed class FileTracertAppFactoryTeardownTests
{
    [Fact]
    public async Task Dispose_releases_and_deletes_both_databases_it_created()
    {
        string dbPath;
        string logsPath;

        using (var factory = new FileTracertAppFactory
        {
            DisableVolumeSync = true,
            DisableScan = true,
            DisableQueue = true,
            DisableDeviceWatcher = true,
        })
        {
            var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-FileTracert-Token", factory.Token);

            // Touch both databases through the real pipeline: the catalog goes through EF Core,
            // /api/logs through the raw-SQLite log store. Each opens a pooled connection.
            (await client.GetAsync("/api/volumes")).IsSuccessStatusCode.Should().BeTrue();
            (await client.GetAsync("/api/logs?take=1")).IsSuccessStatusCode.Should().BeTrue();

            dbPath = factory.DatabasePath;
            logsPath = factory.LogDatabasePath;
            File.Exists(dbPath).Should().BeTrue("the host runs on a real file");
            File.Exists(logsPath).Should().BeTrue("logging runs on its own real file");
        }

        File.Exists(dbPath).Should()
            .BeFalse("the main database's own pool must be the one that gets cleared");
        File.Exists(logsPath).Should()
            .BeFalse("the log database's own pool must be cleared too — it has a separate connection string");
    }
}
