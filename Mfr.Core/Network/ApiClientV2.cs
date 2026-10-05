using System.Net;
using System.Net.Http.Json;
using Mfr.Core.Exceptions;
using Mfr.Protocol;
using Mfr.Protocol.Dto;

namespace Mfr.Core.Network;

/// <summary>Result of the active-version lookup: found / no such line / nothing published yet.</summary>
public readonly record struct LineVersion(string? Id, LineVersionState State);

public enum LineVersionState
{
    Found,
    NoSuchLine,
    NothingYet,
}

/// <summary>
/// v2 API client (Kotlin: KtorProvider) — compatibility lines, versions,
/// manifest links, launcher schema and region ping. Client id travels in the
/// X-Client-ID header. Transient failures retry up to five times.
/// </summary>
public sealed class ApiClientV2
{
    private const int MaxRetries = 5;

    private readonly HttpClient _client;

    public ApiClientV2(HttpClient client, string ruAddress, string euAddress)
    {
        _client = client;
        RuBase = $"https://{ruAddress}";
        EuBase = $"https://{euAddress}";
    }

    public string RuBase { get; }

    public string EuBase { get; }

    public string Base(Region region) => region == Region.RU ? RuBase : EuBase;

    public void SetClientId(Guid clientId)
    {
        _client.DefaultRequestHeaders.Remove("X-Client-ID");
        _client.DefaultRequestHeaders.Add("X-Client-ID", clientId.ToString("D"));
    }

    public Task<IReadOnlyList<string>> GetChannels(Region region, CancellationToken ct = default) =>
        GetAsync<ChannelList>($"{Base(region)}/v2/game/channels?os=WINDOWS", ct)
            .ContinueWith(t => (IReadOnlyList<string>)t.Result.Channels.Select(c => c.Id).ToList(), ct);

    public async Task<LineVersion> GetGameVersion(string line, Region region, CancellationToken ct = default)
    {
        var response = await GetWithRetriesAsync($"{Base(region)}/v2/game/{Uri.EscapeDataString(line)}/version?os=WINDOWS", ct).ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
            var version = await response.Content.ReadFromJsonAsync<VersionId>(cancellationToken: ct).ConfigureAwait(false);
            return new LineVersion(version?.Id, LineVersionState.Found);
        }
        return response.StatusCode switch
        {
            HttpStatusCode.NotFound => new LineVersion(null, LineVersionState.NoSuchLine),
            HttpStatusCode.ServiceUnavailable => new LineVersion(null, LineVersionState.NothingYet),
            _ => throw new ServerConnectionException($"HTTP {(int)response.StatusCode} от /v2/game/version"),
        };
    }

    public Task<GameSchemaResponse> GetGameFiles(string version, Region region, CancellationToken ct = default) =>
        GetAsync<GameSchemaResponse>(
            $"{Base(region)}/v1/game/files?os=WINDOWS&version={Uri.EscapeDataString(version)}&region={region}", ct);

    public Task<string> GetLauncherVersion(Region region, CancellationToken ct = default) =>
        GetAsync<VersionId>($"{Base(region)}/v2/launcher/version?os=WINDOWS", ct)
            .ContinueWith(t => t.Result.Id, ct);

    public Task<LauncherSchemaResponse> GetLauncherFiles(string version, Region region, CancellationToken ct = default) =>
        GetAsync<LauncherSchemaResponse>(
            $"{Base(region)}/v2/launcher/files?os=WINDOWS&version={Uri.EscapeDataString(version)}&region={region}", ct);

    /// <summary>Latency of a region in ms, or null when unreachable (5s timeouts).</summary>
    public async Task<int?> PingAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            using var response = await _client.GetAsync($"{baseUrl}/ping", timeout.Token).ConfigureAwait(false);
            watch.Stop();
            return response.IsSuccessStatusCode ? (int)watch.ElapsedMilliseconds : null;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// <summary>Chooses the fastest reachable region, preferring RU on ties (Kotlin: checkAvailable).</summary>
    public async Task<Region> ChooseRegionAsync(CancellationToken ct = default)
    {
        var ru = await PingAsync(RuBase, ct).ConfigureAwait(false);
        var eu = await PingAsync(EuBase, ct).ConfigureAwait(false);
        return eu is { } euMs && (ru is null || euMs < ru.Value) ? Region.EU : Region.RU;
    }

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        var response = await GetWithRetriesAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new ServerConnectionException($"HTTP {(int)response.StatusCode} от {url}");
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false)
            ?? throw new ServerConnectionException($"Пустой ответ от {url}");
    }

    private async Task<HttpResponseMessage> GetWithRetriesAsync(string url, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var response = await _client.GetAsync(url, ct).ConfigureAwait(false);
                // 503 "nothing published yet" is a legitimate answer for version lookups,
                // everything else >=500 retries (Kotlin parity)
                if ((int)response.StatusCode >= 500 && response.StatusCode != HttpStatusCode.ServiceUnavailable && attempt < MaxRetries)
                {
                    response.Dispose();
                    await Task.Delay(Backoff.Delay(attempt), ct).ConfigureAwait(false);
                    continue;
                }
                return response;
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException && attempt < MaxRetries)
            {
                await Task.Delay(Backoff.Delay(attempt), ct).ConfigureAwait(false);
            }
        }
    }
}
