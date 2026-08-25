using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace FileTracert.Tests.Data;

/// <summary>
/// Records, for every statement EF sends down the reader path, where it came from and whether the
/// read guard had already taken it over.
///
/// <para>The observation is free: an interceptor that suppresses execution returns a result, and EF
/// passes that accumulated <see cref="InterceptionResult{T}"/> to the interceptors registered after
/// it. So <c>HasResult</c> answers "did <c>SqliteReadCancellationInterceptor</c> guard this one?"
/// without the guard having to say anything about itself.</para>
/// </summary>
public sealed class GuardedCommandObserver : DbCommandInterceptor
{
    private readonly List<(CommandSource Source, bool Guarded, bool InTransaction)> _seen = [];

    public IReadOnlyList<(CommandSource Source, bool Guarded, bool InTransaction)> Seen => _seen;

    public void Reset() => _seen.Clear();

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        _seen.Add((eventData.CommandSource, result.HasResult, command.Transaction is not null));
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }
}
