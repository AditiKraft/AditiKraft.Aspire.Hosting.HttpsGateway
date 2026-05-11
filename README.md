# AditiKraft.Aspire.Hosting.HttpsGateway

Local HTTPS gateway for .NET Aspire AppHost with YARP reverse proxy, automated ACME DNS-01 wildcard certificates via Let's Encrypt + Cloudflare, and optional S3-compatible certificate caching.

## Features

| Feature | Description |
|---|---|
| **HTTPS Gateway** | Kestrel-based reverse proxy with TLS termination on a single port |
| **Wildcard Certificates** | Automated ACME DNS-01 challenges for `*.yourdomain.com` |
| **Cloudflare DNS** | Built-in TXT record creation/cleanup for DNS-01 validation |
| **Host Routes** | Map subdomains to backend services (e.g. `api.local.dev` → `https://localhost:5001`) |
| **Path Routes** | Mount backends under the same origin to eliminate CORS |
| **Certificate Caching** | Local disk cache with optional S3/R2 remote persistence |
| **Auto-Renewal** | Background certificate renewal before expiry |
| **Aspire Integration** | URL annotations, service discovery references, health check helpers |

## Installation

```bash
dotnet add package AditiKraft.Aspire.Hosting.HttpsGateway
```

## Quick Start

Two extension methods do most of the work:

- **`.WithHttpsGatewayUrl(...)`** — Publishes a service through the gateway so it gets a public HTTPS URL (e.g. `https://api.local.example.com`). The Aspire dashboard will show this URL instead of the internal localhost address.
- **`.WithHttpsGatewayReference(...)`** — Injects one service's gateway URL into another service's environment variables, so services can talk to each other through the gateway instead of over raw localhost.

```csharp
using AditiKraft.Aspire.Hosting.HttpsGateway;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

HttpsGatewayOptions gatewayOptions = await builder.AddHttpsGatewayAsync(options =>
{
    options.Domain = "local.example.com";
    options.Email = "admin@example.com";
    options.CloudflareApiToken = builder.Configuration["Gateway:CloudflareApiToken"]!;
    options.UseStaging = builder.Configuration.GetValue<bool>("Gateway:UseStaging");
    options.Port = 443;
    options.EnableVerboseProxyLogging = builder.Configuration.GetValue<bool>("Gateway:EnableVerboseProxyLogging");

    // Subdomain → backend URL mappings
    options.Routes = new Dictionary<string, string>
    {
        ["aspire"] = "https://localhost:17026",
        ["backend"] = "https://localhost:7593",
        ["ui"] = "https://localhost:7013",
    };
});

var apiService = builder.AddProject<Projects.Sample_ApiService>("apiservice")
    // Exposes the API through the gateway at https://backend.local.example.com
    // Replaces the default localhost URL in the Aspire dashboard
    .WithHttpsGatewayUrl(gatewayOptions, "backend")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Sample_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    // Exposes the web frontend through the gateway at https://ui.local.example.com
    .WithHttpsGatewayUrl(gatewayOptions, "ui")
    // Injects the API gateway URL into the web frontend's environment variables.
    // The web app can now call the API via https://backend.local.example.com
    // instead of the internal localhost address.
    .WithHttpsGatewayReference(apiService, gatewayOptions, "backend")
    .WaitFor(apiService);

builder.Build().Run();
```

## API Reference

### `AddHttpsGatewayAsync`

```csharp
public static Task<HttpsGatewayOptions> AddHttpsGatewayAsync(
    this IDistributedApplicationBuilder builder,
    Action<HttpsGatewayOptions> configure)
```

Configures and starts the HTTPS gateway. Returns the configured `HttpsGatewayOptions` for use with resource extensions.

**Registers hosted services:**
- `CertificateRenewalService` — background renewal monitoring
- `GatewayServerHostedService` — Kestrel reverse proxy server
- `GatewayDashboardUrlLoggerService` — logs dashboard URLs on startup

### `HttpsGatewayOptions`

| Property | Type | Default | Description |
|---|---|---|---|
| `Domain` | `string` | `"local.iambip.in"` | Base domain for all gateway URLs |
| `Email` | `string` | `""` | ACME account email for Let's Encrypt |
| `CloudflareApiToken` | `string` | `""` | Cloudflare API token with DNS edit permissions |
| `UseStaging` | `bool` | `true` | Use Let's Encrypt staging (set `false` for production) |
| `Port` | `int` | `443` | Gateway listen port |
| `DashboardSubdomain` | `string` | `"aspire"` | Subdomain for the Aspire dashboard |
| `EnableVerboseProxyLogging` | `bool` | `false` | Enable detailed YARP proxy forwarding logs |
| `CertStoreDirectory` | `string` | `%LocalAppData%\HttpsGateway\certs` | Local certificate storage path |
| `CertPassword` | `string` | `"dev-gateway-cert"` | PFX export password (if needed) |
| `Routes` | `Dictionary<string, string>` | `new()` | Host routes: `subdomain` → `backend URL` |
| `PathRoutes` | `Dictionary<string, Dictionary<string, string>>` | `new()` | Path routes: `subdomain` → `{path}` → `backend URL` |
| `RemoteCertificateStore` | `RemoteCertificateStoreOptions` | — | S3-compatible certificate backup settings |

**Computed properties:**
- `WildcardDomain` → `"*.Domain"`
- `DashboardHost` → `"DashboardSubdomain.Domain"`
- `CertFullPath()` → resolves to local cert file path
- `PublicUrl(subdomain, path)` → generates `https://subdomain.domain/path`

### `RemoteCertificateStoreOptions`

| Property | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Enable remote certificate caching |
| `Endpoint` | `string` | `""` | S3-compatible endpoint URL |
| `BucketName` | `string` | `""` | Storage bucket name |
| `AccessKeyId` | `string` | `""` | S3 access key |
| `SecretAccessKey` | `string` | `""` | S3 secret key |
| `Region` | `string` | `"auto"` | S3 region |
| `ForcePathStyle` | `bool` | `true` | Use path-style URLs (required for MinIO/R2) |
| `ObjectKeyPrefix` | `string` | `"certificates"` | Key prefix in bucket |
| `ObjectKey` | `string` | `""` | Override full object key (optional) |

**Object key resolution:**
```
{prefix}/{domain}/{certFileName}
# Example: certificates/local.example.com/gateway-staging.pem
```

### `WithHttpsGatewayUrl`

```csharp
public static IResourceBuilder<T> WithHttpsGatewayUrl<T>(
    this IResourceBuilder<T> builder,
    HttpsGatewayOptions options,
    string subdomain,
    string path = "")
    where T : IResource
```

Replaces the resource's URLs with the gateway public URL. The resource will display `https://subdomain.domain` in the Aspire dashboard instead of its internal localhost address.

### `WithHttpsGatewayReference`

```csharp
public static IResourceBuilder<TDestination> WithHttpsGatewayReference<TDestination, TSource>(
    this IResourceBuilder<TDestination> builder,
    IResourceBuilder<TSource> source,
    HttpsGatewayOptions options,
    string subdomain,
    string path = "",
    string? serviceName = null)
    where TDestination : IResourceWithEnvironment
    where TSource : IResource
```

Injects a service discovery environment variable so the destination resource can reach the source via the gateway URL.

**Environment variable format:**
```
services__{serviceName}__https__0 = https://subdomain.domain/path
```

## Routing

### Host Routes

Map subdomains to backend services. Each key becomes `{key}.{Domain}`:

```csharp
options.Routes = new Dictionary<string, string>
{
    ["aspire"] = "https://localhost:17026",
    ["api"] = "https://localhost:5001",
    ["web"] = "https://localhost:5002",
};
```

Exposes:
- `aspire.local.example.com` → Aspire Dashboard
- `api.local.example.com` → API service
- `web.local.example.com` → Web frontend

### Path Routes

Mount backends under the same origin to avoid CORS:

```csharp
options.PathRoutes = new()
{
    ["web"] = new()
    {
        ["/api"] = "https://localhost:5001",
    },
};
```

Now `web.local.example.com/api/*` proxies to the API service. The browser sees a same-origin request.

## Certificate Lifecycle

1. **Startup check** — `AddHttpsGatewayAsync` ensures a valid certificate exists before Kestrel starts
2. **Local cache** — Certificates stored in `%LocalAppData%\HttpsGateway\certs\{domain}\`
3. **Remote cache** — If enabled, downloads from S3/R2 before requesting new
4. **ACME order** — Full DNS-01 workflow: create TXT → wait 30s → validate → cleanup TXT
5. **Auto-renewal** — Background service checks expiry and renews when < 30 days remaining
6. **Upload** — New certificates uploaded to remote cache for other machines/devs

## Configuration Examples

### Full S3/R2 Certificate Caching

```csharp
IConfigurationSection remoteCertificateStore = builder.Configuration.GetSection("Gateway:RemoteCertificateStore");
options.RemoteCertificateStore.Enabled = remoteCertificateStore.GetValue<bool>("Enabled");
options.RemoteCertificateStore.Endpoint = remoteCertificateStore["Endpoint"] ?? "";
options.RemoteCertificateStore.BucketName = remoteCertificateStore["BucketName"] ?? "";
options.RemoteCertificateStore.AccessKeyId = remoteCertificateStore["AccessKeyId"] ?? "";
options.RemoteCertificateStore.SecretAccessKey = remoteCertificateStore["SecretAccessKey"] ?? "";
options.RemoteCertificateStore.Region = remoteCertificateStore["Region"] ?? "auto";
options.RemoteCertificateStore.ForcePathStyle = remoteCertificateStore.GetValue("ForcePathStyle", true);
options.RemoteCertificateStore.ObjectKeyPrefix = remoteCertificateStore["ObjectKeyPrefix"] ?? "certificates";
options.RemoteCertificateStore.ObjectKey = remoteCertificateStore["ObjectKey"] ?? "";
```

### appsettings.json

```json
{
  "Gateway": {
    "CloudflareApiToken": "your-token",
    "UseStaging": true,
    "EnableVerboseProxyLogging": false,
    "RemoteCertificateStore": {
      "Enabled": true,
      "Endpoint": "https://<account>.r2.cloudflarestorage.com",
      "BucketName": "my-certs",
      "AccessKeyId": "<key>",
      "SecretAccessKey": "<secret>",
      "Region": "auto",
      "ForcePathStyle": true
    }
  }
}
```

## License

This project is licensed under the [MIT License](LICENSE).
