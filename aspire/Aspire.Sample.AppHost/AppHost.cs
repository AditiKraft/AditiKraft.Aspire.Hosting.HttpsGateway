using AditiKraft.Aspire.Hosting.HttpsGateway;
using Aspire.Hosting.Dotnet;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

// Binds the entire "Gateway" config section (appsettings + user secrets) onto the options.
// Pass a lambda for code-only overrides, e.g. options => options.PathRoutes = …
HttpsGatewayOptions gateway = builder.AddHttpsGateway(builder.Configuration.GetSection("Gateway"));

// Subdomain defaults to the resource name; the route target is derived from the resource's
// own endpoint, so there is no manual port mapping. Override with WithHttpsGatewayUrl(gateway, "backend").
IResourceBuilder<DotnetProjectResource> apiService = builder
    .AddDotnetProject("apiservice", "../../sample/Sample.ApiService/Sample.ApiService.csproj")
    .WithHttpsGatewayUrl(gateway)
    .WithHttpHealthCheck("/health");

builder.AddDotnetProject("webfrontend", "../../sample/Sample.Web/Sample.Web.csproj")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithHttpsGatewayUrl(gateway)
    .WithHttpsGatewayReference(apiService, gateway)
    .WaitFor(apiService);

builder.Build().Run();
