using Microsoft.EntityFrameworkCore;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Events;
using SmartPark.Server.Modules.Catalog;
using SmartPark.Server.Modules.Payment;
using SmartPark.Server.Modules.Pricing;

namespace SmartPark.Server.Modules.Session;

/// <summary>
/// Модуль Session: рішення про в’їзд/виїзд, відкриття й закриття сесій паркування.
/// Критичний шлях синхронний: edge-шлюз → Session → Booking/Pricing/Payment в одному процесі.
/// </summary>
public class SessionService
{
    public const double MinConfidence = 0.7;
    public static readonly TimeSpan BookingEarlyArrival = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan PaidExitWindow = TimeSpan.FromMinutes(15);

    private readonly AppDbContext _db;
    private readonly ICatalogService _catalog;
    private readonly IPricingService _pricing;
    private readonly IPaymentService _payments;
    private readonly IOutbox _outbox;
    private readonly IConfiguration _config;

    public SessionService(AppDbContext db, ICatalogService catalog, IPricingService pricing,
        IPaymentService payments, IOutbox outbox, IConfiguration config)
    {
        _db = db;
        _catalog = catalog;
        _pricing = pricing;
        _payments = payments;
        _outbox = outbox;
        _config = config;
    }

    /// <summary>Нормалізація номера: верхній регістр, без пробілів, кирилиця → латиниця (АВЕІКМНОРСТХ).</summary>
    public static string NormalizePlate(string plate)
    {
        const string cyr = "АВЕІКМНОРСТХ";
        const string lat = "ABEIKMHOPCTX";
        var chars = plate.ToUpperInvariant().Where(char.IsLetterOrDigit)
            .Select(c => cyr.IndexOf(c) is var i and >= 0 ? lat[i] : c);
        return new string(chars.ToArray());
    }

    /// <summary>Сценарій UC05 – в’їзд за розпізнаванням номерного знака.</summary>
    public async Task<GateDecision> ProcessEntryAsync(VehicleDetectedRequest r, CancellationToken ct = default)
    {
        // Ідемпотентність: повторна подія з тим самим EventId повертає те саме рішення.
        var existing = await _db.Sessions.AsNoTracking().FirstOrDefaultAsync(s => s.EntryEventId == r.EventId, ct);
        if (existing is not null) return new GateDecision(GateDecisionType.Allow, "Повторна подія", existing.Id);

        if (!await _catalog.IsActiveAsync(r.ParkingId, ct))
            return new GateDecision(GateDecisionType.Deny, "Паркінг не працює");

        if (string.IsNullOrWhiteSpace(r.Plate) || r.Confidence < MinConfidence)
        {
            _outbox.Add(new EntryNeedsManualCheck(r.ParkingId, r.Plate, r.Confidence, r.LaneCode));
            await _db.SaveChangesAsync(ct);
            return new GateDecision(GateDecisionType.ManualCheck, "Номер не розпізнано – зверніться до оператора");
        }

        var plate = NormalizePlate(r.Plate);
        if (await _db.Blacklist.AnyAsync(b => b.Plate == plate, ct))
            return new GateDecision(GateDecisionType.Deny, "В’їзд заборонено");

        if (await _db.Sessions.AnyAsync(s => s.Plate == plate && s.Status == SessionStatus.Active, ct))
            return new GateDecision(GateDecisionType.Deny, "Автомобіль уже на паркінгу");

        var now = r.DetectedAt.AsUtc();
        var vehicle = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Plate == plate, ct);
        var session = new ParkingSession
        {
            ParkingId = r.ParkingId, Plate = plate, OwnerId = vehicle?.OwnerId,
            EntryTime = now, EntryEventId = r.EventId, Mode = EntryMode.Guest
        };

        var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.ParkingId == r.ParkingId && b.Plate == plate &&
            b.Status == BookingStatus.Confirmed && b.StartTime - BookingEarlyArrival <= now && b.EndTime > now, ct);
        var subscription = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(x => x.ParkingId == r.ParkingId &&
            x.Plate == plate && x.ValidFrom <= now && x.ValidTo > now, ct);

        if (booking is not null)
        {
            booking.Status = BookingStatus.Activated;
            session.Mode = EntryMode.Booking;
            session.BookingId = booking.Id;
        }
        else if (subscription is not null)
        {
            session.Mode = EntryMode.Subscription;
            session.SubscriptionId = subscription.Id;
        }
        else
        {
            var occ = await _catalog.GetOccupancyAsync(r.ParkingId, ct);
            var reservedForBookings = await _db.Bookings.CountAsync(b => b.ParkingId == r.ParkingId &&
                b.Status == BookingStatus.Confirmed && b.StartTime - BookingEarlyArrival <= now && b.EndTime > now, ct);
            var activeSessions = await _db.Sessions.CountAsync(s => s.ParkingId == r.ParkingId && s.Status == SessionStatus.Active, ct);
            var capacity = occ.Total - occ.Unavailable;
            if (activeSessions + reservedForBookings >= capacity)
                return new GateDecision(GateDecisionType.Deny, "Вільних місць немає");
        }

        _db.Sessions.Add(session);
        _outbox.Add(new SessionStarted(session.Id, session.ParkingId, plate, session.OwnerId, session.Mode.ToString()));
        await OpenBarrierAsync(r.ParkingId, r.LaneCode, $"В’їзд {plate} ({session.Mode})", "system", ct);
        await _db.SaveChangesAsync(ct);

        return new GateDecision(GateDecisionType.Allow, $"Ласкаво просимо, {plate}!", session.Id);
    }

    /// <summary>Сценарій UC06 – виїзд і оплата.</summary>
    public async Task<GateDecision> ProcessExitAsync(VehicleDetectedRequest r, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(r.Plate) || r.Confidence < MinConfidence)
        {
            _outbox.Add(new EntryNeedsManualCheck(r.ParkingId, r.Plate, r.Confidence, r.LaneCode));
            await _db.SaveChangesAsync(ct);
            return new GateDecision(GateDecisionType.ManualCheck, "Номер не розпізнано – зверніться до оператора");
        }

        var plate = NormalizePlate(r.Plate);
        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.ParkingId == r.ParkingId && s.Plate == plate &&
            (s.Status == SessionStatus.Active || s.Status == SessionStatus.AwaitingPayment), ct);
        if (session is null)
        {
            var closed = await _db.Sessions.AsNoTracking().AnyAsync(s => s.ExitEventId == r.EventId, ct);
            return closed
                ? new GateDecision(GateDecisionType.Allow, "Повторна подія")
                : new GateDecision(GateDecisionType.ManualCheck, "Сесію не знайдено – зверніться до оператора");
        }

        var now = r.DetectedAt.AsUtc();
        var amount = await CalculateAmountAsync(session, now, ct);

        // Гість уже оплатив за QR-кодом або в паркоматі – є 15 хвилин на виїзд.
        var paidRecently = session.PaidAt is not null && now - session.PaidAt <= PaidExitWindow;
        if (!paidRecently) session.Amount = amount;

        if (amount > 0 && !paidRecently)
        {
            if (session.OwnerId is null)
            {
                session.Status = SessionStatus.AwaitingPayment;
                await _db.SaveChangesAsync(ct);
                return PaymentRequired(session, amount);
            }

            var payment = await _payments.ChargeAsync(session.OwnerId, PaymentPurpose.Session, session.Id, amount,
                $"Паркування {plate}", ct);
            if (payment.Status != PaymentStatus.Succeeded)
            {
                session.Status = SessionStatus.AwaitingPayment;
                await _db.SaveChangesAsync(ct);
                return PaymentRequired(session, amount, payment.FailureReason);
            }
            session.PaymentId = payment.Id;
            session.PaidAt = now;
        }

        await CloseAsync(session, now, r.EventId, SessionStatus.Closed, ct);
        await OpenBarrierAsync(r.ParkingId, r.LaneCode, $"Виїзд {plate}", "system", ct);
        await _db.SaveChangesAsync(ct);
        return new GateDecision(GateDecisionType.Allow, "Щасливої дороги!", session.Id, session.Amount);
    }

    /// <summary>Вартість сесії з урахуванням абонемента і бронювання (доплата лише за час поза бронюванням).</summary>
    public async Task<decimal> CalculateAmountAsync(ParkingSession session, DateTime exitTime, CancellationToken ct)
    {
        if (session.Mode == EntryMode.Subscription) return 0m;
        if (session.BookingId is not null)
        {
            var booking = await _db.Bookings.AsNoTracking().FirstAsync(b => b.Id == session.BookingId, ct);
            return exitTime <= booking.EndTime ? 0m : await _pricing.CalculateAsync(session.ParkingId, booking.EndTime, exitTime, ct);
        }
        return await _pricing.CalculateAsync(session.ParkingId, session.EntryTime, exitTime, ct);
    }

    /// <summary>Ручний в’їзд оператором, якщо номер не розпізнано (обов’язкова причина – пишеться в аудит).</summary>
    public async Task<GateDecision> ManualEntryAsync(Guid parkingId, string plate, string laneCode, string reason,
        string operatorName, CancellationToken ct = default)
    {
        var request = new VehicleDetectedRequest(Guid.NewGuid(), parkingId, plate, 1.0, laneCode, DateTime.UtcNow);
        var decision = await ProcessEntryAsync(request, ct);
        if (decision.Decision == GateDecisionType.Allow && decision.SessionId is not null)
        {
            var session = await _db.Sessions.FirstAsync(s => s.Id == decision.SessionId, ct);
            if (session.Mode == EntryMode.Guest) session.Mode = EntryMode.Manual;
            await _db.SaveChangesAsync(ct);
        }
        return decision;
    }

    /// <summary>Досилання подій, прийнятих edge-шлюзом в автономному режимі.</summary>
    public async Task<SyncBatchResult> SyncOfflineAsync(SyncBatchRequest batch, CancellationToken ct = default)
    {
        int accepted = 0, duplicates = 0, failed = 0;
        foreach (var e in batch.Events.OrderBy(x => x.OccurredAt))
        {
            if (await _db.ProcessedEvents.AnyAsync(p => p.EventId == e.EventId && p.Consumer == "edge-sync", ct))
            {
                duplicates++;
                continue;
            }

            var plate = NormalizePlate(e.Plate);
            var time = e.OccurredAt.AsUtc();
            if (e.Kind == "entry")
            {
                if (!await _db.Sessions.AnyAsync(s => s.Plate == plate && s.Status == SessionStatus.Active, ct))
                {
                    var vehicle = await _db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.Plate == plate, ct);
                    var s = new ParkingSession
                    {
                        ParkingId = batch.ParkingId, Plate = plate, OwnerId = vehicle?.OwnerId,
                        EntryTime = time, EntryEventId = e.EventId, Mode = EntryMode.Offline
                    };
                    // Абонемент чи бронювання враховуються й для в’їзду в автономному режимі.
                    var booking = await _db.Bookings.FirstOrDefaultAsync(b => b.ParkingId == batch.ParkingId &&
                        b.Plate == plate && b.Status == BookingStatus.Confirmed &&
                        b.StartTime - BookingEarlyArrival <= time && b.EndTime > time, ct);
                    var subscription = await _db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(x =>
                        x.ParkingId == batch.ParkingId && x.Plate == plate && x.ValidFrom <= time && x.ValidTo > time, ct);
                    if (booking is not null)
                    {
                        booking.Status = BookingStatus.Activated;
                        s.Mode = EntryMode.Booking;
                        s.BookingId = booking.Id;
                    }
                    else if (subscription is not null)
                    {
                        s.Mode = EntryMode.Subscription;
                        s.SubscriptionId = subscription.Id;
                    }
                    _db.Sessions.Add(s);
                    _outbox.Add(new SessionStarted(s.Id, s.ParkingId, plate, s.OwnerId, s.Mode.ToString()));
                }
            }
            else if (e.Kind == "exit")
            {
                var s = await _db.Sessions.FirstOrDefaultAsync(x => x.ParkingId == batch.ParkingId && x.Plate == plate &&
                    (x.Status == SessionStatus.Active || x.Status == SessionStatus.AwaitingPayment), ct);
                if (s is null)
                {
                    failed++;
                    continue;
                }
                var amount = await CalculateAmountAsync(s, time, ct);
                s.Amount = amount;
                var status = SessionStatus.Closed;
                if (amount > 0 && s.OwnerId is not null)
                {
                    var payment = await _payments.ChargeAsync(s.OwnerId, PaymentPurpose.Session, s.Id, amount,
                        $"Паркування {plate} (офлайн)", ct);
                    if (payment.Status == PaymentStatus.Succeeded) s.PaymentId = payment.Id;
                    else status = SessionStatus.Unpaid;
                }
                else if (amount > 0) status = SessionStatus.Unpaid; // гість виїхав без оплати – борг
                await CloseAsync(s, time, e.EventId, status, ct);
            }

            _db.ProcessedEvents.Add(new ProcessedEvent { EventId = e.EventId, Consumer = "edge-sync" });
            await _db.SaveChangesAsync(ct);
            accepted++;
        }
        return new SyncBatchResult(accepted, duplicates, failed);
    }

    /// <summary>Локальний кеш для автономного режиму edge-шлюзу: дозволені номери та чорний список.</summary>
    public async Task<EdgeCache> BuildCacheAsync(Guid parkingId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var subs = await _db.Subscriptions.Where(s => s.ParkingId == parkingId && s.ValidTo > now)
            .Select(s => s.Plate).ToListAsync(ct);
        var bookings = await _db.Bookings.Where(b => b.ParkingId == parkingId && b.Status == BookingStatus.Confirmed &&
            b.EndTime > now && b.StartTime < now.AddDays(1)).Select(b => b.Plate).ToListAsync(ct);
        var blacklist = await _db.Blacklist.Select(b => b.Plate).ToListAsync(ct);
        return new EdgeCache(parkingId, subs.Concat(bookings).Distinct().ToList(), blacklist, now);
    }

    private Task CloseAsync(ParkingSession s, DateTime exitTime, Guid exitEventId, SessionStatus status, CancellationToken ct)
    {
        s.ExitTime = exitTime;
        s.ExitEventId = exitEventId;
        s.Status = status;
        if (s.BookingId is not null)
        {
            var booking = _db.Bookings.Local.FirstOrDefault(b => b.Id == s.BookingId)
                          ?? _db.Bookings.Find(s.BookingId);
            if (booking is not null) booking.Status = BookingStatus.Completed;
        }
        _outbox.Add(new SessionClosed(s.Id, s.ParkingId, s.Plate, s.OwnerId, s.Amount, status.ToString()));
        return Task.CompletedTask;
    }

    private async Task OpenBarrierAsync(Guid parkingId, string laneCode, string reason, string issuedBy, CancellationToken ct)
    {
        var barrier = await _db.Devices.AsNoTracking().FirstOrDefaultAsync(d => d.ParkingId == parkingId &&
            d.Type == DeviceType.Barrier && d.Code == laneCode, ct);
        var code = barrier?.Code ?? laneCode;
        _db.DeviceCommands.Add(new DeviceCommand
        {
            ParkingId = parkingId, DeviceCode = code, Command = "open", Reason = reason, IssuedBy = issuedBy
        });
        _outbox.Add(new BarrierOpened(parkingId, code, reason));
    }

    private GateDecision PaymentRequired(ParkingSession s, decimal amount, string? reason = null)
    {
        var baseUrl = _config["Client:BaseUrl"] ?? "http://localhost:8080";
        var message = reason is null ? $"До сплати {amount:0.00} грн" : $"Оплата не пройшла: {reason}";
        return new GateDecision(GateDecisionType.PaymentRequired, message, s.Id, amount, $"{baseUrl}/pay/{s.Id}");
    }
}
