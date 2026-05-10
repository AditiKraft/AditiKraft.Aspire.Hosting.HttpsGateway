using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using AditiKraft.Aspire.Hosting.HttpsGateway;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.ReverseProxy;

internal sealed class GatewayDashboardUrlLoggerService(
    HttpsGatewayOptions options,
    IServiceProvider services,
    IHostApplicationLifetime applicationLifetime)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await using CancellationTokenRegistration _ = stoppingToken.Register(() => started.TrySetCanceled(stoppingToken));
        await using CancellationTokenRegistration __ =
            applicationLifetime.ApplicationStarted.Register(() => started.TrySetResult());

        await started.Task;
        await WaitForDashboardEndpointAsync(stoppingToken);
        await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);

        string dashboardUrl = options.PublicUrl(options.DashboardSubdomain);
        string? dashboardToken = GetDashboardToken();

        if (!string.IsNullOrWhiteSpace(dashboardToken))
        {
            dashboardUrl = $"{dashboardUrl}/login?t={dashboardToken}";
        }

        Console.WriteLine($"[Gateway] Dashboard also available via gateway at {dashboardUrl}");
    }

    private string? GetDashboardToken()
    {
        var optionsType = Type.GetType("Aspire.Hosting.Dashboard.DashboardOptions, Aspire.Hosting");
        if (optionsType is null)
        {
            return null;
        }

        Type serviceType = typeof(IOptions<>).MakeGenericType(optionsType);
        object? options = services.GetService(serviceType);
        object? value = serviceType.GetProperty("Value")?.GetValue(options);
        return optionsType.GetProperty("DashboardToken")?.GetValue(value) as string;
    }

    private async Task WaitForDashboardEndpointAsync(CancellationToken stoppingToken)
    {
        if (!options.Routes.TryGetValue(options.DashboardSubdomain, out string? dashboardDestination))
        {
            return;
        }

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
                using var request = new HttpRequestMessage(HttpMethod.Head, dashboardDestination);
                using HttpResponseMessage _ = await client.SendAsync(request, stoppingToken);
                return;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
            }
            catch (TaskCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), stoppingToken);
            }
        }
    }
}
