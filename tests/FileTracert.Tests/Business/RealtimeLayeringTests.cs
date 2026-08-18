using System.Reflection;
using FileTracert.Business.Realtime;
using FileTracert.Contracts.Realtime;
using FileTracert.Data;
using FileTracert.Platform;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// §3, enforced instead of trusted: SignalR is a Host dependency. Business, Data, Platform and
/// Contracts publish through <see cref="IRealtimePublisher"/> and must not know what carries it —
/// the day one of them takes an <c>IHubContext</c>, this test is what says so.
///
/// The check is on what the compiled assembly actually binds to, not on the csproj: an unused
/// PackageReference is harmless, a used one is the violation.
/// </summary>
public sealed class RealtimeLayeringTests
{
    public static TheoryData<string, Assembly> LowerLayers => new()
    {
        { "Contracts", typeof(IRealtimePublisher).Assembly },
        { "Data", typeof(FileTracertDbContext).Assembly },
        { "Platform", typeof(PlatformServiceCollectionExtensions).Assembly },
        { "Business", typeof(RealtimeEvents).Assembly },
    };

    [Theory]
    [MemberData(nameof(LowerLayers))]
    public void No_layer_below_Host_binds_to_AspNetCore(string layer, Assembly assembly)
    {
        var forbidden = assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .ToList();

        forbidden.Should().BeEmpty(
            "{0} must reach the transport through the port in Contracts, never through SignalR", layer);
    }
}
