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
        var expected = _tokenAccessor.Token;
        if (string.IsNullOrEmpty(expected) || !IsValid(context, expected))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context);
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
