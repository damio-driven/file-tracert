using FileTracert.Contracts.Search;
using Microsoft.Extensions.DependencyInjection;

namespace FileTracert.HardwareSmoke.Scenarios;

/// <summary>
/// The modified-date filter compares against a TEXT column, so a bound in the wrong format sorts
/// wrong instead of failing loudly: a lower bound at midnight used to drop the whole day and an
/// upper bound at midnight used to keep it (review finding #11). This runs the real scan pipeline
/// over files stamped with known timestamps and asserts both bounds discriminate — and, on the way,
/// that the catalog hands the timestamp back as UTC rather than as a kind-less local-looking value
/// (finding #12).
/// </summary>
public sealed class SearchDateFilterScenario : Scenario
{
    // Fixed instants, far from "now", so the assertions do not depend on when the harness runs.
    private static readonly DateTime MorningUtc = new(2026, 2, 10, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime EveningUtc = new(2026, 2, 11, 20, 0, 0, DateTimeKind.Utc);

    private const string SearchTerm = "dateprobe";
    private const string MorningFile = @"date-filter\dateprobe-morning.jpg";
    private const string EveningFile = @"date-filter\dateprobe-evening.jpg";

    public override string Name => "search-date-filter";

    public override string Description =>
        "Search date bounds: midnight-from keeps that day, midnight-to excludes it; timestamps come back UTC.";

    public override PairRequirement Requires => PairRequirement.Any;

    public override async Task RunAsync(ScenarioContext ctx)
    {
        // ── arrange ───────────────────────────────────────────────────────────
        File.SetLastWriteTimeUtc(ctx.Source.CreateFile(MorningFile, 8 * 1024), MorningUtc);
        File.SetLastWriteTimeUtc(ctx.Source.CreateFile(EveningFile, 8 * 1024), EveningUtc);
        await ctx.IndexSourceAsync(AllowEverything());

        var morning = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(MorningFile));
        var evening = await FindFileRowAsync(ctx, ctx.SourceVolumeId, ctx.Source.RelativePath(EveningFile));

        if (morning is null || evening is null)
        {
            ctx.Assert.Fail("arrange failed: the two probe files are not both in the catalog after the scan.");
            return;
        }

        // ── assert (the catalog speaks UTC) ───────────────────────────────────
        ctx.Assert.Equal(DateTimeKind.Utc, morning.FileModifiedUtc.Kind, "catalog timestamp kind");
        ctx.Assert.Equal(MorningUtc, morning.FileModifiedUtc, "catalog timestamp value for the morning file");

        // ── act + assert (lower bound: the day of the evening file, midnight) ─
        var fromEveningDay = await SearchAsync(ctx, from: new DateTime(2026, 2, 11, 0, 0, 0, DateTimeKind.Utc), to: null);

        ctx.Assert.True(fromEveningDay.Contains(evening.Id),
            $"modifiedFrom at midnight must keep a file modified later that day; hits [{string.Join(", ", fromEveningDay)}], expected id {evening.Id}");
        ctx.Assert.True(!fromEveningDay.Contains(morning.Id),
            $"modifiedFrom must exclude the earlier day; hits [{string.Join(", ", fromEveningDay)}], did not expect id {morning.Id}");

        // ── act + assert (upper bound covers the whole named day) ─────────────
        var toMorningDay = await SearchAsync(
            ctx, from: null, to: new DateTime(2026, 2, 10, 23, 59, 59, 999, DateTimeKind.Utc));

        ctx.Assert.True(toMorningDay.Contains(morning.Id),
            $"modifiedTo at the end of the day must keep that day's file; hits [{string.Join(", ", toMorningDay)}], expected id {morning.Id}");
        ctx.Assert.True(!toMorningDay.Contains(evening.Id),
            $"modifiedTo must exclude the later day; hits [{string.Join(", ", toMorningDay)}], did not expect id {evening.Id}");

        // ── act + assert (upper bound at midnight excludes that day's files) ──
        var toMorningMidnight = await SearchAsync(
            ctx, from: null, to: new DateTime(2026, 2, 10, 0, 0, 0, DateTimeKind.Utc));

        ctx.Assert.True(!toMorningMidnight.Contains(morning.Id),
            $"modifiedTo at midnight must exclude a file modified at 08:00 that day; hits [{string.Join(", ", toMorningMidnight)}]");
    }

    private static Task<IReadOnlyList<int>> SearchAsync(ScenarioContext ctx, DateTime? from, DateTime? to) =>
        ctx.Env.WithScopeAsync<IReadOnlyList<int>>(async sp =>
        {
            var result = await sp.GetRequiredService<IFileSearchIndex>().SearchAsync(
                new FileSearchQuery(
                    Text: SearchTerm, Scope: SearchScope.Name, Category: null, Extensions: null,
                    SizeBytesMin: null, SizeBytesMax: null, ModifiedFrom: from, ModifiedTo: to,
                    VolumeId: ctx.SourceVolumeId, OnlineOnly: false, Sort: SearchSort.Relevance, Desc: false,
                    Skip: 0, Take: 50),
                ctx.Ct);
            return result.Items;
        });
}
