using AditiKraft.Aspire.Hosting.HttpsGateway;
using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);



HttpsGatewayOptions gatewayOptions = await builder.AddHttpsGatewayAsync(options =>
{
    options.Domain = "local.iambip.in";
    options.Email = "iambipinpaul@outlook.com";
    options.CloudflareApiToken = builder.Configuration["Gateway:CloudflareApiToken"]!;
    options.UseStaging = builder.Configuration.GetValue<bool>("Gateway:UseStaging");
    options.Port = 443;
    options.EnableVerboseProxyLogging = builder.Configuration.GetValue<bool>("Gateway:EnableVerboseProxyLogging");

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

    options.Routes = new Dictionary<string, string>
    {
        ["aspire"] = "https://localhost:17026",
        ["backend"] = "https://localhost:7593",
        ["ui"] = "https://localhost:7013",
    };

    // Example: expose a backend under the same UI origin so browser calls
    // https://teachkraft.local.iambip.in/api/* without CORS.
    // options.PathRoutes = new()
    // {
    //     ["HttpsGateway.Sample.UI"] = new()
    //     {
    //         ["/api"] = "https://localhost:7593",
    //     },
    // };
});



var apiService = builder.AddProject<Projects.Sample_ApiService>("apiservice")
    .WithHttpsGatewayUrl(gatewayOptions, "backend")
    .WithHttpHealthCheck("/health");

builder.AddProject<Projects.Sample_Web>("webfrontend")
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health")
    .WithHttpsGatewayUrl(gatewayOptions, "ui")
    .WithHttpsGatewayReference(apiService, gatewayOptions, "backend")
    .WaitFor(apiService);

builder.Build().Run();
