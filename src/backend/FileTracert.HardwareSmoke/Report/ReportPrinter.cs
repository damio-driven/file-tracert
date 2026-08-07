using System.Text;

namespace FileTracert.HardwareSmoke.Report;

/// <summary>
/// Renders the final scenario table and decides the process exit code, so the harness is
/// scriptable: <c>0</c> only when nothing failed.
/// </summary>
public static class ReportPrinter
{
    /// <summary>Exit code: non-zero when at least one scenario failed. Skips do not fail the run.</summary>
    public static int ExitCodeFor(IReadOnlyList<ScenarioResult> results) =>
        results.Any(r => r.Outcome == ScenarioOutcome.Fail) ? 1 : 0;

    public static string Render(IReadOnlyList<ScenarioResult> results, IReadOnlyList<string> notes)
    {
        var sb = new StringBuilder();

        sb.AppendLine();
        sb.AppendLine("═══ HARDWARE HARNESS REPORT ═══");
        sb.AppendLine();

        if (results.Count == 0)
        {
            sb.AppendLine("No scenario ran.");
        }
        else
        {
            var scenarioWidth = Math.Max(8, results.Max(r => r.Scenario.Length));
            var pairWidth = Math.Max(4, results.Max(r => r.Pair.Length));

            sb.Append(Pad("SCENARIO", scenarioWidth)).Append("  ")
              .Append(Pad("PAIR", pairWidth)).Append("  ")
              .Append(Pad("OUTCOME", 7)).Append("  ")
              .AppendLine("TIME");
            sb.AppendLine(new string('─', scenarioWidth + pairWidth + 7 + 8 + 6));

            foreach (var r in results)
            {
                sb.Append(Pad(r.Scenario, scenarioWidth)).Append("  ")
                  .Append(Pad(r.Pair, pairWidth)).Append("  ")
                  .Append(Pad(Label(r.Outcome), 7)).Append("  ")
                  .AppendLine($"{r.Duration.TotalSeconds,6:0.0}s");
            }

            sb.AppendLine();
            foreach (var r in results.Where(r => r.Outcome != ScenarioOutcome.Pass))
            {
                sb.AppendLine($"── {Label(r.Outcome)}: {r.Scenario} [{r.Pair}]");
                if (r.Note is not null)
                    sb.AppendLine($"   note: {r.Note}");
                foreach (var failure in r.Failures)
                    sb.AppendLine($"   • {failure}");
                sb.AppendLine();
            }

            sb.AppendLine(
                $"TOTAL {results.Count}   " +
                $"PASS {results.Count(r => r.Outcome == ScenarioOutcome.Pass)}   " +
                $"FAIL {results.Count(r => r.Outcome == ScenarioOutcome.Fail)}   " +
                $"SKIP {results.Count(r => r.Outcome == ScenarioOutcome.Skipped)}");
        }

        if (notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Notes:");
            foreach (var note in notes)
                sb.AppendLine($"  • {note}");
        }

        return sb.ToString();
    }

    private static string Label(ScenarioOutcome outcome) => outcome switch
    {
        ScenarioOutcome.Pass => "PASS",
        ScenarioOutcome.Fail => "FAIL",
        _ => "SKIP",
    };

    private static string Pad(string value, int width) =>
        value.Length >= width ? value : value + new string(' ', width - value.Length);
}
