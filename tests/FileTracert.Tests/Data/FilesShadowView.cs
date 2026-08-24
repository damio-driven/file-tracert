using FileTracert.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FileTracert.Tests.Data;

/// <summary>
/// Renames <c>Files</c> aside and puts a view of the same name in its place, carrying a UDF in its
/// WHERE. Every candidate row a statement steps through calls that UDF, so a test can count the
/// work a statement really did — and, more to the point here, act at a chosen row while the
/// statement is still running.
///
/// <para>The counting half is the technique of steps 11e and 14a. The acting half is what 14b
/// needs: cancelling from a timer means cancelling whenever the thread pool gets round to it, and
/// a cancellation that lands between two statements instead of inside one proves nothing about a
/// statement that cannot be stopped. Cancelling from inside the step is exact.</para>
/// </summary>
internal sealed class FilesShadowView : IAsyncDisposable
{
    private readonly FileTracertDbContext _ctx;
    private int _visits;

    private FilesShadowView(FileTracertDbContext ctx) => _ctx = ctx;

    /// <summary>Candidate rows stepped through since installation.</summary>
    public int Visits => Volatile.Read(ref _visits);

    /// <param name="onVisit">Called with the 1-based row number, on the thread running the
    /// statement. Nothing by default.</param>
    public static async Task<FilesShadowView> InstallAsync(
        FileTracertDbContext ctx, Action<int>? onVisit = null)
    {
        var view = new FilesShadowView(ctx);

        var conn = (SqliteConnection)ctx.Database.GetDbConnection();
        await ctx.Database.OpenConnectionAsync();
        conn.CreateFunction("visit", (long _) =>
        {
            var n = Interlocked.Increment(ref view._visits);
            onVisit?.Invoke(n);
            return 1L;
        });

        await ctx.Database.ExecuteSqlRawAsync("ALTER TABLE Files RENAME TO FilesReal");
        await ctx.Database.ExecuteSqlRawAsync(
            "CREATE VIEW Files AS SELECT * FROM FilesReal WHERE visit(Id) = 1");
        return view;
    }

    public async ValueTask DisposeAsync()
    {
        await _ctx.Database.ExecuteSqlRawAsync("DROP VIEW Files");
        await _ctx.Database.ExecuteSqlRawAsync("ALTER TABLE FilesReal RENAME TO Files");
        await _ctx.Database.CloseConnectionAsync();
    }
}
