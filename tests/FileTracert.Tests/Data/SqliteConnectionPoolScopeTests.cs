using FileTracert.Tests.Infrastructure;
using FluentAssertions;

namespace FileTracert.Tests.Data;

/// <summary>
/// Why no test teardown may call <c>SqliteConnection.ClearAllPools()</c> (step 11i).
/// <para>
/// The suite was losing 1–2 integration tests per full run under concurrent load — a
/// different test every time, always green in isolation, always
/// <c>ObjectDisposedException: 'SQLitePCL.sqlite3'</c>. These two facts pin the cause:
/// the pool is a <em>process</em> resource, and clearing it disposes native handles that
/// belong to whoever else is querying right now.
/// </para>
/// <para>
/// The measurement runs in <see cref="PoolProbeReport">a child process</see>, because
/// making the point in-process would break the suite it is defending.
/// </para>
/// </summary>
public sealed class SqliteConnectionPoolScopeTests : IClassFixture<PoolProbeReport>
{
    private readonly PoolProbeReport _probe;

    public SqliteConnectionPoolScopeTests(PoolProbeReport probe) => _probe = probe;

    [Fact]
    public void ClearAllPools_reaches_databases_the_caller_never_opened()
    {
        _probe.ExitCode.Should().Be(0, "the probe must run to completion.\n{0}", _probe.Transcript);

        // A pooled-but-idle connection still owns the file, so the lock answers
        // "is this database's pool still holding it?".
        _probe["scope.locked-while-pooled"]
            .Should().Be("True", "an idle pooled connection keeps the file open.\n{0}", _probe.Transcript);

        _probe["scope.locked-after-clearing-another-pool"]
            .Should().Be("True", "ClearPool is scoped to one connection string.\n{0}", _probe.Transcript);

        _probe["scope.locked-after-clear-all-pools"]
            .Should().Be("False", "ClearAllPools clears pools its caller never opened.\n{0}", _probe.Transcript);

        _probe["scope.locked-after-clearing-its-own-pool"]
            .Should().Be("False", "the targeted call still releases what the caller owns.\n{0}", _probe.Transcript);
    }

    [Fact]
    public void ClearAllPools_disposes_the_native_handle_of_a_database_someone_else_is_using()
    {
        _probe.ExitCode.Should().Be(0, "the probe must run to completion.\n{0}", _probe.Transcript);

        _probe["race.clear-all-pools.failure"]
            .Should().Be(
                "System.ObjectDisposedException",
                "four threads querying their own databases while a fifth calls ClearAllPools is the "
                + "shape of the suite under load.\n{0}",
                _probe.Transcript);

        _probe["race.clear-all-pools.disposed-object"]
            .Should().Be("SQLitePCL.sqlite3", "this is the signature the flaky runs reported.\n{0}", _probe.Transcript);

        // The control: the same race, with each teardown clearing only its own pool.
        _probe["race.targeted-clear-pool.failure"]
            .Should().Be("<none>", "a targeted ClearPool disturbs nobody.\n{0}", _probe.Transcript);

        long.Parse(_probe["race.targeted-clear-pool.iterations"])
            .Should().BeGreaterThan(0, "the control has to have done real work.\n{0}", _probe.Transcript);
    }
}
