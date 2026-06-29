namespace AditiKraft.Aspire.Hosting.HttpsGateway;

public sealed class HttpsGatewayOptions
{
    public string Domain { get; set; } = "local.iambip.in";
    public string Email { get; set; } = "";
    public string CloudflareApiToken { get; set; } = "";
    public bool UseStaging { get; set; } = true;
    public int Port { get; set; } = 443;
    public string DashboardSubdomain { get; set; } = "aspire";

    /// <summary>
    /// When true (default), the Aspire dashboard is exposed through the gateway under
    /// <see cref="DashboardSubdomain"/>, with its route auto-wired from the AppHost's own URL.
    /// </summary>
    public bool ExposeDashboard { get; set; } = true;

    public bool EnableVerboseProxyLogging { get; set; }

    public string CertStoreDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HttpsGateway",
        "certs");

    public string CertPassword { get; set; } = "dev-gateway-cert";
    public Dictionary<string, string> Routes { get; set; } = new();
    public Dictionary<string, Dictionary<string, string>> PathRoutes { get; set; } = new();
    public RemoteCertificateStoreOptions RemoteCertificateStore { get; } = new();

    public string CertFileName => UseStaging ? "gateway-staging.pem" : "gateway.pem";

    public string WildcardDomain => $"*.{Domain}";

    public string DashboardHost => $"{DashboardSubdomain}.{Domain}";

    private string CertStoreDomainDirectoryName
    {
        get
        {
            char[] invalidChars = Path.GetInvalidFileNameChars();
            string domain = Domain.ToLowerInvariant();

            foreach (char invalidChar in invalidChars)
            {
                domain = domain.Replace(invalidChar, '-');
            }

            return domain;
        }
    }

    public string CertFullPath() =>
        Path.Combine(CertStoreDirectory, CertStoreDomainDirectoryName, CertFileName);

    public string PublicUrl(string subdomain, string path = "")
    {
        string port = Port == 443 ? "" : $":{Port}";
        return $"https://{subdomain}.{Domain}{port}{NormalizePath(path)}";
    }

    public string RemoteCertificateObjectKey() =>
        RemoteCertificateStore.ResolveObjectKey(Domain, CertFileName);

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "";
        }

        return path.StartsWith('/') ? path : $"/{path}";
    }
}

public sealed class RemoteCertificateStoreOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "";
    public string BucketName { get; set; } = "";
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Region { get; set; } = "auto";
    public bool ForcePathStyle { get; set; } = true;
    public string ObjectKeyPrefix { get; set; } = "certificates";
    public string ObjectKey { get; set; } = "";

    public string ResolveObjectKey(string domain, string certFileName)
    {
        if (!string.IsNullOrWhiteSpace(ObjectKey))
        {
            return ObjectKey.Trim().TrimStart('/');
        }

        string prefix = ObjectKeyPrefix.Trim().Trim('/');
        string domainSegment = domain.Trim().Trim('/').ToLowerInvariant();

        return string.IsNullOrWhiteSpace(prefix)
            ? $"{domainSegment}/{certFileName}"
            : $"{prefix}/{domainSegment}/{certFileName}";
    }
}
