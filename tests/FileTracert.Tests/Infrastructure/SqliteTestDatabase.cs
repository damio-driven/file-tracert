using FileTracert.Host.Configuration;
using Microsoft.Data.Sqlite;

namespace FileTracert.Tests.Infrastructure;

/// <summary>
/// Teardown for a test that owns a real SQLite file on disk.
/// <para>
/// The point of this helper is the one thing it refuses to do: <c>ClearAllPools()</c>.
/// Microsoft.Data.Sqlite pools connections per connection string, but the pool registry is
/// a <em>process</em> resource, and xUnit runs test classes in parallel — so a teardown that
/// clears every pool disposes the native <c>sqlite3</c> handle of whatever another class is
/// querying at that instant. That was the whole flakiness of steps 11e–11g:
/// <c>ObjectDisposedException: 'SQLitePCL.sqlite3'</c>, a different test each run, always
/// green in isolation. See <c>SqliteConnectionPoolScopeTests</c> for the measurement.
/// </para>
/// <para>
/// Clearing is still needed — an idle pooled connection keeps the file open, so on Windows
/// the temp database cannot be deleted while it exists — but only for the database this test
/// owns, which every caller knows by path.
/// </para>
/// </summary>
public static class SqliteTestDatabase
{
    /// <summary>Closes this database's pooled connections, leaving every other pool alone.</summary>
    public static void ClearPool(string databasePath)
    {
        using var handle = new SqliteConnection(ConnectionString(databasePath));
        SqliteConnection.ClearPool(handle);
    }

    /// <summary>
    /// Releases and deletes each database plus its WAL sidecars. Deletion is best effort: a
    /// file can stay mapped for a moment after the last connection closes, and a leftover
    /// temp file must never fail a test.
    /// </summary>
    public static void Delete(params string[] databasePaths)
    {
        foreach (var path in databasePaths)
        {
            ClearPool(path);
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                try
                {
                    File.Delete(path + suffix);
                }
                catch (IOException)
                {
                    // Still mapped; a leftover temp file is harmless.
                }
                catch (UnauthorizedAccessException)
                {
                    // Same, with the other exception Windows uses for a locked file.
                }
            }
        }
    }

    /// <summary>
    /// Releases and deletes a whole folder of databases. Same best-effort contract as
    /// <see cref="Delete"/>.
    /// </summary>
    public static void DeleteDirectory(string directory, params string[] databasePaths)
    {
        foreach (var path in databasePaths)
        {
            ClearPool(path);
        }

        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // Still mapped; a leftover temp folder is harmless.
        }
        catch (UnauthorizedAccessException)
        {
            // Same, with the other exception Windows uses for a locked file.
        }
    }

    /// <summary>
    /// The one place the test suite spells a SQLite connection string, delegated to the product's
    /// own helper. The pool is keyed by this string: a teardown that builds it a second way frees
    /// nothing, and does so in silence. <c>SqliteTestDatabaseContractTests</c> is the alarm.
    /// </summary>
    public static string ConnectionString(string databasePath) =>
        DatabaseLocation.ConnectionString(databasePath);
}
