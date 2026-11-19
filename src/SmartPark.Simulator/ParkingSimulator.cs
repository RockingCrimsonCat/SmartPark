using System.Text.Json;
using SmartPark.Contracts;

namespace SmartPark.Simulator;

/// <summary>
/// Симулятор edge-шлюзу паркінгу. Емулює камери LPR (номер текстом + «впевненість»),
/// сенсори місць, шлагбауми, табло та автономний режим при обриві зв’язку.
/// </summary>
public class ParkingSimulator
{
    private const string EntryLane = "BAR-IN-1";
    private const string ExitLane = "BAR-OUT-1";
    private static readonly string[] DeviceCodes = { "CAM-IN-1", "CAM-OUT-1", "BAR-IN-1", "BAR-OUT-1", "DISPLAY-1", "GW-1" };
    private static readonly string[] KnownPlates = { "AA1234BB", "KA0001EV", "BX6666XX" };
    private const string Letters = "ABCEHIKMOPTX";

    private readonly EdgeClient _api;
    private readonly Guid _parkingId;
    private readonly string _dataDir;
    private readonly object _lock = new();

    private List<string> _spots = new();
    private readonly Dictionary<string, string?> _parked = new(); // номер → код місця
    private readonly HashSet<string> _occupiedSpots = new();
    private List<OfflineEvent> _buffer = new();
    private EdgeCache? _cache;

    public bool Online { get; private set; } = true;

    public ParkingSimulator(EdgeClient api, Guid parkingId, string dataDir)
    {
        _api = api;
        _parkingId = parkingId;
        _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        _buffer = Load<List<OfflineEvent>>("edge-buffer.json") ?? new();
        _cache = Load<EdgeCache>("edge-cache.json");
    }

    public async Task InitAsync()
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                _spots = await _api.SpotCodesAsync(_parkingId);
                await RefreshCacheAsync();
                Log($"Підключено до сервера. Паркінг {_parkingId}, місць: {_spots.Count}");
                return;
            }
            catch (Exception ex) when (attempt < 30)
            {
                Log($"Сервер недоступний ({ex.Message}), повтор через 3 с...");
                await Task.Delay(3000);
            }
        }
    }

    // ---------------- Камера LPR: в’їзд ----------------
    public async Task VehicleArrivesAsync(string? plate = null, double? confidence = null)
    {
        plate ??= RandomPlate();
        var conf = confidence ?? Math.Round(0.75 + Random.Shared.NextDouble() * 0.25, 2);
        Log($"[CAM-IN-1] Розпізнано номер {plate ?? "—"} (впевненість {conf:P0})");

        var request = new VehicleDetectedRequest(Guid.NewGuid(), _parkingId, plate, conf, EntryLane, DateTime.UtcNow);
        if (!Online)
        {
            OfflineEntry(request);
            return;
        }

        try
        {
            var decision = await _api.EntryAsync(request);
            Log($"[Сервер] {decision.Decision}: {decision.Message}");
            if (decision.Decision == GateDecisionType.Allow && plate is not null)
                ParkCar(plate);
        }
        catch (HttpRequestException ex)
        {
            Log($"Немає відповіді сервера ({ex.Message}) – рішення за локальним кешем");
            OfflineEntry(request);
        }
    }

    // ---------------- Камера LPR: виїзд ----------------
    public async Task VehicleLeavesAsync(string? plate = null)
    {
        lock (_lock)
        {
            plate ??= _parked.Keys.OrderBy(_ => Random.Shared.Next()).FirstOrDefault();
        }
        if (plate is null)
        {
            Log("На паркінгу немає автомобілів (за даними симулятора)");
            return;
        }

        var request = new VehicleDetectedRequest(Guid.NewGuid(), _parkingId, plate, 0.95, ExitLane, DateTime.UtcNow);
        Log($"[CAM-OUT-1] Розпізнано номер {plate}");
        if (!Online)
        {
            Buffer(new OfflineEvent(request.EventId, "exit", plate, ExitLane, request.DetectedAt));
            Log("[Офлайн] Виїзд дозволено локально, подію збережено в буфер");
            OpenBarrier(ExitLane, "офлайн-виїзд");
            LeaveCar(plate);
            return;
        }

        var decision = await _api.ExitAsync(request);
        Log($"[Сервер] {decision.Decision}: {decision.Message}");
        if (decision.Decision == GateDecisionType.PaymentRequired)
            Log($"[Табло] До сплати {decision.Amount:0.00} грн. Оплатіть за QR: {decision.PaymentUrl}");
        if (decision.Decision == GateDecisionType.Allow) LeaveCar(plate);
    }

    // ---------------- Сенсори місць ----------------
    public async Task ToggleRandomSensorAsync()
    {
        string spot;
        bool occupied;
        lock (_lock)
        {
            spot = _spots[Random.Shared.Next(_spots.Count)];
            occupied = !_occupiedSpots.Contains(spot);
        }
        await SendSensorAsync(spot, occupied);
    }

    private async Task SendSensorAsync(string spot, bool occupied)
    {
        lock (_lock)
        {
            if (occupied) _occupiedSpots.Add(spot);
            else _occupiedSpots.Remove(spot);
        }
        Log($"[Сенсор S-{spot}] {(occupied ? "зайнято" : "вільно")}");
        if (!Online) return; // сенсори в офлайні не буферизуються – стан передається повторно
        try
        {
            await _api.SensorAsync(new SpotSensorRequest(Guid.NewGuid(), _parkingId, spot, occupied, DateTime.UtcNow));
        }
        catch (HttpRequestException ex)
        {
            Log($"Помилка надсилання стану сенсора: {ex.Message}");
        }
    }

    // ---------------- Автономний режим ----------------
    public async Task ToggleConnectionAsync()
    {
        Online = !Online;
        if (!Online)
        {
            Log("=== ОБРИВ ЗВ’ЯЗКУ: рішення приймаються за локальним кешем, події – в буфер ===");
            return;
        }
        Log($"=== ЗВ’ЯЗОК ВІДНОВЛЕНО: досилання {_buffer.Count} подій ===");
        await FlushBufferAsync();
    }

    private void OfflineEntry(VehicleDetectedRequest r)
    {
        if (r.Plate is null || r.Confidence < 0.7)
        {
            Log("[Офлайн] Номер не розпізнано – в’їзд лише через інтерком");
            return;
        }
        if (_cache?.Blacklist.Contains(r.Plate) == true)
        {
            Log("[Офлайн] Номер у чорному списку – в’їзд заборонено");
            return;
        }
        var known = _cache?.AllowedPlates.Contains(r.Plate) == true ? "абонемент/бронювання" : "гість";
        Buffer(new OfflineEvent(r.EventId, "entry", r.Plate, r.LaneCode, r.DetectedAt));
        Log($"[Офлайн] В’їзд дозволено локально ({known}), подію збережено в буфер");
        OpenBarrier(EntryLane, "офлайн-в’їзд");
        ParkCar(r.Plate);
    }

    private async Task FlushBufferAsync()
    {
        if (_buffer.Count == 0) return;
        try
        {
            var result = await _api.SyncAsync(new SyncBatchRequest(_parkingId, _buffer.ToList()));
            Log($"[Синхронізація] прийнято {result.Accepted}, дублікатів {result.Duplicates}, помилок {result.Failed}");
            _buffer.Clear();
            Save("edge-buffer.json", _buffer);
        }
        catch (Exception ex)
        {
            Log($"[Синхронізація] не вдалося: {ex.Message}. Повторимо пізніше");
        }
    }

    private void Buffer(OfflineEvent e)
    {
        lock (_lock)
        {
            _buffer.Add(e);
            Save("edge-buffer.json", _buffer);
        }
    }

    // ---------------- Фонові задачі ----------------
    public async Task RunBackgroundAsync(CancellationToken ct)
    {
        var lastHeartbeat = DateTime.MinValue;
        var lastCache = DateTime.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            if (Online)
            {
                try
                {
                    foreach (var c in await _api.CommandsAsync(_parkingId))
                        OpenBarrier(c.DeviceCode, c.Reason);

                    if (DateTime.UtcNow - lastHeartbeat > TimeSpan.FromSeconds(20))
                    {
                        await _api.HeartbeatAsync(new HeartbeatRequest(_parkingId, DeviceCodes));
                        lastHeartbeat = DateTime.UtcNow;
                    }
                    if (DateTime.UtcNow - lastCache > TimeSpan.FromMinutes(1))
                    {
                        await RefreshCacheAsync();
                        lastCache = DateTime.UtcNow;
                    }
                    if (_buffer.Count > 0) await FlushBufferAsync();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log($"Фонова задача: {ex.Message}");
                }
            }
            await Task.Delay(2000, ct);
        }
    }

    public void PrintStatus()
    {
        lock (_lock)
        {
            var free = _spots.Count - _occupiedSpots.Count;
            Log($"[Табло DISPLAY-1] Вільних місць: {free} з {_spots.Count}");
            Log($"Зв’язок: {(Online ? "є" : "НЕМАЄ")}; авто на паркінгу: {string.Join(", ", _parked.Keys)}; у буфері подій: {_buffer.Count}");
        }
    }

    // ---------------- Допоміжне ----------------
    private void OpenBarrier(string deviceCode, string reason)
    {
        Log($"[Шлагбаум {deviceCode}] ВІДКРИТО ({reason})");
        _ = Task.Delay(3000).ContinueWith(_ => Log($"[Шлагбаум {deviceCode}] закрито"));
    }

    private void ParkCar(string plate)
    {
        string? spot;
        lock (_lock)
        {
            spot = _spots.FirstOrDefault(s => !_occupiedSpots.Contains(s));
            _parked[plate] = spot;
        }
        if (spot is not null)
            _ = Task.Delay(2000).ContinueWith(_ => SendSensorAsync(spot, true));
    }

    private void LeaveCar(string plate)
    {
        string? spot;
        lock (_lock)
        {
            _parked.Remove(plate, out spot);
        }
        if (spot is not null) _ = SendSensorAsync(spot, false);
    }

    private async Task RefreshCacheAsync()
    {
        _cache = await _api.CacheAsync(_parkingId);
        Save("edge-cache.json", _cache);
    }

    private string RandomPlate()
    {
        if (Random.Shared.NextDouble() < 0.3) return KnownPlates[Random.Shared.Next(KnownPlates.Length)];
        char L() => Letters[Random.Shared.Next(Letters.Length)];
        return $"{L()}{L()}{Random.Shared.Next(1000, 9999)}{L()}{L()}";
    }

    private T? Load<T>(string file)
    {
        var path = Path.Combine(_dataDir, file);
        return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), EdgeClient.Json) : default;
    }

    private void Save<T>(string file, T value) =>
        File.WriteAllText(Path.Combine(_dataDir, file), JsonSerializer.Serialize(value, EdgeClient.Json));

    public static void Log(string message) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
}
