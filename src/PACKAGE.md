# AditiKraft.Aspire.Hosting.HttpsGateway

Local Aspire AppHost HTTPS gateway with YARP host/path routing, ACME DNS-01 certificate management, and optional S3-compatible certificate caching.

## Features

- **YARP Reverse Proxy** — Host and path-based routing for local development
- **ACME DNS-01 Challenges** — Automated certificate provisioning via Let's Encrypt
- **DNS Provider Support** — Cloudflare DNS integration out of the box
- **S3-Compatible Caching** — Persist certificates to S3/R2 storage for reuse across restarts
- **Zero-config HTTPS** — Drop-in HTTPS for your Aspire AppHost projects

## Quick Start

1. Install the package in your AppHost project:

```bash
dotnet add package AditiKraft.Aspire.Hosting.HttpsGateway
```

2. Register the gateway in your `Program.cs`:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

builder.AddHttpsGateway();

builder.Build().Run();
```

## Configuration

Configure via `appsettings.json` or environment variables:

```json
{
  "HttpsGateway": {
    "Acme": {
      "Email": "admin@example.com",
      "DnsProvider": "Cloudflare",
      "CloudflareApiToken": "your-token"
    },
    "CertificateCache": {
      "S3Bucket": "my-certs",
      "S3Region": "us-east-1"
    }
  }
}
```

## License

This project is licensed under the [MIT License](LICENSE).
