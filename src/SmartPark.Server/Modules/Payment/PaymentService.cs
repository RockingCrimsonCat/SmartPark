using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SmartPark.Contracts;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure.Events;
using SmartPark.Server.Modules.Pricing;
using PaymentEntity = SmartPark.Data.Models.Payment;

namespace SmartPark.Server.Modules.Payment;

/// <summary>Публічний інтерфейс модуля Payment.</summary>
public interface IPaymentService
{
    /// <summary>Списання коштів. Зберігає платіж у контексті (без SaveChanges – у транзакції викликача).</summary>
    Task<PaymentEntity> ChargeAsync(Guid? userId, PaymentPurpose purpose, Guid referenceId, decimal amount,
        string description, CancellationToken ct = default);
}

/// <summary>
/// Емуляція платіжного провайдера: повертає «оплачено» або «відмова» з налаштовуваною
/// ймовірністю (Payment:FailureRate). Дані карток у системі не зберігаються.
/// </summary>
public class FakePaymentProvider
{
    private readonly double _failureRate;

    public FakePaymentProvider(IConfiguration config) =>
        _failureRate = config.GetValue("Payment:FailureRate", 0.1);

    public (bool ok, string? reason) Authorize(decimal amount)
    {
        if (amount <= 0) return (true, null);
        if (Random.Shared.NextDouble() < _failureRate) return (false, "Відмова банку-емітента (емуляція)");
        return (true, null);
    }
}

public class PaymentService : IPaymentService
{
    private readonly AppDbContext _db;
    private readonly FakePaymentProvider _provider;
    private readonly ReceiptService _receipts;
    private readonly IOutbox _outbox;

    public PaymentService(AppDbContext db, FakePaymentProvider provider, ReceiptService receipts, IOutbox outbox)
    {
        _db = db;
        _provider = provider;
        _receipts = receipts;
        _outbox = outbox;
    }

    public async Task<PaymentEntity> ChargeAsync(Guid? userId, PaymentPurpose purpose, Guid referenceId, decimal amount,
        string description, CancellationToken ct = default)
    {
        var payment = new PaymentEntity { UserId = userId, Purpose = purpose, ReferenceId = referenceId, Amount = amount };

        if (userId is not null && amount > 0)
        {
            var user = await _db.Users.FindAsync(new object[] { userId.Value }, ct);
            if (user is { HasPaymentMethod: false })
            {
                payment.Status = PaymentStatus.Failed;
                payment.FailureReason = "Не прив’язано спосіб оплати";
            }
        }

        if (payment.FailureReason is null)
        {
            var (ok, reason) = _provider.Authorize(amount);
            payment.Status = ok ? PaymentStatus.Succeeded : PaymentStatus.Failed;
            payment.FailureReason = reason;
        }

        if (payment.Status == PaymentStatus.Succeeded)
            payment.ReceiptPath = _receipts.Generate(payment, description);

        _db.Payments.Add(payment);
        _outbox.Add(new PaymentCompleted(payment.Id, userId, purpose.ToString(), referenceId, amount,
            payment.Status == PaymentStatus.Succeeded));
        return payment;
    }
}

/// <summary>Формування чека у форматі PDF (замість фіскального чека ПРРО).</summary>
public class ReceiptService
{
    private readonly string _folder;

    public ReceiptService(IConfiguration config)
    {
        _folder = config["Storage:ReceiptsPath"] ?? Path.Combine(AppContext.BaseDirectory, "receipts");
        Directory.CreateDirectory(_folder);
    }

    public string Generate(PaymentEntity payment, string description)
    {
        var path = Path.Combine(_folder, $"{payment.Id}.pdf");
        var local = LocalTime.FromUtc(payment.CreatedAt);

        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.A6);
            page.Margin(16);
            page.DefaultTextStyle(t => t.FontSize(9).FontFamily("DejaVu Sans"));
            page.Content().Column(col =>
            {
                col.Spacing(4);
                col.Item().Text("ТОВ «СмартПарк Нетворк»").Bold().FontSize(11);
                col.Item().Text("Квитанція про оплату (не є фіскальним чеком)");
                col.Item().LineHorizontal(0.5f);
                col.Item().Text($"Призначення: {description}");
                col.Item().Text($"Дата: {local:dd.MM.yyyy HH:mm}");
                col.Item().Text($"Сума: {payment.Amount:0.00} грн").Bold().FontSize(12);
                col.Item().Text($"Номер платежу: {payment.Id}").FontSize(7);
                col.Item().LineHorizontal(0.5f);
                col.Item().Text("Дякуємо, що користуєтеся SmartPark!");
            });
        })).GeneratePdf(path);

        return path;
    }
}
