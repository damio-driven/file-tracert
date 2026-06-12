namespace FileTracert.Host.Infrastructure;

/// <summary>
/// Wires the SPA: a token-injecting fallback to <c>index.html</c> for client-side
/// routes, plus a Development-only bootstrap endpoint that hands the dev SPA its
/// token (in production the token is baked into the served HTML instead).
/// </summary>
public static class SpaEndpoints
{
    /// <summary>Placeholder in the built <c>index.html</c>, replaced with the live token.</summary>
    public const string TokenPlaceholder = "__FT_TOKEN__";

    /// <summary>
    /// Serves <c>wwwroot/index.html</c> for any unmatched (non-API) route, replacing
    /// the token placeholder with the runtime token so the SPA can read it from the
    /// <c>&lt;meta&gt;</c> tag. No-ops gracefully when no build is present (e.g. tests).
    /// </summary>
    public static void MapSpaFallback(this WebApplication app)
    {
        // Render once: the token is fixed for the process lifetime.
        var lazyHtml = new Lazy<string?>(() => RenderIndex(app));

        app.MapFallback(async context =>
        {
            // Unmatched API routes are genuine 404s, not SPA navigations — never
            // hand them the index shell.
            if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var html = lazyHtml.Value;
            if (html is null)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(html, context.RequestAborted);
        });
    }

    /// <summary>Development-only: lets <c>ng serve</c> fetch the token at startup.</summary>
    public static void MapDevTokenEndpoint(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            return;
        }

        app.MapGet("/api/dev/token", (IApiTokenAccessor tokens) =>
            Results.Ok(new { token = tokens.Token }));
    }

    private static string? RenderIndex(WebApplication app)
    {
        var webRoot = app.Environment.WebRootPath ?? Path.Combine(app.Environment.ContentRootPath, "wwwroot");
        var indexPath = Path.Combine(webRoot, "index.html");
        if (!File.Exists(indexPath))
        {
            return null;
        }

        var token = app.Services.GetRequiredService<IApiTokenAccessor>().Token ?? string.Empty;
        return File.ReadAllText(indexPath).Replace(TokenPlaceholder, token, StringComparison.Ordinal);
    }
}
