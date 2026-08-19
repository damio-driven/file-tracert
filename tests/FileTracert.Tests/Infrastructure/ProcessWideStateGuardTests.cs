using System.Text.RegularExpressions;
using FluentAssertions;

namespace FileTracert.Tests.Infrastructure;

/// <summary>
/// Guards the rule step 11i established: <em>a test may not touch state that belongs to the
/// process</em>. xUnit runs test classes in parallel, so anything process-wide a teardown
/// mutates lands on whoever else is mid-flight — which is how the suite spent three steps
/// losing a different test per run.
/// <para>
/// This scans source, not behaviour, on purpose: the failure it prevents is a race, and a
/// race cannot be asserted reliably after the fact. The reproduction of what goes wrong when
/// the rule is broken lives in <c>SqliteConnectionPoolScopeTests</c>.
/// </para>
/// </summary>
public sealed class ProcessWideStateGuardTests
{
    // Split so this file is not its own first match. The trailing parenthesis makes it the
    // *call* that is forbidden, not the name: tests and messages are free to say it out loud.
    private const string ForbiddenName = "ClearAll" + "Pools";
    private const string ForbiddenCall = ForbiddenName + "(";

    /// <summary>
    /// The only two places allowed to clear every pool, both because they own their process:
    /// the hardware harness runs single-threaded with nothing else alive to disturb, and the
    /// pool probe exists precisely to demonstrate the damage — in a child process, where the
    /// only victim is itself. Remove the harness from this list the day it gets a targeted
    /// teardown, and run it on real hardware, which is the price of touching it.
    /// </summary>
    private static readonly string[] AllowedFiles =
    [
        Path.Combine("src", "backend", "FileTracert.HardwareSmoke", "Harness", "ScenarioEnvironment.cs"),
        Path.Combine("tests", "FileTracert.PoolProbe", "Program.cs"),
    ];

    [Fact]
    public void Only_the_single_threaded_harness_clears_every_connection_pool()
    {
        var root = RepositoryRoot();

        var offenders = new List<string>();
        foreach (var directory in new[] { Path.Combine(root, "src"), Path.Combine(root, "tests") })
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                // bin/obj hold copies and generated code, not sources anyone maintains.
                var relative = Path.GetRelativePath(root, file);
                if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                    || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                {
                    continue;
                }

                // Comments are stripped so the files that *explain* the rule — this one
                // included — are not read as breaking it.
                if (WithoutComments(File.ReadAllText(file)).Contains(ForbiddenCall, StringComparison.Ordinal)
                    && !AllowedFiles.Contains(relative, StringComparer.OrdinalIgnoreCase))
                {
                    offenders.Add(relative);
                }
            }
        }

        offenders.Should().BeEmpty(
            "SqliteConnection.{0}() is process-wide: it disposes the native sqlite3 handle of every "
            + "connection in the process, including the ones other test classes are querying right "
            + "now. Release the database this code owns with SqliteTestDatabase instead.",
            ForbiddenName);
    }

    /// <summary>
    /// Drops <c>//</c> and <c>/* */</c> comments. Deliberately crude — it only has to tell
    /// code from prose, and the one thing it must never do is hide a real call.
    /// </summary>
    private static string WithoutComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
    }

    /// <summary>Walks up from the test binaries to the repository (the folder holding src and tests).</summary>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "src"))
                && Directory.Exists(Path.Combine(directory.FullName, "tests")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"could not find the repository root above '{AppContext.BaseDirectory}'");
    }
}
