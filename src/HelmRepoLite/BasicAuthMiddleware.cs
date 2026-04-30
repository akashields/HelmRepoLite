using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace HelmRepoLite;

/// <summary>
/// Minimal HTTP Basic auth middleware. Activated only if a username is configured.
/// When <see cref="ServerOptions.AnonymousGet"/> is true, GETs and HEADs are exempt.
/// </summary>
public sealed class BasicAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ServerOptions _options;
    private readonly byte[] _userBytes;
    private readonly byte[] _passBytes;

    public BasicAuthMiddleware(RequestDelegate next, ServerOptions options)
    {
        _next = next;
        _options = options;
        _userBytes = Encoding.UTF8.GetBytes(options.BasicAuthUser);
        _passBytes = Encoding.UTF8.GetBytes(options.BasicAuthPass);
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (string.IsNullOrEmpty(_options.BasicAuthUser))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        // Health endpoints must always be reachable by the kubelet regardless of auth settings.
        if (ctx.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        // Allow anonymous reads if enabled.
        if (_options.AnonymousGet && (HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
        {
            await _next(ctx).ConfigureAwait(false);
            return;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header) || !header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            Challenge(ctx);
            return;
        }

        try
        {
            var raw = Convert.FromBase64String(header[6..].Trim());
            var idx = Array.IndexOf(raw, (byte)':');
            if (idx < 0) { Challenge(ctx); return; }

            var user = raw.AsSpan(0, idx);
            var pass = raw.AsSpan(idx + 1);

            // CryptographicOperations.FixedTimeEquals is constant-time.
            var userOk = user.Length == _userBytes.Length &&
                         CryptographicOperations.FixedTimeEquals(user, _userBytes);
            var passOk = pass.Length == _passBytes.Length &&
                         CryptographicOperations.FixedTimeEquals(pass, _passBytes);

            if (!userOk || !passOk) { Challenge(ctx); return; }
        }
        catch (FormatException)
        {
            Challenge(ctx);
            return;
        }

        await _next(ctx).ConfigureAwait(false);
    }

    private static void Challenge(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"helmrepolite\", charset=\"UTF-8\"";
    }
}
