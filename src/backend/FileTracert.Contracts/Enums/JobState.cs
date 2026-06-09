namespace FileTracert.Contracts.Enums;

public enum JobState
{
    Pending,
    SpaceReserved,
    Copying,
    Verifying,
    DeletingSource,
    Completed,
    Blocked,
    Failed,
    Cancelled
}
