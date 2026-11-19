using System.Text;
using SmartPark.Simulator;

// Симулятор паркінгу (емуляція edge-шлюзу та обладнання).
// Параметри: SERVER_URL, EDGE_KEY, PARKING_ID, SIM_DATA; ключ --auto – автоматичний режим.
Console.OutputEncoding = Encoding.UTF8;

var serverUrl = Environment.GetEnvironmentVariable("SERVER_URL") ?? "http://localhost:5000";
var edgeKey = Environment.GetEnvironmentVariable("EDGE_KEY") ?? "dev-edge-key";
var parkingId = Guid.Parse(Environment.GetEnvironmentVariable("PARKING_ID") ?? "11111111-1111-1111-1111-111111111111");
var dataDir = Environment.GetEnvironmentVariable("SIM_DATA") ?? "sim-data";
var auto = args.Contains("--auto") || Console.IsInputRedirected;

var sim = new ParkingSimulator(new EdgeClient(serverUrl, edgeKey), parkingId, dataDir);
await sim.InitAsync();

using var cts = new CancellationTokenSource();
var background = sim.RunBackgroundAsync(cts.Token);

// Сценарний режим для демонстрації: --script "1;2:KA0001EV;o;..." (команди через «;», пауза 3 с).
var scriptIndex = Array.IndexOf(args, "--script");
if (scriptIndex >= 0 && scriptIndex + 1 < args.Length)
{
    foreach (var step in args[scriptIndex + 1].Split(';', StringSplitOptions.RemoveEmptyEntries))
    {
        var parts = step.Split(':', 2);
        var arg = parts.Length > 1 ? parts[1] : null;
        await RunSafe(parts[0] switch
        {
            "1" => () => sim.VehicleArrivesAsync(arg),
            "3" => () => sim.VehicleArrivesAsync(null, 0.42),
            "4" => () => sim.VehicleLeavesAsync(arg),
            "6" => sim.ToggleRandomSensorAsync,
            "o" => sim.ToggleConnectionAsync,
            _ => () => { sim.PrintStatus(); return Task.CompletedTask; }
        });
        await Task.Delay(3000);
    }
    cts.Cancel();
    return;
}

if (auto)
{
    ParkingSimulator.Log("Автоматичний режим: випадкова подія кожні 5 секунд");
    while (true)
    {
        await RunSafe(RandomAction);
        await Task.Delay(5000);
    }
}

PrintHelp();
while (true)
{
    var key = Console.ReadKey(intercept: true).KeyChar;
    switch (char.ToLowerInvariant(key))
    {
        case '1': await RunSafe(() => sim.VehicleArrivesAsync()); break;
        case '2':
            Console.Write("Номер: ");
            var plate = Console.ReadLine();
            await RunSafe(() => sim.VehicleArrivesAsync(string.IsNullOrWhiteSpace(plate) ? null : plate.Trim()));
            break;
        case '3': await RunSafe(() => sim.VehicleArrivesAsync(null, 0.42)); break;
        case '4': await RunSafe(() => sim.VehicleLeavesAsync()); break;
        case '5':
            Console.Write("Номер: ");
            var exitPlate = Console.ReadLine();
            await RunSafe(() => sim.VehicleLeavesAsync(string.IsNullOrWhiteSpace(exitPlate) ? null : exitPlate.Trim()));
            break;
        case '6': await RunSafe(sim.ToggleRandomSensorAsync); break;
        case 'o': await RunSafe(sim.ToggleConnectionAsync); break;
        case 's': sim.PrintStatus(); break;
        case 'h': PrintHelp(); break;
        case 'q':
            cts.Cancel();
            return;
    }
}

async Task RandomAction()
{
    var roll = Random.Shared.Next(10);
    if (roll < 4) await sim.VehicleArrivesAsync();
    else if (roll < 7) await sim.VehicleLeavesAsync();
    else if (roll < 9) await sim.ToggleRandomSensorAsync();
    else await sim.VehicleArrivesAsync(null, 0.4);
}

static async Task RunSafe(Func<Task> action)
{
    try
    {
        await action();
    }
    catch (Exception ex)
    {
        ParkingSimulator.Log($"Помилка: {ex.Message}");
    }
}

static void PrintHelp() => Console.WriteLine("""
    ---------------- Симулятор паркінгу SmartPark ----------------
     1 – в’їзд випадкового авто       2 – в’їзд авто з номером
     3 – в’їзд, номер не розпізнано   4 – виїзд випадкового авто
     5 – виїзд авто з номером         6 – змінити стан сенсора місця
     o – обрив / відновлення зв’язку  s – стан (табло, буфер)
     h – довідка                      q – вихід
    ---------------------------------------------------------------
    """);
