using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Mfr.Core.Exceptions;
using Mfr.Core.Options;
using Mfr.Protocol;
using Mfr.Protocol.Dto;

namespace Mfr.Core.Network;

/// <summary>
/// REST client of the public API (Kotlin: RestProvider over WebClient):
/// GET /api/v1/game, GET /api/v1/game/{id}?lastUpdate=, GET /api/v1/client.
/// Sends the Identity header, retries transient failures three times and
/// maps HTTP 503 to <see cref="ServerMaintenanceException"/>.
/// </summary>
public sealed class ApiServerClient
{
    private const int Attempts = 3;

    private readonly HttpClient _client;

    public ApiServerClient(HttpClient client, LauncherOptions options)
    {
        _client = client;
        _client.BaseAddress = new Uri($"https://{options.Server.Address}/");
        _client.Timeout = TimeSpan.FromSeconds(10);
        _client.DefaultRequestHeaders.Remove("Identity");
        _client.DefaultRequestHeaders.Add("Identity", options.ClientId.ToString("D"));
    }

    public Task<BuildDto[]> GetBuilds(CancellationToken cancellationToken = default) =>
        GetAsync<BuildDto[]>("api/v1/game", cancellationToken);

    public Task<LauncherVersionDto[]> GetLauncherVersions(CancellationToken cancellationToken = default) =>
        GetAsync<LauncherVersionDto[]>("api/v1/client", cancellationToken);

    public Task<Content> GetGameContent(int buildId, DateTime? lastUpdate = null, CancellationToken cancellationToken = default)
    {
        var url = lastUpdate is { } value
            ? $"api/v1/game/{buildId}?lastUpdate={value:yyyy-MM-dd'T'HH:mm:ss}"
            : $"api/v1/game/{buildId}";
        return GetAsync<Content>(url, cancellationToken);
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                    throw new ServerMaintenanceException();
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} от {url}");

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                return await JsonSerializer.DeserializeAsync<T>(stream, ProtocolJson.Options, cancellationToken)
                    ?? throw new ServerConnectionException($"Пустой ответ от {url}");
            }
            catch (Exception exception) when (
                exception is HttpRequestException or IOException or SocketException &&
                attempt < Attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
