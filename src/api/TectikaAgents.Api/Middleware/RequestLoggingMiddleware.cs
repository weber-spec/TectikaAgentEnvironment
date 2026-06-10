using System.Diagnostics;
using Microsoft.Extensions.Options;
using TectikaAgents.Core.Configuration;
using TectikaAgents.Core.Observability;

namespace TectikaAgents.Api.Middleware;

/// <summary>
/// Logs one structured line per HTTP request: method, path, status, duration, user.
/// The request body is logged only when LogSensitiveContent is enabled.
/// </summary>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;
    private readonly bool _logSensitive;

    // Cap body logging so large agent payloads (prompts, file/base64 blobs) don't blow up
    // log lines or double peak memory. Above this, log a size marker instead of the content.
    private const int MaxBodyLogBytes = 64 * 1024;

    public RequestLoggingMiddleware(RequestDelegate next, ILogger<RequestLoggingMiddleware> logger,
        IOptions<LoggingSettings> logging)
    {
        _next = next;
        _logger = logger;
        _logSensitive = logging.Value.LogSensitiveContent;
    }

    public async Task Invoke(HttpContext ctx)
    {
        var sw = Stopwatch.StartNew();
        var body = string.Empty;
        if (_logSensitive && (HttpMethods.IsPost(ctx.Request.Method) || HttpMethods.IsPut(ctx.Request.Method)
            || HttpMethods.IsPatch(ctx.Request.Method)))
        {
            var length = ctx.Request.ContentLength;
            if (length is > 0 and <= MaxBodyLogBytes)
            {
                ctx.Request.EnableBuffering();
                using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
                body = await reader.ReadToEndAsync();
                ctx.Request.Body.Position = 0;
            }
            else if (length is > MaxBodyLogBytes)
            {
                body = $"[body too large to log: {length} bytes]";
            }
            // length null (chunked/unknown) or 0 -> leave body empty, don't risk an unbounded read
        }

        _logger.LogInformation(
            "[HttpRequest] {Method} {Path} from user {User} body {Body}",
            ctx.Request.Method, ctx.Request.Path, ctx.User?.Identity?.Name ?? "anonymous",
            SensitiveContent.Format(body, _logSensitive));

        try
        {
            await _next(ctx);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex,
                "[HttpResponse] {Method} {Path} threw after {ElapsedMs}ms",
                ctx.Request.Method, ctx.Request.Path, sw.ElapsedMilliseconds);
            throw;
        }
        finally
        {
            if (sw.IsRunning)
            {
                sw.Stop();
                _logger.LogInformation(
                    "[HttpResponse] {Method} {Path} -> {Status} in {ElapsedMs}ms",
                    ctx.Request.Method, ctx.Request.Path, ctx.Response.StatusCode, sw.ElapsedMilliseconds);
            }
        }
    }
}
