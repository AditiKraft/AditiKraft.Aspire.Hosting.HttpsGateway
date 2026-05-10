using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.Certificates;

public sealed class CertificateRenewalService(
    CertificateManager certManager,
    ILogger<CertificateRenewalService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(12), stoppingToken);
                await certManager.RenewIfNeededAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Certificate renewal check failed");
            }
        }
    }
}
