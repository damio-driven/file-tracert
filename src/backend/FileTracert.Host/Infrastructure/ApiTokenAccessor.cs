namespace FileTracert.Host.Infrastructure;

/// <summary>
/// Holds the loopback API token once it has been resolved/generated at startup,
/// so the auth middleware can read it without touching the database on the hot path.
/// </summary>
public interface IApiTokenAccessor
{
    /// <summary>Current token, or null before <see cref="Set"/> has run.</summary>
    string? Token { get; }

    void Set(string token);
}

/// <summary>Singleton, thread-safe via a volatile reference write.</summary>
public sealed class ApiTokenAccessor : IApiTokenAccessor
{
    private volatile string? _token;

    public string? Token => _token;

    public void Set(string token) => _token = token;
}
