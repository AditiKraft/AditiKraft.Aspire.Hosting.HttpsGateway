using System.Security.Cryptography.X509Certificates;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.Certificates;

internal static class GatewayCertificateLoader
{
    public static X509Certificate2 LoadFromPem(string certPath)
    {
        string pem = File.ReadAllText(certPath);
        using var certificate = X509Certificate2.CreateFromPem(pem, pem);

        return X509CertificateLoader.LoadPkcs12(
            certificate.Export(X509ContentType.Pkcs12),
            null);
    }
}
