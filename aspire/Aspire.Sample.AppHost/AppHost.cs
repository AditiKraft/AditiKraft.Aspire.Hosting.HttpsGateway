using AditiKraft.Aspire.Hosting.HttpsGateway;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

// Binds the entire "Gateway" config section (appsettings + user secrets) onto the options.
// Pass a lambda for code-only overrides, e.g. options => options.PathRoutes = …
HttpsGatewayOptions gateway = builder.AddHttpsGateway(builder.Configuration.GetSection("Gateway"));

// Subdomain defaults to the resource name; the route target is derived from the resource's
// own endpoint, so there is no manual port mapping. Override with WithHttpsGatewayUrl(gateway, "backend").
var apiService = builder.AddProject<Projects.Sample_ApiService>("apiservice")
    .WithHttpsGatewayUrl(gateway)
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Sample_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithHttpsGatewayUrl(gateway)
    .WithHttpsGatewayReference(apiService, gateway)
    .WaitFor(apiService);

builder.Build().Run();
