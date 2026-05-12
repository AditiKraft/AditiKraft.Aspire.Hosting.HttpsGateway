using System.Security.Cryptography.X509Certificates;
using AditiKraft.Aspire.Hosting.HttpsGateway;
using AditiKraft.Aspire.Hosting.HttpsGateway.Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.ReverseProxy;

internal sealed class GatewayServerHostedService(HttpsGatewayOptions options, IHostEnvironment environment)
    : IHostedLifecycleService, IAsyncDisposable
{
    private static readonly TimeSpan _gatewayShutdownTimeout = TimeSpan.FromSeconds(2);

    private WebApplication? _app;
    private X509Certificate2? _serverCertificate;
    private bool _started;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }

        _serverCertificate?.Dispose();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        await StartGatewayAsync(cancellationToken);
    }

    public Task StartingAsync(CancellationToken cancellationToken) =>
        StartGatewayAsync(cancellationToken);

    public Task StartedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) =>
        Task.CompletedTask;

    private async Task StartGatewayAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        string routeHosts =
            string.Join(", ", options.Routes.Keys.Select(subdomain => $"{subdomain}.{options.Domain}"));
        Console.WriteLine($"[Gateway] Starting on https://localhost:{options.Port} for hosts: {routeHosts}");

        try
        {
            string certPath = options.CertFullPath();
            _serverCertificate = LoadServerCertificate(certPath);

            WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(GatewayServerHostedService).Assembly.FullName,
                ContentRootPath = environment.ContentRootPath
            });

            builder.Services.AddSingleton(options);
            builder.Services.AddHttpForwarder();
            builder.Services.Configure<HostOptions>(options => { options.ShutdownTimeout = _gatewayShutdownTimeout; });
            builder.Logging.AddFilter(
                "Yarp.ReverseProxy.Forwarder.HttpForwarder",
                options.EnableVerboseProxyLogging ? LogLevel.Information : LogLevel.Error);

            builder.WebHost.ConfigureKestrel(kestrel =>
            {
                kestrel.ListenLocalhost(options.Port, listenOptions =>
                {
                    listenOptions.Protocols = HttpProtocols.Http1AndHttp2;
                    listenOptions.UseHttps(httpsOptions => { httpsOptions.ServerCertificate = _serverCertificate; });
                });
            });

            _app = builder.Build();
            _app.UseWebSockets();
            _app.UseMiddleware<GatewayMiddleware>();
            await _app.StartAsync(cancellationToken);
            _started = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Gateway] Failed to start on https://localhost:{options.Port}: {ex.Message}");
            throw;
        }

        Console.WriteLine($"[Gateway] Listening on https://localhost:{options.Port} for hosts: {routeHosts}");
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            using var shutdownCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            shutdownCts.CancelAfter(_gatewayShutdownTimeout);

            try
            {
                await _app.StopAsync(shutdownCts.Token);
            }
            catch (OperationCanceledException) when (shutdownCts.IsCancellationRequested)
            {
                Console.WriteLine("[Gateway] Shutdown timed out; forcing local dev shutdown.");
            }
        }
    }

    private static X509Certificate2 LoadServerCertificate(string certPath) =>
        GatewayCertificateLoader.LoadFromPem(certPath);
}
