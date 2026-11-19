using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Security;

namespace SmartPark.Server.Infrastructure;

/// <summary>
/// Початкове наповнення БД для демонстрації: ролі, тестові користувачі,
/// три паркінги з рівнями, місцями, пристроями й тарифами.
/// Паролі тестових користувачів – у README.md (лише для середовища розробки).
/// </summary>
public static class DataSeeder
{
    public static readonly Guid CentralParkingId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static async Task SeedAsync(IServiceProvider sp, IConfiguration config)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var roles = sp.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        var users = sp.GetRequiredService<UserManager<AppUser>>();

        foreach (var role in Roles.All)
            if (!await roles.RoleExistsAsync(role))
                await roles.CreateAsync(new IdentityRole<Guid>(role));

        if (await db.Parkings.AnyAsync()) return;

        var parkings = new[]
        {
            NewParking(CentralParkingId, "Паркінг «Центральний»", "Київ", "вул. Хрещатик, 1 (умовна адреса)", 50.4501, 30.5234, 2, 10),
            NewParking(Guid.Parse("22222222-2222-2222-2222-222222222222"), "Паркінг «Лівобережний»", "Київ", "просп. Броварський, 10 (умовна адреса)", 50.4512, 30.5990, 1, 12),
            NewParking(Guid.Parse("33333333-3333-3333-3333-333333333333"), "Паркінг «Ринок»", "Львів", "пл. Ринок, 5 (умовна адреса)", 49.8419, 24.0315, 1, 8)
        };
        db.Parkings.AddRange(parkings);

        foreach (var p in parkings)
        {
            db.Devices.AddRange(
                new Device { ParkingId = p.Id, Code = "CAM-IN-1", Type = DeviceType.Camera },
                new Device { ParkingId = p.Id, Code = "CAM-OUT-1", Type = DeviceType.Camera },
                new Device { ParkingId = p.Id, Code = "BAR-IN-1", Type = DeviceType.Barrier },
                new Device { ParkingId = p.Id, Code = "BAR-OUT-1", Type = DeviceType.Barrier },
                new Device { ParkingId = p.Id, Code = "DISPLAY-1", Type = DeviceType.Display },
                new Device { ParkingId = p.Id, Code = "GW-1", Type = DeviceType.Gateway });
            foreach (var spot in p.Levels.SelectMany(l => l.Spots))
                db.Devices.Add(new Device { ParkingId = p.Id, Code = $"S-{spot.Code}", Type = DeviceType.Sensor, SpotCode = spot.Code });

            var tariff = new Tariff { ParkingId = p.Id, Name = "Базовий" };
            tariff.Versions.Add(new TariffVersion
            {
                ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                DayRate = p.City == "Львів" ? 30 : 40, NightRate = 20, NightStartHour = 22, NightEndHour = 7,
                FreeMinutes = 15, DailyMax = 400, SubscriptionMonthPrice = 2500, CreatedBy = "seed"
            });
            db.Tariffs.Add(tariff);
        }
        await db.SaveChangesAsync();

        var password = config["Seed:Password"] ?? "Test#12345";
        await CreateUser(users, "admin@smartpark.test", "Системний адміністратор", password, Roles.SysAdmin);
        await CreateUser(users, "operator@smartpark.test", "Оператор ситуаційного центру", password, Roles.Operator);
        await CreateUser(users, "parkadmin@smartpark.test", "Адміністратор паркінгу", password, Roles.ParkingAdmin);
        await CreateUser(users, "tech@smartpark.test", "Технік", password, Roles.Technician);
        await CreateUser(users, "tariff@smartpark.test", "Менеджер з тарифів", password, Roles.TariffManager);
        await CreateUser(users, "finance@smartpark.test", "Фінансист", password, Roles.Finance);
        var driver = await CreateUser(users, "driver@smartpark.test", "Олена Водій", password, Roles.Driver);
        driver.HasPaymentMethod = true;
        await users.UpdateAsync(driver);

        db.Vehicles.Add(new Vehicle { OwnerId = driver.Id, Plate = "AA1234BB", Brand = "Skoda Octavia" });
        db.Vehicles.Add(new Vehicle { OwnerId = driver.Id, Plate = "KA0001EV", Brand = "Nissan Leaf", IsElectric = true });
        db.Subscriptions.Add(new Subscription
        {
            OwnerId = driver.Id, ParkingId = CentralParkingId, Plate = "KA0001EV",
            ValidFrom = DateTime.UtcNow.AddDays(-1), ValidTo = DateTime.UtcNow.AddMonths(1), Price = 2500
        });
        db.Blacklist.Add(new BlacklistEntry { Plate = "BX6666XX", Reason = "Борг за попередні паркування" });
        await db.SaveChangesAsync();
    }

    private static Parking NewParking(Guid id, string name, string city, string address, double lat, double lng,
        int levels, int spotsPerLevel)
    {
        var p = new Parking
        {
            Id = id, Name = name, City = city, Address = address, Latitude = lat, Longitude = lng,
            Status = ParkingStatus.Active
        };
        for (var l = 1; l <= levels; l++)
        {
            var level = new Level { ParkingId = id, Name = $"Рівень {l}", Number = l };
            for (var n = 1; n <= spotsPerLevel; n++)
            {
                var type = n == 1 ? SpotType.Disabled : n <= 3 ? SpotType.Electric : SpotType.Regular;
                level.Spots.Add(new Spot { ParkingId = id, Code = $"{l}-{n:D2}", Type = type });
            }
            p.Levels.Add(level);
        }
        return p;
    }

    private static async Task<AppUser> CreateUser(UserManager<AppUser> users, string email, string name, string password, string role)
    {
        var user = new AppUser { UserName = email, Email = email, FullName = name, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded) throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        await users.AddToRoleAsync(user, role);
        return user;
    }
}
