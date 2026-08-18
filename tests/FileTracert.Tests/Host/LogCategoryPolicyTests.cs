using FileTracert.Host.Logging;
using FluentAssertions;
using Microsoft.Extensions.Logging;

namespace FileTracert.Tests.Host;

/// <summary>
/// Guards the log-flood regression: with the user switch at Debug, EF Core internals
/// (Microsoft.EntityFrameworkCore.ChangeTracking fires per tracked entity) produced
/// ~1M rows/hour in the log DB, saturating disk I/O until main-DB writes timed out
/// ("database is locked"). Framework categories must stay capped at Warning regardless
/// of the user-facing minimum level, which governs FileTracert categories only.
/// </summary>
public sealed class LogCategoryPolicyTests
{
    [Theory]
    [InlineData("Microsoft.EntityFrameworkCore.ChangeTracking")]
    [InlineData("Microsoft.EntityFrameworkCore.Database.Command")]
    [InlineData("Microsoft.AspNetCore.Hosting.Diagnostics")]
    [InlineData("System.Net.Http.HttpClient")]
    public void Framework_categories_are_capped_at_Warning_even_when_switch_is_Trace(string category)
    {
        LogCategoryPolicy.IsEnabled(category, LogLevel.Debug, LogLevel.Trace).Should().BeFalse();
        LogCategoryPolicy.IsEnabled(category, LogLevel.Information, LogLevel.Trace).Should().BeFalse();
        LogCategoryPolicy.IsEnabled(category, LogLevel.Warning, LogLevel.Trace).Should().BeTrue();
        LogCategoryPolicy.IsEnabled(category, LogLevel.Error, LogLevel.Trace).Should().BeTrue();
    }

    [Theory]
    [InlineData("FileTracert.Business.Scanning.ScanService")]
    [InlineData("FileTracert.Host.Workers.QueueProcessorWorker")]
    public void App_categories_follow_the_user_switch(string category)
    {
        LogCategoryPolicy.IsEnabled(category, LogLevel.Debug, LogLevel.Debug).Should().BeTrue();
        LogCategoryPolicy.IsEnabled(category, LogLevel.Debug, LogLevel.Information).Should().BeFalse();
        LogCategoryPolicy.IsEnabled(category, LogLevel.Information, LogLevel.Information).Should().BeTrue();
    }

    /// <summary>
    /// Step 10b relies on this: the SignalR client authenticates the socket with
    /// <c>?access_token=…</c> (a WebSocket handshake cannot carry a custom header), and a query
    /// string is the kind of value that leaks through request logging. Kestrel's request line is
    /// written by <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> at Information — capped away
    /// here at every user level, so the token never reaches the log DB. Verified, not assumed.
    /// </summary>
    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    public void Request_lines_are_never_logged_so_the_hub_query_token_stays_out_of_the_log(LogLevel userMinimum)
    {
        LogCategoryPolicy
            .IsEnabled("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Information, userMinimum)
            .Should().BeFalse();
    }

    [Fact]
    public void None_level_is_never_enabled()
    {
        LogCategoryPolicy.IsEnabled("FileTracert.X", LogLevel.None, LogLevel.Trace).Should().BeFalse();
    }
}
