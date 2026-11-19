using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SmartPark.Contracts;

namespace SmartPark.Simulator;

/// <summary>HTTP-клієнт REST API сервера для edge-шлюзу (замість MQTT цільової архітектури).</summary>
public class EdgeClient
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly HttpClient _http;

    public EdgeClient(string serverUrl, string edgeKey)
    {
        _http = new HttpClient { BaseAddress = new Uri(serverUrl), Timeout = TimeSpan.FromSeconds(5) };
        _http.DefaultRequestHeaders.Add("X-Edge-Key", edgeKey);
    }

    public Task<GateDecision> EntryAsync(VehicleDetectedRequest r) => PostAsync<GateDecision>("api/edge/entries", r);

    public Task<GateDecision> ExitAsync(VehicleDetectedRequest r) => PostAsync<GateDecision>("api/edge/exits", r);

    public Task SensorAsync(SpotSensorRequest r) => PostAsync<object?>("api/edge/sensors", r);

    public Task HeartbeatAsync(HeartbeatRequest r) => PostAsync<object?>("api/edge/heartbeat", r);

    public async Task<List<DeviceCommandDto>> CommandsAsync(Guid parkingId) =>
        await _http.GetFromJsonAsync<List<DeviceCommandDto>>($"api/edge/commands?parkingId={parkingId}", Json) ?? new();

    public async Task<EdgeCache?> CacheAsync(Guid parkingId) =>
        await _http.GetFromJsonAsync<EdgeCache>($"api/edge/cache?parkingId={parkingId}", Json);

    public Task<SyncBatchResult> SyncAsync(SyncBatchRequest r) => PostAsync<SyncBatchResult>("api/edge/sync", r);

    /// <summary>Коди паркомісць паркінгу (публічний ендпоінт каталогу).</summary>
    public async Task<List<string>> SpotCodesAsync(Guid parkingId)
    {
        var doc = await _http.GetFromJsonAsync<JsonElement>($"api/parkings/{parkingId}", Json);
        return doc.GetProperty("levels").EnumerateArray()
            .SelectMany(l => l.GetProperty("spots").EnumerateArray())
            .Select(s => s.GetProperty("code").GetString()!)
            .ToList();
    }

    private async Task<T> PostAsync<T>(string url, object body)
    {
        var response = await _http.PostAsJsonAsync(url, body, Json);
        response.EnsureSuccessStatusCode();
        if (response.StatusCode == System.Net.HttpStatusCode.NoContent) return default!;
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }
}
