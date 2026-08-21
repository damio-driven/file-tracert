using System.Text.RegularExpressions;
using FileTracert.Host.Configuration;
using FluentAssertions;

namespace FileTracert.Tests.Host;

/// <summary>
/// The catalog has one home, and the service has to be able to find it. Running as
/// <c>LocalSystem</c>, a per-user default resolves under
/// <c>C:\Windows\System32\config\systemprofile</c> — the service would start on an empty
/// database and say nothing about it, which is a data loss the user only discovers by
/// noticing their catalog is gone.
/// </summary>
public sealed class DatabaseLocationTests
{
    [Fact]
    public void The_default_database_is_machine_wide_not_per_user()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        DatabaseLocation.DefaultPath.Should().Be(
            Path.Combine(programData, "FileTracert", "filetracert.db"));

        // Stated separately because it is the actual requirement: whatever the folder is named,
        // it must not be the profile of whoever happens to run the host.
        DatabaseLocation.DefaultPath.Should().NotStartWith(localAppData);
    }

    [Fact]
    public void The_log_database_is_a_sibling_of_the_catalog()
    {
        DatabaseLocation.ResolveLogs(DatabaseLocation.DefaultPath).Should().Be(
            Path.Combine(
                Path.GetDirectoryName(DatabaseLocation.DefaultPath)!,
                "filetracert-logs.db"));
    }

    [Fact]
    public void An_explicit_path_wins_and_its_folder_is_created()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"ft-dbloc-{Guid.NewGuid():N}", "nested");
        var expected = Path.Combine(folder, "custom.db");
        try
        {
            var resolved = DatabaseLocation.Resolve(new FileTracertOptions { DatabasePath = expected });

            resolved.Should().Be(expected);
            Directory.Exists(folder).Should().BeTrue();
        }
        finally
        {
            var root = Path.GetDirectoryName(folder);
            if (root is not null && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void No_configured_path_falls_back_to_the_machine_wide_default()
    {
        DatabaseLocation.Resolve(new FileTracertOptions { DatabasePath = "   " })
            .Should().Be(DatabaseLocation.DefaultPath);
    }

    /// <summary>
    /// Source scan, deliberately: the convention used to be written twice — once in the Host and
    /// once in the harness, whose guard reads the production database to learn which folders hold
    /// the user's catalogued data. A second copy left behind would not just be duplication; it
    /// would point that guard at a database that does not exist and let it report "nothing
    /// catalogued here" about a drive full of it. One derivation, asked of one place.
    /// </summary>
    [Fact]
    public void Only_DatabaseLocation_derives_the_database_folder_from_a_special_folder()
    {
        const string forbidden = "SpecialFolder.Local" + "ApplicationData";
        var root = RepositoryRoot();
        var backend = Path.Combine(root, "src", "backend");

        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(backend, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file);
            if (relative.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || relative.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            {
                continue;
            }

            if (WithoutComments(File.ReadAllText(file)).Contains(forbidden, StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        offenders.Should().BeEmpty(
            "the database path is machine-wide and lives in DatabaseLocation.DefaultPath; a "
            + "per-user copy would hand the service (LocalSystem) a different, empty catalog");
    }

    /// <summary>Drops comments so the files that explain the rule are not read as breaking it.</summary>
    private static string WithoutComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutBlocks, @"(?m)^[ \t]*//[^\n]*", " ");
    }

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
