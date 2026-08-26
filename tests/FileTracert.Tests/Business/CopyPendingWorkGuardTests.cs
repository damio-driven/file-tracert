using FileTracert.Business.Operations;
using FileTracert.Contracts.Enums;
using FileTracert.Data.Entities;
using FluentAssertions;

namespace FileTracert.Tests.Business;

/// <summary>
/// Step 15a, third of the three things a Copy makes different: <b>the source survives</b>.
///
/// The enqueue guard serializes operations by path overlap on the assumption that whoever touches
/// a path is going to TAKE IT AWAY (§5, one pending operation per entity). A copy only reads it.
/// So a copy's source stops being a SOURCE claim — but it does not become a target claim either,
/// which is what these tests pin down: two copies of one file to two destinations must both be
/// Pending, while anything that would move, rename or land on that file while it is being read
/// must still conflict.
///
/// Pure claim algebra, no database: <see cref="PendingWorkGuard.ClaimsOf"/> and
/// <see cref="PendingWorkGuard.Conflicts"/> are the whole rule (the SQL side only narrows
/// candidates), so testing them directly tests the decision rather than a query plan.
/// </summary>
public sealed class CopyPendingWorkGuardTests
{
    private const int Vol1 = 1;
    private const int Vol2 = 2;

    private static List<OperationJobItem> FileItem(string source, string target) =>
        [new OperationJobItem { FileId = 42, SourceRelativePath = source, TargetRelativePath = target }];

    private static List<OperationJobItem> FolderItem(string source, string target) =>
        [new OperationJobItem { FileId = null, SourceRelativePath = source, TargetRelativePath = target }];

    private static IReadOnlyCollection<PathClaim> Claims(
        JobType type, string source, string target, int sourceVol = Vol1, int targetVol = Vol1) =>
        PendingWorkGuard.ClaimsOf(
            type, sourceVol, targetVol, target,
            type is JobType.CopyFolder or JobType.MoveFolder or JobType.RenameFolder
                ? FolderItem(source, target)
                : FileItem(source, target));

    // ── a copy does not claim its own source ──────────────────────────────────

    [Fact]
    public void Two_copies_of_the_same_file_to_different_places_do_not_conflict()
    {
        var a = Claims(JobType.CopyFile, @"Docs\report.txt", @"Backup\report.txt");
        var b = Claims(JobType.CopyFile, @"Docs\report.txt", @"Archivio\report.txt");

        // The case the whole change exists for. Both read the same bytes and neither removes
        // them, so both must run.
        PendingWorkGuard.Conflicts(a, b).Should().BeFalse();
    }

    [Fact]
    public void Two_copies_of_the_same_folder_to_different_places_do_not_conflict()
    {
        var a = Claims(JobType.CopyFolder, "Docs", @"Backup\Docs");
        var b = Claims(JobType.CopyFolder, "Docs", @"Archivio\Docs");

        PendingWorkGuard.Conflicts(a, b).Should().BeFalse();
    }

    [Fact]
    public void A_copy_of_a_file_inside_a_folder_another_copy_is_reading_does_not_conflict()
    {
        var folder = Claims(JobType.CopyFolder, "Docs", @"Backup\Docs");
        var file = Claims(JobType.CopyFile, @"Docs\report.txt", @"Archivio\report.txt");

        // Two readers of overlapping trees are still two readers.
        PendingWorkGuard.Conflicts(folder, file).Should().BeFalse();
    }

    // ── but everything that can pull the source away still conflicts ──────────

    [Fact]
    public void A_copy_conflicts_with_a_move_of_its_own_source()
    {
        var copy = Claims(JobType.CopyFile, @"Docs\report.txt", @"Backup\report.txt");
        var move = Claims(JobType.MoveFile, @"Docs\report.txt", @"Altro\report.txt");

        PendingWorkGuard.Conflicts(copy, move).Should().BeTrue();
        // Symmetric, whichever one is enqueued second.
        PendingWorkGuard.Conflicts(move, copy).Should().BeTrue();
    }

    [Fact]
    public void A_copy_conflicts_with_a_rename_of_its_own_source()
    {
        var copy = Claims(JobType.CopyFile, @"Docs\report.txt", @"Backup\report.txt");
        var rename = Claims(JobType.RenameFile, @"Docs\report.txt", @"Docs\report_v2.txt");

        PendingWorkGuard.Conflicts(copy, rename).Should().BeTrue();
    }

    [Fact]
    public void A_copy_conflicts_with_a_folder_move_that_takes_its_source_subtree_away()
    {
        var copy = Claims(JobType.CopyFile, @"Docs\Sub\report.txt", @"Backup\report.txt");
        var moveFolder = Claims(JobType.MoveFolder, "Docs", @"Altro\Docs");

        PendingWorkGuard.Conflicts(copy, moveFolder).Should().BeTrue();
    }

    [Fact]
    public void A_copy_conflicts_with_a_job_landing_ON_the_file_it_is_reading()
    {
        var copy = Claims(JobType.CopyFile, @"Docs\report.txt", @"Backup\report.txt");
        // Someone moves a different file ONTO the path this copy reads from.
        var move = Claims(JobType.MoveFile, @"Altro\report.txt", @"Docs\report.txt");

        // The source can change under the copy — the bytes it reads would be whichever file won
        // the race, and the move would hit its own collision against our source.
        PendingWorkGuard.Conflicts(copy, move).Should().BeTrue();
    }

    // ── the target half of a copy is a target like any other ──────────────────

    [Fact]
    public void Two_copies_landing_on_the_same_destination_conflict()
    {
        var a = Claims(JobType.CopyFile, @"Docs\report.txt", @"Backup\report.txt");
        var b = Claims(JobType.CopyFile, @"Altro\report.txt", @"Backup\report.txt");

        PendingWorkGuard.Conflicts(a, b).Should().BeTrue();
    }

    [Fact]
    public void A_copy_landing_where_a_move_is_taking_something_away_conflicts()
    {
        var copy = Claims(JobType.CopyFile, @"Docs\report.txt", @"Backup\report.txt");
        var move = Claims(JobType.MoveFile, @"Backup\report.txt", @"Altro\report.txt");

        PendingWorkGuard.Conflicts(copy, move).Should().BeTrue();
    }

    [Fact]
    public void A_copy_into_a_folder_another_job_is_about_to_create_stays_legal()
    {
        // §5's «queue folder X, then put files into it» — targets that merely nest never conflict,
        // and a copy must not be the exception that breaks it.
        var create = PendingWorkGuard.ClaimsOf(
            JobType.CreateFolder, null, Vol1, @"Backup\Nuova", []);
        var copy = Claims(JobType.CopyFile, @"Docs\report.txt", @"Backup\Nuova\report.txt");

        PendingWorkGuard.Conflicts(create, copy).Should().BeFalse();
    }

    // ── volumes still separate places ─────────────────────────────────────────

    [Fact]
    public void The_same_relative_path_on_another_volume_is_another_place()
    {
        var copy = Claims(JobType.CopyFile, @"Docs\report.txt", @"Backup\report.txt");
        var move = Claims(JobType.MoveFile, @"Docs\report.txt", @"Altro\report.txt",
            sourceVol: Vol2, targetVol: Vol2);

        PendingWorkGuard.Conflicts(copy, move).Should().BeFalse();
    }
}
