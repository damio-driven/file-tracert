namespace FileTracert.Contracts.Dtos;

/// <summary>Service liveness payload returned by <c>GET /health</c>.</summary>
public sealed record HealthResponse(
    string Status,
    string Version,
    string Database);
