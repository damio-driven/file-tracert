using System.Diagnostics;
using FileTracert.HardwareSmoke.Report;
using FileTracert.HardwareSmoke.Scenarios;
using Microsoft.Extensions.Logging;

namespace FileTracert.HardwareSmoke.Harness;

/// <summary>
/// Runs every applicable scenario on every generated volume pair and collects the report rows.
/// Each run gets its own isolated <see cref="ScenarioEnvironment"/> and its own fixture folders,
/// so one scenario's outcome can never explain another's.
/// </summary>
public sealed class HarnessRunner
{
    private readonly HardwareSmokeOptions _options;
    private readonly IReadOnlyList<Scenario> _scenarios;
    private readonly IHarnessConsole _console;
    private readonly LogLevel _serviceLogLevel;

    public HarnessRunner(
        HardwareSmokeOptions options,
        IReadOnlyList<Scenario> scenarios,
        IHarnessConsole console,
        LogLevel serviceLogLevel = LogLevel.Warning)
    {
        _options = options;
        _scenarios = scenarios;
        _console = console;
        _serviceLogLevel = serviceLogLevel;
    }

    public async Task<IReadOnlyList<ScenarioResult>> RunAsync(
        IReadOnlyList<VolumePair> pairs, string workRoot, CancellationToken ct)
    {
        var results = new List<ScenarioResult>();

        for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
        {
            var pair = pairs[pairIndex];

            foreach (var scenario in _scenarios)
            {
                if (!scenario.AppliesTo(pair, _options)) continue;

                ct.ThrowIfCancellationRequested();
                _console.Write($"▶ {scenario.Name} [{pair.Label}] — {scenario.Description}");
                results.Add(await RunOneAsync(scenario, pair, pairIndex, workRoot, ct));
            }
        }

        return results;
    }

    private async Task<ScenarioResult> RunOneAsync(
        Scenario scenario, VolumePair pair, int pairIndex, string workRoot, CancellationToken ct)
    {
        // The fixture folder name must be unique per (scenario, pair): the same volume can take
        // part in several pairs, and two runs sharing a folder would assert on each other's files.
        var runKey = $"{HarnessPaths.Slug(scenario.Name)}__p{pairIndex}";
        var stopwatch = Stopwatch.StartNew();

        ScenarioEnvironment? environment = null;
        QueueDriver? queue = null;

        // Interactive scenarios wait on a human: a wall-clock cap would kill them at the prompt.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (!scenario.NeedsSemiAutomatic)
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ScenarioTimeoutSeconds * 2));

        try
        {
            environment = await ScenarioEnvironment.CreateAsync(
                pair,
                Path.Combine(workRoot, runKey),
                _serviceLogLevel,
                line => _console.Write($"    {line}"),
                timeoutCts.Token);

            queue = new QueueDriver(environment.Services, line => _console.Write($"    {line}"));

            var context = new ScenarioContext(
                scenario,
                _options,
                pair,
                environment,
                queue,
                new FixtureArea(pair.Source, runKey, "source"),
                new FixtureArea(pair.Target, runKey, "target"),
                _console,
                timeoutCts.Token);

            await scenario.RunAsync(context);

            stopwatch.Stop();
            return context.Assert.AnyFailed
                ? ScenarioResult.Fail(scenario.Name, pair.Label, context.Assert.Failures, stopwatch.Elapsed)
                : ScenarioResult.Pass(scenario.Name, pair.Label, stopwatch.Elapsed);
        }
        catch (ScenarioSkippedException ex)
        {
            stopwatch.Stop();
            return ScenarioResult.Skipped(scenario.Name, pair.Label, ex.Message, stopwatch.Elapsed);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return ScenarioResult.Fail(
                scenario.Name, pair.Label,
                [$"the scenario exceeded its wall-clock budget of {_options.ScenarioTimeoutSeconds * 2}s and was aborted."],
                stopwatch.Elapsed);
        }
        catch (Exception ex)
        {
            // Never silent (§9): the full exception (type, message, stack, inner) reaches the
            // report the operator reads, and the run continues with the next scenario.
            stopwatch.Stop();
            return ScenarioResult.Fail(
                scenario.Name, pair.Label,
                [$"unhandled {ex.GetType().Name}: {ex.Message}", ex.ToString()],
                stopwatch.Elapsed);
        }
        finally
        {
            if (queue is not null) await queue.DisposeAsync();
            if (environment is not null) await environment.DisposeAsync();
        }
    }
}
