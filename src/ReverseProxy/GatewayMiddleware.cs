using System.Net.Security;
using AditiKraft.Aspire.Hosting.HttpsGateway;
using Microsoft.AspNetCore.Http;
using Yarp.ReverseProxy.Forwarder;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.ReverseProxy;

internal sealed class GatewayMiddleware
{
    private readonly IHttpForwarder _forwarder;
    private readonly Dictionary<string, List<PathRoute>> _pathRoutes;
    private readonly Dictionary<string, (string Destination, HttpMessageInvoker Client)> _routes;

    public GatewayMiddleware(
        RequestDelegate next,
        IHttpForwarder forwarder,
        HttpsGatewayOptions options)
    {
        _forwarder = forwarder;

        _routes =
            new Dictionary<string, (string Destination, HttpMessageInvoker Client)>(StringComparer.OrdinalIgnoreCase);
        foreach ((string subdomain, string destination) in options.Routes)
        {
            string host = $"{subdomain}.{options.Domain}";
            _routes[host] = (destination, CreateClient());
        }

        _pathRoutes = new Dictionary<string, List<PathRoute>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string subdomain, Dictionary<string, string> routes) in options.PathRoutes)
        {
            string host = $"{subdomain}.{options.Domain}";
            _pathRoutes[host] = routes
                .Select(route => new PathRoute(
                    NormalizePathPrefix(route.Key),
                    route.Value,
                    CreateClient()))
                .OrderByDescending(route => route.PathPrefix.Value?.Length ?? 0)
                .ToList();
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        string host = context.Request.Host.Host;

        Console.WriteLine($"[Gateway] Request: {context.Request.Method} {host}{context.Request.Path}");

        if (TryGetPathRoute(host, context.Request.Path, out PathRoute pathRoute, out PathString remainingPath))
        {
            PathString forwardPath = NormalizeForwardPath(remainingPath);
            Console.WriteLine(
                $"[Gateway] Matched path route {host}{pathRoute.PathPrefix} -> {pathRoute.Destination}{forwardPath}");
            await ForwardAsync(context, pathRoute.Destination, pathRoute.Client, forwardPath);
            return;
        }

        if (_routes.TryGetValue(host, out (string Destination, HttpMessageInvoker Client) route))
        {
            Console.WriteLine($"[Gateway] Matched route {host} -> {route.Destination}");
            await ForwardAsync(context, route.Destination, route.Client);
            return;
        }

        Console.WriteLine($"[Gateway] No route match for {host}");
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        await context.Response.WriteAsync($"No gateway route for host '{host}'.");
    }

    private bool TryGetPathRoute(
        string host,
        PathString requestPath,
        out PathRoute route,
        out PathString remainingPath)
    {
        if (_pathRoutes.TryGetValue(host, out List<PathRoute>? hostPathRoutes))
        {
            foreach (PathRoute pathRoute in hostPathRoutes)
            {
                if (requestPath.StartsWithSegments(pathRoute.PathPrefix, out PathString candidateRemainingPath))
                {
                    route = pathRoute;
                    remainingPath = candidateRemainingPath;
                    return true;
                }
            }
        }

        route = default!;
        remainingPath = default;
        return false;
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
                Console.WriteLine($"[Gateway] Forward error: {error}");

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

    private sealed record PathRoute(
        PathString PathPrefix,
        string Destination,
        HttpMessageInvoker Client);
}
