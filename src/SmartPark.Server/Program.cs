using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using QuestPDF.Infrastructure;
using Serilog;
using SmartPark.Data;
using SmartPark.Data.Models;
using SmartPark.Server.Infrastructure;
using SmartPark.Server.Infrastructure.Events;
using SmartPark.Server.Infrastructure.Realtime;
using SmartPark.Server.Infrastructure.Security;
using SmartPark.Server.Modules.Booking;
using SmartPark.Server.Modules.Catalog;
using SmartPark.Server.Modules.Device;
using SmartPark.Server.Modules.Payment;
using SmartPark.Server.Modules.Pricing;
using SmartPark.Server.Modules.Session;

var builder = WebApplication.CreateBuilder(args);

// Журналювання: Serilog у консоль і файл.
builder.Host.UseSerilog((ctx, cfg) => cfg
    .ReadFrom.Configuration(ctx.Configuration)
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(ctx.Configuration["Storage:LogsPath"] ?? "logs", "smartpark-.log"),
        rollingInterval: RollingInterval.Day));

QuestPDF.Settings.License = LicenseType.Community;

// ---------- Дані ----------
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddScoped<AuditInterceptor>();
builder.Services.AddDbContext<AppDbContext>((sp, o) => o
    .UseNpgsql(builder.Configuration.GetConnectionString("Default"))
    .AddInterceptors(sp.GetRequiredService<AuditInterceptor>()));
builder.Services.AddMemoryCache();

// ---------- Ідентифікація ----------
builder.Services.AddIdentityCore<AppUser>(o =>
    {
        o.Password.RequiredLength = 8;
        o.Password.RequireNonAlphanumeric = false;
        o.Lockout.MaxFailedAccessAttempts = 5;
        o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        o.User.RequireUniqueEmail = true;
    })
    .AddRoles<IdentityRole<Guid>>()
    .AddEntityFrameworkStores<AppDbContext>();

var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddSingleton(jwt);
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = jwt.Issuer,
        ValidAudience = jwt.Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
    // SignalR передає токен у рядку запиту.
    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            var token = ctx.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                ctx.Token = token;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorization();

// ---------- Модулі ----------
builder.Services.AddScoped<ICatalogService, CatalogService>();
builder.Services.AddScoped<IPricingService, PricingService>();
builder.Services.AddSingleton<FakePaymentProvider>();
builder.Services.AddSingleton<ReceiptService>();
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddScoped<DeviceService>();

// ---------- Внутрішня шина подій + Outbox ----------
builder.Services.AddScoped<IOutbox, Outbox>();
builder.Services.AddSingleton<InProcessEventBus>();
builder.Services.AddSingleton<OutboxProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OutboxProcessor>());
builder.Services.AddHostedService<EventDispatcher>();
builder.Services.AddHostedService<DeviceMonitor>();
builder.Services.AddHostedService<BookingExpiryService>();
RegisterEventHandlers(builder.Services);

// ---------- Реальний час ----------
builder.Services.AddSignalR();
builder.Services.AddSingleton<IRealtime, SignalRRealtime>();

// ---------- API ----------
builder.Services.AddControllers().AddJsonOptions(o =>
{
    o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.JsonSerializerOptions.Converters.Add(new UtcDateTimeConverter());
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SmartPark API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT", In = ParameterLocation.Header
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] =
            Array.Empty<string>()
    });
    c.CustomSchemaIds(t => t.FullName!.Replace("+", "."));
});
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(builder.Configuration.GetSection("Cors:Origins").Get<string[]>() ?? new[] { "http://localhost:5173" })
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

// Міграції та початкові дані при старті.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            await db.Database.MigrateAsync();
            break;
        }
        catch (Exception ex) when (attempt < 10)
        {
            Log.Warning(ex, "Database not ready, retry {Attempt}", attempt);
            await Task.Delay(TimeSpan.FromSeconds(3));
        }
    }
    await DataSeeder.SeedAsync(scope.ServiceProvider, app.Configuration);
}

app.UseSerilogRequestLogging();
app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapHub<EventsHub>("/hubs/events");
app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();

// Реєстрація всіх IEventHandler<T> зі збірки (один клас може обробляти кілька подій).
static void RegisterEventHandlers(IServiceCollection services)
{
    var handlerTypes = typeof(Program).Assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false });
    foreach (var type in handlerTypes)
    foreach (var itf in type.GetInterfaces().Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>)))
        services.AddScoped(itf, type);
}

public partial class Program;

/// <summary>Усі дати в API приводяться до UTC (вимога Npgsql для timestamptz).</summary>
public class UtcDateTimeConverter : JsonConverter<DateTime>
{
    public override DateTime Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert,
        System.Text.Json.JsonSerializerOptions options) => reader.GetDateTime().AsUtc();

    public override void Write(System.Text.Json.Utf8JsonWriter writer, DateTime value,
        System.Text.Json.JsonSerializerOptions options) => writer.WriteStringValue(value.AsUtc());
}
