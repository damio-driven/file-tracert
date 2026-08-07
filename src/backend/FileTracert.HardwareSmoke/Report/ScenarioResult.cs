namespace FileTracert.HardwareSmoke.Report;

public enum ScenarioOutcome
{
    Pass,
    Fail,

    /// <summary>
    /// The scenario could not produce a verdict on this machine (fixture too small to interrupt,
    /// no external drive, …). Never counted as a pass — but it does not fail the run either.
    /// </summary>
    Skipped,
}

/// <summary>One row of the final report.</summary>
/// <param name="Scenario">Scenario name.</param>
/// <param name="Pair">The source→target combination it ran on.</param>
/// <param name="Outcome">Verdict.</param>
/// <param name="Failures">Assertion failures, empty unless <see cref="Outcome"/> is Fail.</param>
/// <param name="Note">Skip reason, or extra context for a failure.</param>
/// <param name="Duration">Wall-clock time of the scenario.</param>
public sealed record ScenarioResult(
    string Scenario,
    string Pair,
    ScenarioOutcome Outcome,
    IReadOnlyList<string> Failures,
    string? Note,
    TimeSpan Duration)
{
    public static ScenarioResult Pass(string scenario, string pair, TimeSpan duration) =>
        new(scenario, pair, ScenarioOutcome.Pass, [], null, duration);

    public static ScenarioResult Fail(
        string scenario, string pair, IReadOnlyList<string> failures, TimeSpan duration, string? note = null) =>
        new(scenario, pair, ScenarioOutcome.Fail, failures, note, duration);

    public static ScenarioResult Skipped(string scenario, string pair, string reason, TimeSpan duration) =>
        new(scenario, pair, ScenarioOutcome.Skipped, [], reason, duration);
}
