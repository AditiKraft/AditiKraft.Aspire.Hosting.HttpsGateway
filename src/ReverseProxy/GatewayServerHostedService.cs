using System.Security.Cryptography.X509Certificates;
using AditiKraft.Aspire.Hosting.HttpsGateway;
using AditiKraft.Aspire.Hosting.HttpsGateway.Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.ReverseProxy;

internal sealed class GatewayServerHostedService(HttpsGatewayOptions options, IHostEnvironment environment)
    : IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan _gatewayShutdownTimeout = TimeSpan.FromSeconds(2);

    private WebApplication? _app;
    private X509Certificate2? _serverCertificate;

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

        string routeHosts =
            string.Join(", ", options.Routes.Keys.Select(subdomain => $"{subdomain}.{options.Domain}"));
        Console.WriteLine($"[Gateway] Listening on https://localhost:{options.Port} for hosts: {routeHosts}");

        await _app.StartAsync(cancellationToken);
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
