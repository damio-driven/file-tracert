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

    /// <summary>
    /// The data API (<c>/api/*</c>) and the diagnostic <c>/health</c> require the
    /// token. The dev token bootstrap endpoint is the one <c>/api</c> exception:
    /// the SPA must reach it before it has a token.
    /// </summary>
    private static bool RequiresAuth(PathString path)
    {
        if (path.StartsWithSegments("/api/dev/token", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsValid(HttpContext context, string expected)
    {
        if (!context.Request.Headers.TryGetValue(_headerName, out var provided) || provided.Count != 1)
        {
            return false;
        }

        var a = Encoding.UTF8.GetBytes(provided!);
        var b = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
