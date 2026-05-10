using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using AditiKraft.Aspire.Hosting.HttpsGateway;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.Certificates;

public sealed class S3CertificateStore(HttpsGatewayOptions options)
{
    public async Task<string?> DownloadCertificateAsync(CancellationToken cancellationToken = default)
    {
        if (!options.RemoteCertificateStore.Enabled)
        {
            return null;
        }

        ValidateOptions();

        string objectKey = options.RemoteCertificateObjectKey();

        try
        {
            using AmazonS3Client client = CreateClient();
            using GetObjectResponse response = await client.GetObjectAsync(
                options.RemoteCertificateStore.BucketName,
                objectKey,
                cancellationToken);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(cancellationToken);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound || ex.ErrorCode == "NoSuchKey")
        {
            Console.WriteLine($"[Gateway] Remote certificate not found at s3://{options.RemoteCertificateStore.BucketName}/{objectKey}.");
            return null;
        }
        catch (AmazonS3Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to download remote gateway certificate from s3://{options.RemoteCertificateStore.BucketName}/{objectKey}.",
                ex);
        }
    }

    public async Task UploadCertificateAsync(string pem, CancellationToken cancellationToken = default)
    {
        if (!options.RemoteCertificateStore.Enabled)
        {
            return;
        }

        ValidateOptions();

        string objectKey = options.RemoteCertificateObjectKey();

        try
        {
            byte[] body = Encoding.UTF8.GetBytes(pem);
            using AmazonS3Client client = CreateClient();
            using var stream = new MemoryStream(body);
            var request = new PutObjectRequest
            {
                BucketName = options.RemoteCertificateStore.BucketName,
                Key = objectKey,
                InputStream = stream,
                AutoCloseStream = false,
                ContentType = "application/x-pem-file",
                DisableDefaultChecksumValidation = true,
                DisablePayloadSigning = true,
                UseChunkEncoding = false
            };
            request.Headers.ContentLength = body.Length;

            await client.PutObjectAsync(request, cancellationToken);
            Console.WriteLine($"[Gateway] Certificate uploaded to s3://{options.RemoteCertificateStore.BucketName}/{objectKey}.");
        }
        catch (AmazonS3Exception ex)
        {
            throw new InvalidOperationException(
                $"Failed to upload remote gateway certificate to s3://{options.RemoteCertificateStore.BucketName}/{objectKey}.",
                ex);
        }
    }

    private AmazonS3Client CreateClient()
    {
        var credentials = new BasicAWSCredentials(
            options.RemoteCertificateStore.AccessKeyId,
            options.RemoteCertificateStore.SecretAccessKey);

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.RemoteCertificateStore.ForcePathStyle,
            AuthenticationRegion = options.RemoteCertificateStore.Region,
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED
        };

        if (!string.IsNullOrWhiteSpace(options.RemoteCertificateStore.Endpoint))
        {
            config.ServiceURL = options.RemoteCertificateStore.Endpoint;
        }

        return new AmazonS3Client(credentials, config);
    }

    private void ValidateOptions()
    {
        if (string.IsNullOrWhiteSpace(options.RemoteCertificateStore.BucketName))
        {
            throw new InvalidOperationException("Gateway remote certificate store is enabled, but BucketName is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.RemoteCertificateStore.AccessKeyId))
        {
            throw new InvalidOperationException("Gateway remote certificate store is enabled, but AccessKeyId is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.RemoteCertificateStore.SecretAccessKey))
        {
            throw new InvalidOperationException("Gateway remote certificate store is enabled, but SecretAccessKey is not configured.");
        }

        if (string.IsNullOrWhiteSpace(options.RemoteCertificateStore.Region))
        {
            throw new InvalidOperationException("Gateway remote certificate store is enabled, but Region is not configured.");
        }
    }
}
