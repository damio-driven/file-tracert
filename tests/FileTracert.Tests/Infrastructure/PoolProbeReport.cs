using System.Diagnostics;

namespace FileTracert.Tests.Infrastructure;

/// <summary>
/// Runs <c>FileTracert.PoolProbe</c> once for the whole test class and exposes what it
/// printed. The probe is a separate process on purpose (see its own summary): it calls
/// <c>SqliteConnection.ClearAllPools()</c>, which is process-wide, so running it in the
/// test host would break whatever else is mid-query at that moment — the defect step 11i
/// closes.
/// </summary>
public sealed class PoolProbeReport
{
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

    public PoolProbeReport()
    {
        var directory = AppContext.BaseDirectory;
        var exe = Path.Combine(directory, "FileTracert.PoolProbe.exe");
        var dll = Path.Combine(directory, "FileTracert.PoolProbe.dll");

        ProcessStartInfo start;
        if (File.Exists(exe))
        {
            start = new ProcessStartInfo(exe);
        }
        else if (File.Exists(dll))
        {
            start = new ProcessStartInfo("dotnet");
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add(dll);
        }
        else
        {
            // Loud, not skipped: a missing probe means the proof stopped being run.
            throw new InvalidOperationException(
                $"FileTracert.PoolProbe was not copied next to the tests ('{directory}'). " +
                "The reproduction of the process-wide pool race cannot run.");
        }

        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.UseShellExecute = false;

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("could not start FileTracert.PoolProbe");

        // Read both pipes before waiting, or a full stderr buffer deadlocks the child.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(milliseconds: 180_000))
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidOperationException("FileTracert.PoolProbe did not finish within 180 s");
        }

        StandardOutput = stdout.GetAwaiter().GetResult();
        StandardError = stderr.GetAwaiter().GetResult();
        ExitCode = process.ExitCode;

        foreach (var line in StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                _values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }
    }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public int ExitCode { get; }

    public string this[string key] =>
        _values.TryGetValue(key, out var value)
            ? value
            : throw new KeyNotFoundException($"the probe printed no '{key}'. Full output:\n{Transcript}");

    /// <summary>Everything the probe said — attached to every assertion message.</summary>
    public string Transcript => $"exit={ExitCode}\nstdout:\n{StandardOutput}\nstderr:\n{StandardError}";
}
