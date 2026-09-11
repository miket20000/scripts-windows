using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Gp.ZeroTier.Connect.Core;

namespace Gp.ZeroTier.Connect;

public sealed class BackendClient(HttpClient http)
{
    public async Task<BootstrapResponse> BootstrapAsync(string code, CancellationToken cancellationToken)
    {
        using var response = await http.PostAsJsonAsync("guest/zerotier/bootstrap", new BootstrapRequest(code), cancellationToken);
        return await ReadAsync<BootstrapResponse>(response, "ZT_BOOTSTRAP_FAILED", cancellationToken);
    }

    public async Task<EnrollResponse> EnrollAsync(string token, string nodeId, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, "guest/zerotier/enroll", token);
        request.Content = JsonContent.Create(new EnrollRequest(nodeId));
        using var response = await http.SendAsync(request, cancellationToken);
        return await ReadAsync<EnrollResponse>(response, "ZT_ENROLL_FAILED", cancellationToken);
    }

    public async Task<LeaseStatusResponse> StatusAsync(string token, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Get, "guest/zerotier/status", token);
        using var response = await http.SendAsync(request, cancellationToken);
        return await ReadAsync<LeaseStatusResponse>(response, "ZT_STATUS_FAILED", cancellationToken);
    }

    public async Task SendTelemetryAsync(string token, TelemetryEvent telemetry, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, "guest/zerotier/telemetry", token);
        request.Content = JsonContent.Create(telemetry);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task CleanupAckAsync(string token, string networkId, CancellationToken cancellationToken)
    {
        using var request = Authorized(HttpMethod.Post, "guest/zerotier/cleanup-ack", token);
        request.Content = JsonContent.Create(new CleanupAckRequest(networkId, "left"));
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, string code, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new BackendException(code, $"Serwer odrzucił żądanie (HTTP {(int)response.StatusCode}).", response.StatusCode);
        var value = await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken);
        return value ?? throw new LauncherException(code, "Serwer zwrócił nieprawidłową odpowiedź.");
    }
}
