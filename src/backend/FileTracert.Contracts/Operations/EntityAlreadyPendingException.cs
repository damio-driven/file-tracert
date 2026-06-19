namespace FileTracert.Contracts.Operations;

/// <summary>
/// Thrown by <see cref="IQueueService.EnqueueAsync"/> when the requested source entity
/// already has a non-terminal operation pending.
/// MVP guard: one pending op per entity at a time.
/// </summary>
public sealed class EntityAlreadyPendingException : Exception
{
    public EntityAlreadyPendingException(string entityType, int entityId)
        : base($"{entityType} {entityId} already has a non-terminal operation pending.")
    {
        EntityType = entityType;
        EntityId = entityId;
    }

    public string EntityType { get; }
    public int EntityId { get; }
}
