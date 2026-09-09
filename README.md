# AditiKraft.Aspire.Hosting.HttpsGateway

Local HTTPS gateway for .NET Aspire AppHost with YARP reverse proxy, automated ACME DNS-01 wildcard certificates via Let's Encrypt + Cloudflare, and optional S3-compatible certificate caching.

[![NuGet](https://img.shields.io/nuget/v/AditiKraft.Aspire.Hosting.HttpsGateway.svg)](https://www.nuget.org/packages/AditiKraft.Aspire.Hosting.HttpsGateway)

HttpsGateway gives every Aspire resource a real `https://<name>.yourdomain.com` URL on your machine — wildcard TLS, same-origin path mounting (no CORS), and the Aspire dashboard itself — without editing `hosts`, minting dev certs, or hand-mapping ports.

## Requirements

| Requirement | Version |
|---|---|
| .NET SDK | 10.x |
| Aspire | 13.5+ |

## Features

| Feature | Description |
|---|---|
| **HTTPS Gateway** | Kestrel-based reverse proxy with TLS termination on a single port |
| **Wildcard Certificates** | Automated ACME DNS-01 challenges for `*.yourdomain.com` |
| **Cloudflare DNS** | Built-in TXT record creation/cleanup for DNS-01 validation |
| **Auto Routes** | Route targets derived from each resource's own endpoint — no manual port mapping |
| **Path Routes** | Mount backends under the same origin to eliminate CORS |
| **Dashboard Exposure** | The Aspire dashboard is auto-published through the gateway |
| **Certificate Caching** | Local disk cache with optional S3/R2 remote persistence |
| **Auto-Renewal** | Background certificate renewal before expiry |

## Quick Start

Install the package in your AppHost project:

```bash
dotnet add package AditiKraft.Aspire.Hosting.HttpsGateway
```

Add a `Gateway` section to configuration. Non-secret values go in `appsettings.json`;
the Cloudflare token (and any S3 credentials) belong in **user-secrets**:

```json
// appsettings.json
{
  "Gateway": {
    "Domain": "local.example.com",
    "Email": "admin@example.com",
    "UseStaging": true,
    "DashboardSubdomain": "aspire",
    "ExposeDashboard": true,
    "EnableVerboseProxyLogging": false
  }
}
```

```json
// user-secrets (dotnet user-secrets set "Gateway:CloudflareApiToken" "...")
{
  "Gateway": {
    "CloudflareApiToken": "cf-token-with-dns-edit"
  }
}
```

Wire the gateway into `AppHost.cs`:

```csharp
using AditiKraft.Aspire.Hosting.HttpsGateway;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Binds the whole "Gateway" section (appsettings + user-secrets) onto the options.
HttpsGatewayOptions gateway = builder.AddHttpsGateway(builder.Configuration.GetSection("Gateway"));

// The subdomain defaults to the resource name and the route target is taken from the
// resource's own endpoint — so there is no Routes dictionary and no manual port mapping.
var apiService = builder.AddProject<Projects.ApiService>("apiservice")   // https://apiservice.local.example.com
    .WithHttpsGatewayUrl(gateway)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Web>("webfrontend")                          // https://webfrontend.local.example.com
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithHttpsGatewayUrl(gateway)
    // Injects apiservice's gateway URL into the web app's service discovery so it calls the
    // API at its public HTTPS origin (same-site, no CORS) instead of raw localhost.
    .WithHttpsGatewayReference(apiService, gateway)
    .WaitFor(apiService);

builder.Build().Run();
```

That is enough for the normal case. The Aspire dashboard is also published — open `https://aspire.local.example.com`.

## What Happens

1. **Registration** — `AddHttpsGateway` binds config, registers the gateway services, and auto-wires the dashboard route from the AppHost's own URL.
2. **Endpoint resolution** — as each resource's endpoints are allocated, `WithHttpsGatewayUrl` records that resource's real address as the route target. No ports are hardcoded.
3. **Gateway start** — the gateway hosted service ensures a valid certificate exists (local cache → remote cache → ACME order), then starts Kestrel on the gateway port.
4. **Requests** — `https://<subdomain>.<domain>` is matched live against the resolved routes and forwarded to the backend.
5. **Renewal** — a background service renews the certificate before it expires.

## Subdomains

The subdomain defaults to the **resource name**. Override it per call when you want a different public name:

```csharp
builder.AddProject<Projects.ApiService>("apiservice")
    .WithHttpsGatewayUrl(gateway, "backend");   // https://backend.local.example.com
```

`WithHttpsGatewayReference` defaults both the subdomain and the service-discovery name to the source resource name; override either as needed.

## Routes

You normally do **not** maintain a `Routes` dictionary — `WithHttpsGatewayUrl` derives the target from the Aspire resource. `Routes` remains as an **escape hatch** for targets that are *not* Aspire resources (a service on another machine, something you run by hand). It binds straight from config:

```json
{
  "Gateway": {
    "Routes": {
      "legacy": "https://192.168.1.50:8443"
    }
  }
}
```

Each key becomes `{key}.{Domain}` (e.g. `legacy.local.example.com`).

### Path Routes (single origin, no CORS)

Mount a backend under another resource's path so the browser sees one origin. Use
`WithHttpsGatewayPath` to serve the UI at the host root and the API under `/api` on the
**same** host — the API target is derived from its endpoint, so there is no literal URL:

```csharp
using AditiKraft.Aspire.Hosting.HttpsGateway;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

HttpsGatewayOptions gateway = builder.AddHttpsGateway(builder.Configuration.GetSection("Gateway"));

// Backend API — reached only under the UI's /api path (no subdomain of its own).
var api = builder.AddProject<Projects.ApiService>("apiservice")
    .WithHttpHealthCheck("/health");

// UI at the host root; API mounted under /api on the same origin.
builder.AddProject<Projects.Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithHttpsGatewayUrl(gateway)                  // https://webfrontend.local.example.com        → UI ("/")
    .WithHttpsGatewayPath(gateway, "/api", api)    // https://webfrontend.local.example.com/api/*   → API
    .WaitFor(api);

builder.Build().Run();
```

| Request | Goes to |
|---|---|
| `https://webfrontend.local.example.com/` | UI |
| `https://webfrontend.local.example.com/api/weatherforecast` | API (forwarded as `/weatherforecast`) |

Path routes are matched **before** host routes (longest prefix first), and the prefix is
**stripped** before forwarding — so the API receives `/weatherforecast`, not `/api/weatherforecast`,
and its controllers stay at their normal paths. For the single origin to matter, the UI's
browser code should call the relative path (`/api/...`).

> [!TIP]
> To also reach the API on its own subdomain, add `.WithHttpsGatewayUrl(gateway)` to the
> `api` resource — you then get both `https://apiservice.local.example.com` and the `/api` mount.

#### Static path routes

For a target that is **not** an Aspire resource, set `PathRoutes` directly (keyed by subdomain,
then path prefix). It binds from config or the `configure` lambda:

```json
{
  "Gateway": {
    "PathRoutes": {
      "webfrontend": { "/legacy": "https://192.168.1.50:8443" }
    }
  }
}
```

## Dashboard

The Aspire dashboard is exposed through the gateway automatically. Its route is wired from the AppHost's own URL, so there is nothing to map. Control it with two options:

- `DashboardSubdomain` (default `"aspire"`) — the public subdomain.
- `ExposeDashboard` (default `true`) — set `false` to keep the dashboard off the gateway.

## Certificate Lifecycle

1. **Ensure on start** — the gateway hosted service ensures a valid certificate before Kestrel binds.
2. **Local cache** — certificates are stored in `%LocalAppData%\HttpsGateway\certs\{domain}\`.
3. **Remote cache** — if enabled, a cached certificate is downloaded from S3/R2 before requesting a new one.
4. **ACME order** — full DNS-01 workflow: create TXT → wait for propagation → validate → clean up TXT.
5. **Auto-renewal** — a background service renews when fewer than 30 days remain.
6. **Upload** — new certificates are uploaded to the remote cache for other machines/devs.

> [!NOTE]
> Certificate provisioning runs as the gateway **starts**, not during registration, so
> `AddHttpsGateway` is synchronous and returns immediately. The first run for a new domain
> can take a minute or two while the DNS-01 challenge propagates.

## Configuring Options

There are two ways to configure the gateway. Both are fully supported, so pick whichever you prefer.

### Bind from configuration (recommended)

Pass the `Gateway` config section. Everything — including the nested
`RemoteCertificateStore` and the `Routes`/`PathRoutes` dictionaries — is bound for you.
Use the optional `configure` lambda to override or add values in code:

```csharp
HttpsGatewayOptions gateway = builder.AddHttpsGateway(builder.Configuration.GetSection("Gateway"));
```

### Configure everything in code

If you would rather wire each value yourself, use the `configure`-only overload:

```csharp
HttpsGatewayOptions gateway = builder.AddHttpsGateway(options =>
{
    options.Domain = "local.example.com";
    options.Email = "admin@example.com";
    options.CloudflareApiToken = builder.Configuration["Gateway:CloudflareApiToken"]!;
    options.UseStaging = true;
});
```

### Mix both

With the config-binding overload, `configure` runs *after* the bind, so you can bind
from config and still override individual values in code:

```csharp
HttpsGatewayOptions gateway = builder.AddHttpsGateway(
    builder.Configuration.GetSection("Gateway"),
    options =>
    {
        options.UseStaging = false;          // override a bound value
        options.PathRoutes = new() { /* … */ };
    });
```

### Remote certificate caching (S3 / R2)

Enable a shared certificate cache so teammates and CI reuse the same wildcard cert
instead of each issuing their own. Bound from the `Gateway:RemoteCertificateStore`
section (keep the credentials in user-secrets):

```json
{
  "Gateway": {
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

## Options Reference

### `HttpsGatewayOptions`

| Property | Type | Default | Description |
|---|---|---|---|
| `Domain` | `string` | `"local.iambip.in"` | Base domain for all gateway URLs |
| `Email` | `string` | `""` | ACME account email for Let's Encrypt |
| `CloudflareApiToken` | `string` | `""` | Cloudflare API token with DNS edit permissions |
| `UseStaging` | `bool` | `true` | Use Let's Encrypt staging (set `false` for production) |
| `Port` | `int` | `443` | Gateway listen port |
| `DashboardSubdomain` | `string` | `"aspire"` | Subdomain for the Aspire dashboard |
| `ExposeDashboard` | `bool` | `true` | Publish the Aspire dashboard through the gateway |
| `EnableVerboseProxyLogging` | `bool` | `false` | Enable detailed YARP proxy forwarding logs |
| `CertStoreDirectory` | `string` | `%LocalAppData%\HttpsGateway\certs` | Local certificate storage path |
| `CertPassword` | `string` | `"dev-gateway-cert"` | PFX export password (if needed) |
| `Routes` | `Dictionary<string, string>` | `new()` | Escape-hatch host routes: `subdomain` → `backend URL` |
| `PathRoutes` | `Dictionary<string, Dictionary<string, string>>` | `new()` | Path routes: `subdomain` → `{path}` → `backend URL` |
| `RemoteCertificateStore` | `RemoteCertificateStoreOptions` | — | S3-compatible certificate backup settings |

**Computed:** `WildcardDomain` → `"*.Domain"`, `DashboardHost` → `"DashboardSubdomain.Domain"`, `CertFullPath()`, `PublicUrl(subdomain, path)`.

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

Object key resolution: `{prefix}/{domain}/{certFileName}` — e.g. `certificates/local.example.com/gateway-staging.pem`.

## API

- `AddHttpsGateway(configurationSection, configure)` — binds the config section onto the options, then applies the code-only `configure` overrides. Returns `HttpsGatewayOptions`.
- `AddHttpsGateway(configure)` — configure every option in code.
- `WithHttpsGatewayUrl(gateway, subdomain = null, endpointName = "https", path = "")` — publishes the resource through the gateway. Subdomain defaults to the resource name; the route target is derived from the named endpoint.
- `WithHttpsGatewayReference(source, gateway, subdomain = null, path = "", serviceName = null)` — injects the source resource's gateway URL into the destination as a service-discovery entry. Subdomain and service name default to the source resource name.
- `WithHttpsGatewayPath(gateway, pathPrefix, backend, subdomain = null, endpointName = "https")` — mounts `backend` under `pathPrefix` on the host resource's subdomain (same-origin, no CORS). The prefix is stripped before forwarding and the target is derived from the backend's endpoint. Subdomain defaults to the host resource name.

## Security Notes

- Do not commit `Gateway:CloudflareApiToken` or S3 credentials — keep them in user-secrets.
- Use a Cloudflare token scoped to just the zone's DNS edit permission.
- Prefer `UseStaging = true` while iterating to avoid Let's Encrypt rate limits; flip to `false` for a trusted certificate.

## License

This project is licensed under the [MIT License](LICENSE).
