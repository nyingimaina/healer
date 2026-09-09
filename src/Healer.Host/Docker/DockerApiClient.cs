using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Healer.Host.Serialization;
using Serilog;

namespace Healer.Host.Docker;

/// <summary>
/// Raw transport to the Docker Engine API over its Unix domain socket. Deliberately hand-rolled
/// rather than using Docker.DotNet: that library's Native AOT/trimming support is unproven, while
/// this is a thin HttpClient configured with a Unix-socket ConnectCallback plus source-generated
/// JSON — both fully AOT-safe. All Docker Engine API paths accept a container's NAME or ID
/// interchangeably, so callers never need to resolve a name to an id first.
/// </summary>
public sealed class DockerApiClient : IDisposable
{
    private readonly HttpClient _http;

    public DockerApiClient(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };

        _http = new HttpClient(handler) { BaseAddress = new Uri("http://docker/") };
    }

    public async Task<List<DockerContainerListItem>> ListContainersAsync(bool all, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"containers/json?all={(all ? "true" : "false")}", ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, HealerJsonContext.Default.ListDockerContainerListItem, ct)
            ?? [];
    }

    public async Task<DockerContainerInspect?> InspectAsync(string nameOrId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"containers/{Uri.EscapeDataString(nameOrId)}/json", ct);
        if (!response.IsSuccessStatusCode)
        {
            // Not necessarily an error — the container may simply have been removed between the
            // list call and this inspect call — but worth a trace-level breadcrumb either way.
            Log.Debug("Docker inspect for {Container} returned {StatusCode}", nameOrId, (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, HealerJsonContext.Default.DockerContainerInspect, ct);
    }

    public async Task<DockerStatsResponse?> GetStatsAsync(string nameOrId, CancellationToken ct)
    {
        using var response = await _http.GetAsync($"containers/{Uri.EscapeDataString(nameOrId)}/stats?stream=false", ct);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Docker stats for {Container} returned {StatusCode} — memory/CPU usage for it will read as 0 this tick", nameOrId, (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync(stream, HealerJsonContext.Default.DockerStatsResponse, ct);
    }

    public async Task RestartContainerAsync(string nameOrId, TimeSpan timeout, CancellationToken ct)
    {
        using var response = await _http.PostAsync(
            $"containers/{Uri.EscapeDataString(nameOrId)}/restart?t={(int)timeout.TotalSeconds}",
            content: null, ct);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            Log.Error("Docker restart for {Container} failed with {StatusCode}: {Body}", nameOrId, (int)response.StatusCode, body);
        }

        response.EnsureSuccessStatusCode();
    }

    public async Task<(int Count, long BytesReclaimed)> PruneStoppedContainersAsync(CancellationToken ct)
    {
        using var response = await _http.PostAsync("containers/prune", content: null, ct);
        response.EnsureSuccessStatusCode();
        return await ParsePruneResultAsync(response, "ContainersDeleted", ct);
    }

    public async Task<(int Count, long BytesReclaimed)> PruneDanglingImagesAsync(TimeSpan retention, CancellationToken ct)
    {
        // Docker's prune filter syntax: until=<duration> keeps anything newer than the retention window.
        var filters = Uri.EscapeDataString($"{{\"until\":[\"{(int)retention.TotalHours}h\"]}}");
        using var response = await _http.PostAsync($"images/prune?filters={filters}", content: null, ct);
        response.EnsureSuccessStatusCode();
        return await ParsePruneResultAsync(response, "ImagesDeleted", ct);
    }

    private static async Task<(int Count, long BytesReclaimed)> ParsePruneResultAsync(HttpResponseMessage response, string deletedArrayProperty, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var root = doc.RootElement;

        var count = root.TryGetProperty(deletedArrayProperty, out var deleted) && deleted.ValueKind == JsonValueKind.Array
            ? deleted.GetArrayLength()
            : 0;
        var bytes = root.TryGetProperty("SpaceReclaimed", out var reclaimed) && reclaimed.ValueKind == JsonValueKind.Number
            ? reclaimed.GetInt64()
            : 0L;

        return (count, bytes);
    }

    public void Dispose() => _http.Dispose();
}
