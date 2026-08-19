using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace FileTracert.PoolProbe;

/// <summary>
/// Standalone reproduction of the race that made the xUnit suite flaky under concurrent
/// load (step 11i): <c>SqliteConnection.ClearAllPools()</c> is <em>process-wide</em>, so a
/// teardown that calls it disposes the native <c>sqlite3</c> handle of connections other
/// tests are using at that instant.
/// <para>
/// It lives in its own executable, and not in a <c>[Fact]</c>, for the same reason the fix
/// exists: calling <c>ClearAllPools()</c> inside the test host would be the very defect it
/// is here to demonstrate. <c>SqliteConnectionPoolScopeTests</c> spawns this process and
/// asserts on what it prints; it is also runnable by hand.
/// </para>
/// <para>
/// stdout carries one <c>key=value</c> line per finding. The exit code says whether the
/// probe <em>ran</em>, never whether the findings are good — the assertions belong to the
/// test that reads them.
/// </para>
/// </summary>
public static class Program
{
    private const int WorkerCount = 4;

    public static int Main(string[] args)
    {
        SQLitePCL.Batteries.Init();

        var raceBudget = TimeSpan.FromSeconds(Arg(args, "--race-budget-seconds", 30));
        var controlWindow = TimeSpan.FromSeconds(Arg(args, "--control-seconds", 3));

        var root = Path.Combine(Path.GetTempPath(), "ft-pool-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ProbeScope(root);
            ProbeRace(root, raceBudget, controlWindow);
            return 0;
        }
        catch (Exception ex)
        {
            // Not silent (CLAUDE.md §9): the spawning test prints stderr with its failure.
            Console.Error.WriteLine(ex);
            return 2;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"probe temp folder left behind at '{root}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Which pools a call clears, observed without any timing: a pooled-but-idle connection
    /// still owns the file handle, so "can I open the file exclusively?" answers "is this
    /// database's pool still holding it?".
    /// </summary>
    private static void ProbeScope(string root)
    {
        var mine = Path.Combine(root, "scope-mine.db");
        var other = Path.Combine(root, "scope-other.db");
        OpenAndClose(mine);
        OpenAndClose(other);

        Console.WriteLine($"scope.locked-while-pooled={IsLocked(mine)}");

        ClearPoolOf(other);
        Console.WriteLine($"scope.locked-after-clearing-another-pool={IsLocked(mine)}");

        SqliteConnection.ClearAllPools();
        Console.WriteLine($"scope.locked-after-clear-all-pools={IsLocked(mine)}");

        OpenAndClose(mine);
        ClearPoolOf(mine);
        Console.WriteLine($"scope.locked-after-clearing-its-own-pool={IsLocked(mine)}");
    }

    /// <summary>
    /// The failure itself: four "other test classes" hammering their own databases while a
    /// fifth "teardown" clears pools. With <c>ClearAllPools()</c> one of the four dies on a
    /// disposed native handle; with the targeted call nobody is disturbed.
    /// </summary>
    private static void ProbeRace(string root, TimeSpan budget, TimeSpan controlWindow)
    {
        Report("race.clear-all-pools", RunRace(root, "all", clearAllPools: true, budget));
        Report("race.targeted-clear-pool", RunRace(root, "own", clearAllPools: false, controlWindow));
    }

    private static void Report(string key, (Exception? Failure, long ElapsedMs, long Iterations) outcome)
    {
        Console.WriteLine($"{key}.failure={outcome.Failure?.GetType().FullName ?? "<none>"}");
        Console.WriteLine(
            $"{key}.disposed-object={(outcome.Failure as ObjectDisposedException)?.ObjectName ?? "<none>"}");
        Console.WriteLine($"{key}.elapsed-ms={outcome.ElapsedMs}");
        Console.WriteLine($"{key}.iterations={outcome.Iterations}");
        if (outcome.Failure is not null)
        {
            Console.Error.WriteLine($"--- {key} ---");
            Console.Error.WriteLine(outcome.Failure);
        }
    }

    private static (Exception? Failure, long ElapsedMs, long Iterations) RunRace(
        string root,
        string tag,
        bool clearAllPools,
        TimeSpan window)
    {
        Exception? failure = null;
        long iterations = 0;
        var stopwatch = Stopwatch.StartNew();
        using var stop = new CancellationTokenSource(window);

        var workers = Enumerable.Range(0, WorkerCount).Select(i => Task.Run(() =>
        {
            var connectionString = ConnectionString(Path.Combine(root, $"race-{tag}-w{i}.db"));
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var connection = new SqliteConnection(connectionString);
                    connection.Open();
                    using var command = connection.CreateCommand();
                    command.CommandText = "CREATE TABLE IF NOT EXISTS t(a); SELECT count(*) FROM t;";
                    command.ExecuteScalar();
                    Interlocked.Increment(ref iterations);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    stop.Cancel();
                    return;
                }
            }
        })).ToArray();

        var teardown = Task.Run(() =>
        {
            var ownPath = Path.Combine(root, $"race-{tag}-teardown.db");
            while (!stop.IsCancellationRequested)
            {
                OpenAndClose(ownPath);
                if (clearAllPools)
                {
                    SqliteConnection.ClearAllPools();
                }
                else
                {
                    ClearPoolOf(ownPath);
                }
            }
        });

        Task.WaitAll([.. workers, teardown]);
        stopwatch.Stop();
        return (failure, stopwatch.ElapsedMilliseconds, Interlocked.Read(ref iterations));
    }

    private static string ConnectionString(string path) => $"Data Source={path}";

    private static void OpenAndClose(string path)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
    }

    private static void ClearPoolOf(string path)
    {
        using var handle = new SqliteConnection(ConnectionString(path));
        SqliteConnection.ClearPool(handle);
    }

    /// <summary>True when some handle in this process still holds the file open.</summary>
    private static bool IsLocked(string path)
    {
        try
        {
            using var _ = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    private static int Arg(string[] args, string name, int fallback)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var value)
            ? value
            : fallback;
    }
}
