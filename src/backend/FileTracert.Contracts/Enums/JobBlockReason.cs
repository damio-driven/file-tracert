namespace FileTracert.Contracts.Enums;

public enum JobBlockReason
{
    None,
    InsufficientSpace,
    TargetVolumeOffline,
    SourceVolumeOffline,
    NameCollision,
    DependencyPending,
    DependencyCancelled
}
