using System.Collections.Concurrent;
using System.Net.Security;
using AditiKraft.Aspire.Hosting.HttpsGateway;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Yarp.ReverseProxy.Forwarder;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.ReverseProxy;

internal sealed class GatewayMiddleware
{
    private readonly IHttpForwarder _forwarder;
    private readonly ILogger<GatewayMiddleware> _logger;
    private readonly HttpsGatewayOptions _options;
    private readonly bool _verboseLogging;

    // Routes are read live from the options because some are registered after the gateway has
    // started (resource endpoints resolve via AfterEndpointsAllocatedEvent). Clients are created
    // lazily per destination and cached.
    private readonly ConcurrentDictionary<string, HttpMessageInvoker> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    public GatewayMiddleware(
        RequestDelegate next,
        IHttpForwarder forwarder,
        ILogger<GatewayMiddleware> logger,
        HttpsGatewayOptions options)
    {
        _forwarder = forwarder;
        _logger = logger;
        _options = options;
        _verboseLogging = options.EnableVerboseProxyLogging;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string host = context.Request.Host.Host;

        if (_verboseLogging)
        {
            _logger.LogInformation(
                "Gateway request: {Method} {Host}{Path}",
                context.Request.Method,
                host,
                context.Request.Path);
        }

        if (TryGetPathRoute(host, context.Request.Path, out string pathDestination, out PathString remainingPath))
        {
            PathString forwardPath = NormalizeForwardPath(remainingPath);

            if (_verboseLogging)
            {
                _logger.LogInformation(
                    "Gateway matched path route {Host}{Path} -> {Destination}{ForwardPath}",
                    host,
                    context.Request.Path,
                    pathDestination,
                    forwardPath);
            }

            await ForwardAsync(context, pathDestination, ClientFor(pathDestination), forwardPath);
            return;
        }

        if (TryGetHostRoute(host, out string destination))
        {
            if (_verboseLogging)
            {
                _logger.LogInformation("Gateway matched route {Host} -> {Destination}", host, destination);
            }

            await ForwardAsync(context, destination, ClientFor(destination));
            return;
        }

        _logger.LogWarning("No gateway route for host {Host}", host);
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync($"No gateway route for host '{host}'.");
    }

    private HttpMessageInvoker ClientFor(string destination) =>
        _clients.GetOrAdd(destination, static _ => CreateClient());

    private bool TryGetHostRoute(string host, out string destination)
    {
        foreach ((string subdomain, string routeDestination) in _options.Routes)
        {
            if (string.Equals($"{subdomain}.{_options.Domain}", host, StringComparison.OrdinalIgnoreCase))
            {
                destination = routeDestination;
                return true;
            }
        }

        destination = string.Empty;
        return false;
    }

    private bool TryGetPathRoute(
        string host,
        PathString requestPath,
        out string destination,
        out PathString remainingPath)
    {
        string subdomain = SubdomainForHost(host);

        if (_options.PathRoutes.TryGetValue(subdomain, out Dictionary<string, string>? routes))
        {
            foreach ((string prefix, string routeDestination) in routes.OrderByDescending(route => route.Key.Length))
            {
                PathString pathPrefix = NormalizePathPrefix(prefix);
                if (requestPath.StartsWithSegments(pathPrefix, out PathString candidateRemainingPath))
                {
                    destination = routeDestination;
                    remainingPath = candidateRemainingPath;
                    return true;
                }
            }
        }

        destination = string.Empty;
        remainingPath = default;
        return false;
    }

    private string SubdomainForHost(string host)
    {
        string suffix = $".{_options.Domain}";
        return host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? host[..^suffix.Length]
            : host;
    }

    private async Task ForwardAsync(
        HttpContext context,
        string destination,
        HttpMessageInvoker client,
        PathString? pathOverride = null)
    {
        PathString originalPath = context.Request.Path;

        try
        {
            if (pathOverride.HasValue)
            {
                context.Request.Path = pathOverride.Value;
            }

            ForwarderError error = await _forwarder.SendAsync(
                context,
                destination,
                client,
                ForwarderRequestConfig.Empty,
                HttpTransformer.Default);

            if (error is not ForwarderError.None)
            {
                LogForwarderError(context, destination, error);

                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = StatusCodes.Status502BadGateway;
                    await context.Response.WriteAsync($"Gateway forward error: {error}");
                }
            }
        }
        finally
        {
            context.Request.Path = originalPath;
        }
    }

    private void LogForwarderError(HttpContext context, string destination, ForwarderError error)
    {
        if (IsExpectedUpgradeDisconnect(error))
        {
            if (_verboseLogging)
            {
                _logger.LogInformation(
                    "Gateway upgraded connection ended for {Method} {Host}{Path}: {Error}",
                    context.Request.Method,
                    context.Request.Host.Host,
                    context.Request.Path,
                    error);
            }

            return;
        }

        _logger.LogWarning(
            "Gateway forward error for {Method} {Host}{Path} -> {Destination}: {Error}",
            context.Request.Method,
            context.Request.Host.Host,
            context.Request.Path,
            destination,
            error);
    }

    private static bool IsExpectedUpgradeDisconnect(ForwarderError error) =>
        error is ForwarderError.UpgradeRequestCanceled
            or ForwarderError.UpgradeRequestClient
            or ForwarderError.UpgradeResponseCanceled
            or ForwarderError.UpgradeResponseClient;

    private static PathString NormalizePathPrefix(string pathPrefix)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix))
        {
            return PathString.Empty;
        }

        return pathPrefix.StartsWith('/') ? pathPrefix : $"/{pathPrefix}";
    }

    private static PathString NormalizeForwardPath(PathString remainingPath) =>
        remainingPath.HasValue ? remainingPath : "/";

    private static HttpMessageInvoker CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            },
            EnableMultipleHttp2Connections = true
        };

        return new HttpMessageInvoker(handler);
    }
}
