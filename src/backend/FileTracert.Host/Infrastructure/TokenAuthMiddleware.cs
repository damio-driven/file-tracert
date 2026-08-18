using System.Security.Cryptography;
using System.Text;
using FileTracert.Host.Configuration;
using Microsoft.Extensions.Options;

namespace FileTracert.Host.Infrastructure;

/// <summary>
/// Minimal single-user loopback auth: every request must carry the configured
/// header with the startup token. Missing/wrong token → 401. Comparison is
/// fixed-time to avoid leaking the token by timing.
/// </summary>
public sealed class TokenAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IApiTokenAccessor _tokenAccessor;
    private readonly string _headerName;

    public TokenAuthMiddleware(RequestDelegate next, IApiTokenAccessor tokenAccessor, IOptions<FileTracertOptions> options)
    {
        _next = next;
        _tokenAccessor = tokenAccessor;
        _headerName = options.Value.TokenHeader;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!RequiresAuth(context.Request.Path))
        {
            // Static SPA assets, the injected index.html and the dev-only token
            // endpoint are served same-origin without the token (the HTML carries it).
            await _next(context);
            return;
        }

        var expected = _tokenAccessor.Token;
        if (string.IsNullOrEmpty(expected) || !IsValid(context, expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
    }

    /// <summary>Query-string parameter carrying the token on the hub path. See <see cref="ReadToken"/>.</summary>
    private const string AccessTokenQueryParameter = "access_token";

    /// <summary>Prefix of the SignalR hub endpoints (<c>/hubs/events</c>).</summary>
    private const string HubPrefix = "/hubs";

    /// <summary>
    /// The data API (<c>/api/*</c>), the SignalR hubs (<c>/hubs/*</c>) and the diagnostic
    /// <c>/health</c> require the token. The dev token bootstrap endpoint is the one <c>/api</c>
    /// exception: the SPA must reach it before it has a token.
    /// </summary>
    private static bool RequiresAuth(PathString path)
    {
        if (path.StartsWithSegments("/api/dev/token", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments(HubPrefix, StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsValid(HttpContext context, string expected)
    {
        var provided = ReadToken(context);
        if (provided is null)
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(provided);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }

    /// <summary>
    /// The header is the normal channel. On the hub path only, the query string
    /// <c>?access_token=…</c> is accepted as well: the browser's WebSocket handshake cannot carry
    /// custom headers, so the SignalR client has no other way to authenticate the socket.
    ///
    /// Security note — a token in a query string is less confidential than one in a header: it is
    /// the kind of value that ends up in access logs, proxy logs and HTTP telemetry. It is accepted
    /// here because Kestrel binds <c>127.0.0.1</c> only and the token is a local, single-user
    /// secret (§3 "Security locale"), and because our own logging keeps framework categories at
    /// Warning so no request line is written at the default level (see <c>LogCategoryPolicy</c>).
    /// It is a deliberate trade-off, not an oversight — hence the narrow path restriction.
    /// </summary>
    private string? ReadToken(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(_headerName, out var header) && header.Count == 1)
        {
            return header!;
        }

        if (context.Request.Path.StartsWithSegments(HubPrefix, StringComparison.OrdinalIgnoreCase) &&
            context.Request.Query.TryGetValue(AccessTokenQueryParameter, out var query) && query.Count == 1)
        {
            return query!;
        }

        return null;
    }
}
