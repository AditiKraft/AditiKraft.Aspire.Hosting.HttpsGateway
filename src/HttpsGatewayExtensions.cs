using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AditiKraft.Aspire.Hosting.HttpsGateway.Certificates;
using AditiKraft.Aspire.Hosting.HttpsGateway.ReverseProxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AditiKraft.Aspire.Hosting.HttpsGateway;

public static class HttpsGatewayExtensions
{
    private static readonly TimeSpan _appHostShutdownTimeout = TimeSpan.FromSeconds(5);
    private const string ServiceDiscoveryHttpsScheme = "https";

    public static async Task<HttpsGatewayOptions> AddHttpsGatewayAsync(
        this IDistributedApplicationBuilder builder,
        Action<HttpsGatewayOptions> configure)
    {
        var options = new HttpsGatewayOptions();
        configure(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<S3CertificateStore>();
        builder.Services.AddSingleton<CertificateManager>();
        builder.Services.AddHostedService<CertificateRenewalService>();
        builder.Services.AddHostedService<GatewayServerHostedService>();
        builder.Services.AddHostedService<GatewayDashboardUrlLoggerService>();
        builder.Services.Configure<HostOptions>(hostOptions =>
        {
            hostOptions.ShutdownTimeout = _appHostShutdownTimeout;
        });

        builder.Services.AddHttpClient("Cloudflare", client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
            client.DefaultRequestHeaders.Add(
                "Authorization", $"Bearer {options.CloudflareApiToken}");
        });

        builder.Services.AddHttpForwarder();

        // Ensure certificate exists before Kestrel starts
        CertificateManager certManager = CreateTemporaryCertManager(options);
        await certManager.EnsureCertificateAsync();

        return options;
    }

    private static CertificateManager CreateTemporaryCertManager(HttpsGatewayOptions options)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("Cloudflare", client =>
        {
            client.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
            client.DefaultRequestHeaders.Add(
                "Authorization", $"Bearer {options.CloudflareApiToken}");
        });
        ServiceProvider sp = services.BuildServiceProvider();

        return new CertificateManager(
            options,
            sp.GetRequiredService<IHttpClientFactory>(),
            new S3CertificateStore(options));
    }

    public static IResourceBuilder<T> WithHttpsGatewayUrl<T>(
        this IResourceBuilder<T> builder,
        HttpsGatewayOptions options,
        string subdomain,
        string path = "")
        where T : IResource
    {
        string publicUrl = options.PublicUrl(subdomain, path);

        builder.WithUrls(context =>
        {
            context.Urls.Clear();
            context.Urls.Add(new ResourceUrlAnnotation { Url = publicUrl, DisplayText = publicUrl });
        });

        return builder;
    }

    public static IResourceBuilder<TDestination> WithHttpsGatewayReference<TDestination, TSource>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TSource> source,
        HttpsGatewayOptions options,
        string subdomain,
        string path = "",
        string? serviceName = null)
        where TDestination : IResourceWithEnvironment
        where TSource : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(subdomain);

        string resolvedServiceName = string.IsNullOrWhiteSpace(serviceName)
            ? source.Resource.Name
            : serviceName;
        string publicUrl = options.PublicUrl(subdomain, path);

        return builder
            .WithEnvironment($"services__{resolvedServiceName}__{ServiceDiscoveryHttpsScheme}__0", publicUrl)
            .WithReferenceRelationship(source.Resource);
    }
}
