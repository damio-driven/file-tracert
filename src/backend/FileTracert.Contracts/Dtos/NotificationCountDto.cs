namespace FileTracert.Contracts.Dtos;

/// <summary>Count of unread, non-dismissed notifications — drives the bell badge.</summary>
public sealed record NotificationCountDto(int Unread);
