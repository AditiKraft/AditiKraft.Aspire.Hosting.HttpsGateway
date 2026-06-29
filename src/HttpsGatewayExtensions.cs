using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using AditiKraft.Aspire.Hosting.HttpsGateway.Certificates;
using AditiKraft.Aspire.Hosting.HttpsGateway.ReverseProxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AditiKraft.Aspire.Hosting.HttpsGateway;

public static class HttpsGatewayExtensions
{
    private static readonly TimeSpan _appHostShutdownTimeout = TimeSpan.FromSeconds(5);
    private const string ServiceDiscoveryHttpsScheme = "https";
    private const string DefaultGatewayEndpointName = "https";

    /// <summary>
    /// Registers the HTTPS gateway, binding <paramref name="configuration"/> onto the options as
    /// defaults (e.g. the <c>"Gateway"</c> section). Use <paramref name="configure"/> for code-only
    /// overrides such as <see cref="HttpsGatewayOptions.PathRoutes"/>.
    /// </summary>
    public static HttpsGatewayOptions AddHttpsGateway(
        this IDistributedApplicationBuilder builder,
        IConfiguration configuration,
        Action<HttpsGatewayOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return builder.AddHttpsGateway(options =>
        {
            // Bind the configuration section first as defaults, then let the caller override
            // in code. Nested objects (RemoteCertificateStore) and dictionaries (Routes,
            // PathRoutes) bind automatically.
            configuration.Bind(options);
            configure?.Invoke(options);
        });
    }

    /// <summary>
    /// Registers the HTTPS gateway configured entirely in code.
    /// </summary>
    public static HttpsGatewayOptions AddHttpsGateway(
        this IDistributedApplicationBuilder builder,
        Action<HttpsGatewayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

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

        // Certificate provisioning happens lazily in GatewayServerHostedService as it starts,
        // so AddHttpsGateway stays synchronous (no eager network I/O at registration time).
        WireDashboardRoute(builder, options);

        return options;
    }

    /// <summary>
    /// Routes a resource through the gateway. The public hostname defaults to the resource name
    /// (override via <paramref name="subdomain"/>), and the route target is auto-derived from the
    /// resource's own endpoint once Aspire allocates it — no manual port mapping required.
    /// </summary>
    public static IResourceBuilder<T> WithHttpsGatewayUrl<T>(
        this IResourceBuilder<T> builder,
        HttpsGatewayOptions options,
        string? subdomain = null,
        string endpointName = DefaultGatewayEndpointName,
        string path = "")
        where T : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        string resolvedSubdomain = string.IsNullOrWhiteSpace(subdomain) ? builder.Resource.Name : subdomain;
        string publicUrl = options.PublicUrl(resolvedSubdomain, path);

        builder.WithUrls(context =>
        {
            context.Urls.Clear();
            context.Urls.Add(new ResourceUrlAnnotation { Url = publicUrl, DisplayText = publicUrl });
        });

        // Resolve the resource's real endpoint after Aspire allocates it, and register it as the
        // gateway route target. Reading happens at request time in GatewayMiddleware, so populating
        // the route here (after the gateway has already started) is fine.
        EndpointReference endpoint = builder.GetEndpoint(endpointName);
        builder.ApplicationBuilder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(
            builder.Resource,
            (_, _) =>
            {
                if (endpoint.IsAllocated)
                {
                    options.Routes[resolvedSubdomain] = endpoint.Url;
                }
                else
                {
                    Console.WriteLine(
                        $"[Gateway] Resource '{builder.Resource.Name}' has no allocated '{endpointName}' endpoint; " +
                        $"no route registered for {resolvedSubdomain}.{options.Domain}.");
                }

                return Task.CompletedTask;
            });

        return builder;
    }

    /// <summary>
    /// Injects <paramref name="source"/>'s public gateway URL into <paramref name="builder"/> as a
    /// service-discovery entry. The subdomain and service name both default to the source resource
    /// name; override either as needed.
    /// </summary>
    public static IResourceBuilder<TDestination> WithHttpsGatewayReference<TDestination, TSource>(
        this IResourceBuilder<TDestination> builder,
        IResourceBuilder<TSource> source,
        HttpsGatewayOptions options,
        string? subdomain = null,
        string path = "",
        string? serviceName = null)
        where TDestination : IResourceWithEnvironment
        where TSource : IResource
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        string resolvedServiceName = string.IsNullOrWhiteSpace(serviceName)
            ? source.Resource.Name
            : serviceName;
        string resolvedSubdomain = string.IsNullOrWhiteSpace(subdomain)
            ? source.Resource.Name
            : subdomain;
        string publicUrl = options.PublicUrl(resolvedSubdomain, path);

        return builder
            .WithEnvironment($"services__{resolvedServiceName}__{ServiceDiscoveryHttpsScheme}__0", publicUrl)
            .WithReferenceRelationship(source.Resource);
    }

    /// <summary>
    /// Mounts <paramref name="backend"/> under <paramref name="pathPrefix"/> on the host resource's
    /// subdomain, so browser calls to <c>https://{subdomain}{pathPrefix}/*</c> are forwarded to the
    /// backend (same-origin, no CORS). The path prefix is stripped before forwarding. The target is
    /// derived from the backend's own endpoint — no manual URL. Subdomain defaults to the host
    /// resource name.
    /// </summary>
    public static IResourceBuilder<THost> WithHttpsGatewayPath<THost, TBackend>(
        this IResourceBuilder<THost> builder,
        HttpsGatewayOptions options,
        string pathPrefix,
        IResourceBuilder<TBackend> backend,
        string? subdomain = null,
        string endpointName = DefaultGatewayEndpointName)
        where THost : IResource
        where TBackend : IResourceWithEndpoints
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(backend);
        ArgumentException.ThrowIfNullOrWhiteSpace(pathPrefix);

        string resolvedSubdomain = string.IsNullOrWhiteSpace(subdomain) ? builder.Resource.Name : subdomain;

        EndpointReference endpoint = backend.GetEndpoint(endpointName);
        builder.ApplicationBuilder.Eventing.Subscribe<ResourceEndpointsAllocatedEvent>(
            backend.Resource,
            (_, _) =>
            {
                if (!endpoint.IsAllocated)
                {
                    Console.WriteLine(
                        $"[Gateway] Backend '{backend.Resource.Name}' has no allocated '{endpointName}' endpoint; " +
                        $"no path route registered for {resolvedSubdomain}.{options.Domain}{pathPrefix}.");
                    return Task.CompletedTask;
                }

                // Rebuild the inner map as a fresh dictionary so the request-time reader in
                // GatewayMiddleware never enumerates a map that is being mutated.
                lock (options)
                {
                    Dictionary<string, string> map =
                        options.PathRoutes.TryGetValue(resolvedSubdomain, out Dictionary<string, string>? existing)
                            ? new Dictionary<string, string>(existing)
                            : new Dictionary<string, string>();

                    map[pathPrefix] = endpoint.Url;
                    options.PathRoutes[resolvedSubdomain] = map;
                }

                return Task.CompletedTask;
            });

        return builder;
    }

    private static void WireDashboardRoute(IDistributedApplicationBuilder builder, HttpsGatewayOptions options)
    {
        if (!options.ExposeDashboard)
        {
            return;
        }

        // Don't clobber an explicitly configured dashboard route.
        if (options.Routes.ContainsKey(options.DashboardSubdomain))
        {
            return;
        }

        string? dashboardUrl = ResolveDashboardUrl(builder.Configuration);
        if (!string.IsNullOrWhiteSpace(dashboardUrl))
        {
            options.Routes[options.DashboardSubdomain] = dashboardUrl;
        }
    }

    private static string? ResolveDashboardUrl(IConfiguration configuration)
    {
        // The AppHost's own URL is the Aspire dashboard frontend; prefer the HTTPS binding.
        string? urls = configuration["ASPNETCORE_URLS"];
        if (string.IsNullOrWhiteSpace(urls))
        {
            return null;
        }

        string[] candidates = urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Array.Find(candidates, url => url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ?? (candidates.Length > 0 ? candidates[0] : null);
    }
}
