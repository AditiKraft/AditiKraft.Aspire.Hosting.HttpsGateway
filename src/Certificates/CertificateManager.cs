using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using AditiKraft.Aspire.Hosting.HttpsGateway;
using Directory = System.IO.Directory;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.Certificates;

public sealed class CertificateManager(
    HttpsGatewayOptions options,
    IHttpClientFactory httpClientFactory,
    S3CertificateStore remoteCertificateStore)
{
    private static bool IsCertValid(string certPath)
    {
        if (!File.Exists(certPath))
        {
            return false;
        }

        try
        {
            string pem = File.ReadAllText(certPath);
            return IsPemCertValid(pem);
        }
        catch
        {
            return false;
        }
    }

    public async Task EnsureCertificateAsync()
    {
        string certPath = options.CertFullPath();

        if (File.Exists(certPath))
        {
            if (IsCertValid(certPath))
            {
                Console.WriteLine($"[Gateway] Certificate valid at {certPath}, skipping renewal.");
                return;
            }

            Console.WriteLine($"[Gateway] Local certificate exists but is expired or invalid at {certPath}.");
            await RequestAndSaveCertificateAsync(certPath);
            return;
        }

        if (await TryUseRemoteCertificateAsync(certPath))
        {
            return;
        }

        await RequestAndSaveCertificateAsync(certPath);
    }

    public async Task RenewIfNeededAsync()
    {
        string certPath = options.CertFullPath();

        if (!File.Exists(certPath))
        {
            return;
        }

        if (!IsCertValid(certPath))
        {
            Console.WriteLine("[Gateway] Renewing certificate...");
            string pem = await OrderCertificateAsync();
            await SaveCertificateAsync(certPath, pem);
            Console.WriteLine("[Gateway] Certificate renewed.");
            await UploadRemoteCertificateAsync(pem);
        }
    }

    private async Task<bool> TryUseRemoteCertificateAsync(string certPath)
    {
        if (!options.RemoteCertificateStore.Enabled)
        {
            return false;
        }

        string? remotePem = await remoteCertificateStore.DownloadCertificateAsync();

        if (remotePem is null)
        {
            return false;
        }

        if (!IsPemCertValid(remotePem))
        {
            Console.WriteLine("[Gateway] Remote certificate exists but is expired or invalid; requesting a new certificate.");
            return false;
        }

        await SaveCertificateAsync(certPath, remotePem);
        Console.WriteLine($"[Gateway] Remote certificate is valid and saved to local store: {certPath}");
        return true;
    }

    private async Task UploadRemoteCertificateAsync(string pem)
    {
        if (options.RemoteCertificateStore.Enabled)
        {
            await remoteCertificateStore.UploadCertificateAsync(pem);
        }
    }

    private async Task RequestAndSaveCertificateAsync(string certPath)
    {
        Console.WriteLine(
            $"[Gateway] Requesting {(options.UseStaging ? "STAGING" : "PRODUCTION")} certificate for {options.WildcardDomain}");
        string pem = await OrderCertificateAsync();

        await SaveCertificateAsync(certPath, pem);
        Console.WriteLine($"[Gateway] Certificate saved to {certPath}");
        await UploadRemoteCertificateAsync(pem);
    }

    private static async Task SaveCertificateAsync(string certPath, string pem)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(certPath)!);
        await File.WriteAllTextAsync(certPath, pem);
    }

    private static bool IsPemCertValid(string pem)
    {
        try
        {
            using var cert = X509Certificate2.CreateFromPem(pem);
            return cert.NotAfter > DateTime.UtcNow.AddDays(30);
        }
        catch
        {
            return false;
        }
    }

    private async Task<string> OrderCertificateAsync()
    {
        Uri? server = options.UseStaging
            ? WellKnownServers.LetsEncryptStagingV2
            : WellKnownServers.LetsEncryptV2;

        Console.WriteLine($"[Gateway] Connecting to ACME server: {server.AbsoluteUri}");
        var acme = new AcmeContext(server);
        await acme.NewAccount(options.Email, true);

        Console.WriteLine($"[Gateway] ACME account ready for {options.Email}");

        IOrderContext? order = await acme.NewOrder([options.WildcardDomain, options.Domain]);

        var cloudflare = new CloudflareDnsService(
            httpClientFactory.CreateClient("Cloudflare"));

        (string zoneId, string zoneName) = await cloudflare.GetZoneIdAsync(options.Domain);
        Console.WriteLine($"[Gateway] Cloudflare zone: {zoneName} ({zoneId})");

        // Phase 1: Create all DNS TXT records (both domains share same record name)
        var challenges = new List<(IAuthorizationContext Authz, IChallengeContext Challenge, string RecordId)>();

        foreach (IAuthorizationContext? authz in await order.Authorizations())
        {
            IChallengeContext? dnsChallenge = await authz.Dns();
            string dnsTxtValue = ComputeDnsTxtValue(dnsChallenge.Token, acme.AccountKey.Thumbprint());
            string recordName = $"_acme-challenge.{options.Domain}";
            string relativeName = recordName.EndsWith(zoneName)
                ? recordName[..^(zoneName.Length + 1)]
                : recordName;

            Console.WriteLine($"[Gateway] Creating TXT record: {relativeName} = {dnsTxtValue}");
            string recordId = await cloudflare.CreateTxtRecordAsync(zoneId, relativeName, dnsTxtValue);
            Console.WriteLine($"[Gateway] TXT record created, id: {recordId}");

            challenges.Add((authz, dnsChallenge, recordId));
        }

        // Phase 2: Wait for DNS propagation
        Console.WriteLine("[Gateway] Waiting 30s for DNS propagation...");
        await Task.Delay(TimeSpan.FromSeconds(30));

        // Phase 3: Validate all challenges
        foreach ((IAuthorizationContext authz, IChallengeContext challenge, string recordId) in challenges)
        {
            await challenge.Validate();
            Console.WriteLine("[Gateway] Challenge submitted, checking status...");

            int retries = 30;
            while (retries-- > 0)
            {
                Authorization? authzCtx = await authz.Resource();
                Console.WriteLine($"[Gateway] Challenge status: {authzCtx.Status} (retries left: {retries})");

                if (authzCtx.Status == AuthorizationStatus.Valid)
                {
                    break;
                }

                if (authzCtx.Status == AuthorizationStatus.Invalid)
                {
                    // Don't clean up here — other challenge might still need its record
                    throw new InvalidOperationException(
                        $"DNS challenge INVALID. Verify _acme-challenge.{options.Domain} has the correct TXT value.");
                }

                await Task.Delay(TimeSpan.FromSeconds(3));
            }
        }

        // Phase 4: Clean up all TXT records
        foreach ((IAuthorizationContext _, IChallengeContext _, string recordId) in challenges)
        {
            await cloudflare.DeleteRecordAsync(zoneId, recordId);
        }

        Console.WriteLine("[Gateway] All challenges validated, TXT records cleaned up");

        Console.WriteLine("[Gateway] Generating certificate...");
        IKey? privateKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
        CertificateChain? certChain = await order.Generate(new CsrInfo { CommonName = options.Domain }, privateKey);

        return BuildPem(certChain, privateKey);
    }

    private static string BuildPem(CertificateChain chain, IKey key)
    {
        var sb = new StringBuilder();

        sb.AppendLine("-----BEGIN CERTIFICATE-----");
        sb.AppendLine(Convert.ToBase64String(chain.Certificate.ToDer(), Base64FormattingOptions.InsertLineBreaks));
        sb.AppendLine("-----END CERTIFICATE-----");
        sb.Append(key.ToPem());

        return sb.ToString();
    }

    private static string ComputeDnsTxtValue(string token, string thumbprint)
    {
        string keyAuthz = $"{token}.{thumbprint}";
        Console.WriteLine($"[Gateway] Key authorization: {keyAuthz}");
        byte[] hash = SHA256.HashData(Encoding.ASCII.GetBytes(keyAuthz));
        return Convert.ToBase64String(hash)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
