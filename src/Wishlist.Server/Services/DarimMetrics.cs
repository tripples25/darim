using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Prometheus;

namespace Wishlist.Server.Services;

public sealed class DarimMetrics
{
    private readonly CollectorRegistry? registry;
    private readonly byte[]? token;
    private readonly Counter? requests;
    private readonly Histogram? duration;
    private readonly Counter? sync;

    public DarimMetrics(IConfiguration configuration)
    {
        var configuredToken = configuration["Monitoring:Token"];
        if (configuredToken is not { Length: 64 } || !configuredToken.All(Uri.IsHexDigit)) return;

        token = Encoding.ASCII.GetBytes(configuredToken);
        registry = Metrics.NewCustomRegistry();
        var metrics = Metrics.WithCustomRegistry(registry);
        requests = metrics.CreateCounter("darim_http_requests_total", "Completed Darim HTTP requests.",
            ["method", "route", "status_class"]);
        duration = metrics.CreateHistogram("darim_http_request_duration_seconds", "Darim HTTP request duration in seconds.",
            new HistogramConfiguration
            {
                LabelNames = ["method", "route"],
                Buckets = [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30]
            });
        sync = metrics.CreateCounter("darim_ohmywishes_sync_total", "Outcomes of scheduled Ohmywishes sync.", ["outcome"]);
        foreach (var outcome in Enum.GetValues<SyncOutcome>()) sync.WithLabels(OutcomeLabel(outcome));
    }

    public async Task Scrape(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (registry is null || token is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var authorization = context.Request.Headers.Authorization;
        if (authorization.Count != 1 || !AuthenticationHeaderValue.TryParse(authorization[0], out var header) ||
            !header.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            header.Parameter is not { Length: 64 } supplied ||
            !CryptographicOperations.FixedTimeEquals(token, Encoding.UTF8.GetBytes(supplied)))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }

        if (!HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers.Allow = HttpMethods.Get;
            return;
        }

        context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        await registry.CollectAndExportAsTextAsync(context.Response.Body, context.RequestAborted);
    }

    public void RecordRequest(string method, string route, int status, double seconds)
    {
        if (requests is null || duration is null) return;

        method = method is "GET" or "POST" or "PUT" or "DELETE" or "PATCH" or "HEAD" or "OPTIONS" ? method : "OTHER";
        var statusClass = status switch
        {
            499 => "aborted",
            >= 100 and <= 599 => $"{status / 100}xx",
            _ => "other"
        };
        requests.WithLabels(method, route, statusClass).Inc();
        duration.WithLabels(method, route).Observe(seconds);
    }

    public void RecordSync(SyncOutcome outcome) => sync?.WithLabels(OutcomeLabel(outcome)).Inc();

    private static string OutcomeLabel(SyncOutcome outcome) => outcome switch
    {
        SyncOutcome.Applied => "applied",
        SyncOutcome.ProviderError => "provider_error",
        SyncOutcome.ReconnectRequired => "reconnect_required",
        SyncOutcome.WorkerError => "worker_error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome))
    };

    public enum SyncOutcome { Applied, ProviderError, ReconnectRequired, WorkerError }
}
