using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Primitives;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.ReverseProxy;

internal sealed class GatewayDashboardRouteService(
    HttpsGatewayOptions options,
    IConfiguration configuration,
    IHostApplicationLifetime applicationLifetime)
    : IDistributedApplicationEventingSubscriber
{
    private const string DashboardResourceName = "aspire-dashboard";
    private const string DashboardLoginPath = "/login";
    private string? _automaticDashboardDestination;

    public Task SubscribeAsync(
        IDistributedApplicationEventing eventing,
        DistributedApplicationExecutionContext executionContext,
        CancellationToken cancellationToken)
    {
        if (!executionContext.IsRunMode)
        {
            return Task.CompletedTask;
        }

        if (GatewayMiddleware.TryGetHostRoute(options, options.DashboardHost, out string destination))
        {
            // Aspire initializes subscriptions before starting hosted services.
            applicationLifetime.ApplicationStarted.Register(() =>
                _ = LogDashboardUrlWhenAvailableAsync(destination, includeBrowserToken: false));
        }
        else if (options.ExposeDashboard)
        {
            eventing.Subscribe<ResourceEndpointsAllocatedEvent>(OnDashboardEndpointsAllocated);
        }

        return Task.CompletedTask;
    }

    public bool ShouldRejectDashboardLogin(HttpRequest request, string destination)
    {
        if (!string.Equals(request.Path.Value, DashboardLoginPath, StringComparison.OrdinalIgnoreCase)
            || !request.Query.TryGetValue("t", out StringValues tokens))
        {
            return false;
        }

        string? browserToken = configuration["AppHost:BrowserToken"];
        if (string.IsNullOrWhiteSpace(browserToken) || !tokens.Contains(browserToken, StringComparer.Ordinal))
        {
            return false;
        }

        lock (options)
        {
            return !string.Equals(_automaticDashboardDestination, destination, StringComparison.Ordinal);
        }
    }

    private Task OnDashboardEndpointsAllocated(ResourceEndpointsAllocatedEvent @event, CancellationToken cancellationToken)
    {
        if (@event.Resource is not IResourceWithEndpoints dashboard
            || !string.Equals(dashboard.Name, DashboardResourceName, StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        EndpointReference endpoint = dashboard.GetEndpoint("https");
        if (!endpoint.IsAllocated)
        {
            endpoint = dashboard.GetEndpoint("http");
        }

        if (!endpoint.IsAllocated)
        {
            return Task.CompletedTask;
        }

        // Isolated runs can allocate a different port from the launch profile.
        var address = new Uri(endpoint.Url);
        string destination = address.Host is "0.0.0.0" or "[::]"
            ? new UriBuilder(address) { Host = "localhost" }.Uri.AbsoluteUri
            : address.AbsoluteUri;

        lock (options)
        {
            if (!options.Routes.TryAdd(options.DashboardSubdomain, destination))
            {
                return Task.CompletedTask;
            }

            _automaticDashboardDestination = destination;
        }

        _ = LogDashboardUrlWhenAvailableAsync(destination, includeBrowserToken: true);
        return Task.CompletedTask;
    }

    private async Task LogDashboardUrlWhenAvailableAsync(string destination, bool includeBrowserToken)
    {
        CancellationToken stoppingToken = applicationLifetime.ApplicationStopping;

        try
        {
            await WaitForDashboardEndpointAsync(destination, stoppingToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken).ConfigureAwait(false);

            string dashboardUrl = options.PublicUrl(options.DashboardSubdomain);
            string? dashboardToken = includeBrowserToken
                ? GetDashboardToken(new Uri(dashboardUrl).Host, destination)
                : null;

            if (!string.IsNullOrWhiteSpace(dashboardToken))
            {
                dashboardUrl = $"{dashboardUrl}{DashboardLoginPath}?t={Uri.EscapeDataString(dashboardToken)}";
            }

            Console.WriteLine($"[Gateway] Dashboard also available via gateway at {dashboardUrl}");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gateway] Failed to log dashboard gateway URL: {ex.Message}");
        }
    }

    private string? GetDashboardToken(string host, string destination)
    {
        bool suppressLoginUrl = bool.TryParse(
            configuration["AppHost:SuppressDashboardLoginUrlInStartupSummary"], out bool suppress) && suppress;

        // A path route can intercept sign-in even when the host route points to the Aspire dashboard.
        if (suppressLoginUrl
            || GatewayMiddleware.TryGetPathRoute(options, host, DashboardLoginPath, out _, out _)
            || !GatewayMiddleware.TryGetHostRoute(options, host, out string currentDestination)
            || !string.Equals(currentDestination, destination, StringComparison.Ordinal))
        {
            return null;
        }

        return configuration["AppHost:BrowserToken"];
    }

    private static async Task WaitForDashboardEndpointAsync(string destination, CancellationToken stoppingToken)
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Head, destination);
                using HttpResponseMessage _ = await client.SendAsync(request, stoppingToken).ConfigureAwait(false);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
