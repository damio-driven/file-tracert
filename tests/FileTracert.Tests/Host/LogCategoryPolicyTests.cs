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

    [Fact]
    public void None_level_is_never_enabled()
    {
        LogCategoryPolicy.IsEnabled("FileTracert.X", LogLevel.None, LogLevel.Trace).Should().BeFalse();
    }
}
