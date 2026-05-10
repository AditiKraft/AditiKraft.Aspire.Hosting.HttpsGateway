using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AditiKraft.Aspire.Hosting.HttpsGateway.Certificates;

public sealed class CloudflareDnsService(HttpClient http)
{
    public async Task<(string ZoneId, string ZoneName)> GetZoneIdAsync(string domain)
    {
        HttpResponseMessage response = await http.GetAsync($"zones?name={domain}");
        response.EnsureSuccessStatusCode();
        string body = await response.Content.ReadAsStringAsync();
        CloudflareResponse<List<ZoneResult>>? result =
            await response.Content.ReadFromJsonAsync<CloudflareResponse<List<ZoneResult>>>();

        if (result is { Success: true, Result.Count: > 0 })
        {
            return (result.Result[0].Id, result.Result[0].Name);
        }

        // Try parent domain (e.g. local.iambip.in → iambip.in)
        string[] parts = domain.Split('.');
        if (parts.Length > 2)
        {
            string parent = string.Join('.', parts[^2..]);
            response = await http.GetAsync($"zones?name={parent}");
            response.EnsureSuccessStatusCode();
            body = await response.Content.ReadAsStringAsync();
            result = await response.Content.ReadFromJsonAsync<CloudflareResponse<List<ZoneResult>>>();
            if (result is { Success: true, Result.Count: > 0 })
            {
                return (result.Result[0].Id, result.Result[0].Name);
            }
        }

        throw new InvalidOperationException(
            $"Cloudflare zone not found for '{domain}' or its parent. " +
            $"Verify: (1) domain is registered in your Cloudflare account, (2) API token has Zone:Read permission. " +
            $"API: {body[..Math.Min(body.Length, 300)]}");
    }

    public async Task<string> CreateTxtRecordAsync(string zoneId, string name, string content)
    {
        var payload = new DnsRecordRequest { Type = "TXT", Name = name, Content = content, Ttl = 120 };

        HttpResponseMessage response = await http.PostAsJsonAsync($"zones/{zoneId}/dns_records", payload);
        response.EnsureSuccessStatusCode();
        CloudflareResponse<DnsRecordResult>? result =
            await response.Content.ReadFromJsonAsync<CloudflareResponse<DnsRecordResult>>();
        if (result is not { Success: true })
        {
            throw new InvalidOperationException(
                $"Failed to create DNS TXT record: {string.Join(", ", result?.Errors ?? [])}");
        }

        return result.Result!.Id;
    }

    public async Task DeleteRecordAsync(string zoneId, string recordId) =>
        await http.DeleteAsync($"zones/{zoneId}/dns_records/{recordId}");

    private sealed class DnsRecordRequest
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
        [JsonPropertyName("ttl")] public int Ttl { get; set; } = 120;
    }

    private sealed record CloudflareResponse<T>(
        [property: JsonPropertyName("success")]
        bool Success,
        [property: JsonPropertyName("result")] T? Result,
        [property: JsonPropertyName("errors")] string[]? Errors
    );

    private sealed record DnsRecordResult(
        [property: JsonPropertyName("id")] string Id
    );

    private sealed record ZoneResult(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name
    );
}
