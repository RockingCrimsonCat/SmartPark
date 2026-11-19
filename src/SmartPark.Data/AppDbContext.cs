using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using SmartPark.Data.Models;

namespace SmartPark.Data;

/// <summary>
/// Єдиний контекст бази даних. Межі модулів збережено на рівні схем PostgreSQL:
/// кожен модуль працює лише зі своїми таблицями.
/// </summary>
public class AppDbContext : IdentityDbContext<AppUser, IdentityRole<Guid>, Guid>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    // catalog
    public DbSet<Parking> Parkings => Set<Parking>();
    public DbSet<Level> Levels => Set<Level>();
    public DbSet<Spot> Spots => Set<Spot>();
    // session
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<ParkingSession> Sessions => Set<ParkingSession>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<BlacklistEntry> Blacklist => Set<BlacklistEntry>();
    // booking
    public DbSet<Booking> Bookings => Set<Booking>();
    // pricing
    public DbSet<Tariff> Tariffs => Set<Tariff>();
    public DbSet<TariffVersion> TariffVersions => Set<TariffVersion>();
    // payment
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<Refund> Refunds => Set<Refund>();
    // device
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<Telemetry> Telemetry => Set<Telemetry>();
    public DbSet<DeviceCommand> DeviceCommands => Set<DeviceCommand>();
    // incident
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentComment> IncidentComments => Set<IncidentComment>();
    // notification
    public DbSet<Notification> Notifications => Set<Notification>();
    // identity (додатково до таблиць ASP.NET Identity)
    public DbSet<UserParkingScope> UserParkingScopes => Set<UserParkingScope>();
    // audit, outbox
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<OutboxEvent> OutboxEvents => Set<OutboxEvent>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();

    protected override void ConfigureConventions(ModelConfigurationBuilder builder)
    {
        // Перелічення зберігаються як рядки – зручно читати дані та звіти.
        builder.Properties<Enum>().HaveConversion<string>().HaveMaxLength(32);
        builder.Properties<decimal>().HavePrecision(12, 2);
    }

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        // identity
        b.Entity<AppUser>().ToTable("Users", "identity");
        b.Entity<IdentityRole<Guid>>().ToTable("Roles", "identity");
        b.Entity<IdentityUserRole<Guid>>().ToTable("UserRoles", "identity");
        b.Entity<IdentityUserClaim<Guid>>().ToTable("UserClaims", "identity");
        b.Entity<IdentityUserLogin<Guid>>().ToTable("UserLogins", "identity");
        b.Entity<IdentityUserToken<Guid>>().ToTable("UserTokens", "identity");
        b.Entity<IdentityRoleClaim<Guid>>().ToTable("RoleClaims", "identity");
        b.Entity<UserParkingScope>(e =>
        {
            e.ToTable("UserParkingScopes", "identity");
            e.HasKey(x => new { x.UserId, x.ParkingId });
        });

        // catalog
        b.Entity<Parking>(e =>
        {
            e.ToTable("Parkings", "catalog");
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Address).HasMaxLength(250);
            e.HasMany(x => x.Levels).WithOne(x => x.Parking).HasForeignKey(x => x.ParkingId);
        });
        b.Entity<Level>(e =>
        {
            e.ToTable("Levels", "catalog");
            e.HasIndex(x => new { x.ParkingId, x.Number }).IsUnique();
            e.HasMany(x => x.Spots).WithOne(x => x.Level).HasForeignKey(x => x.LevelId);
        });
        b.Entity<Spot>(e =>
        {
            e.ToTable("Spots", "catalog");
            e.Property(x => x.Code).HasMaxLength(20);
            e.HasIndex(x => new { x.ParkingId, x.Code }).IsUnique();
            e.HasIndex(x => new { x.ParkingId, x.Status });
        });

        // session
        b.Entity<Vehicle>(e =>
        {
            e.ToTable("Vehicles", "session");
            e.Property(x => x.Plate).HasMaxLength(16);
            e.HasIndex(x => x.Plate).IsUnique();
            e.HasIndex(x => x.OwnerId);
        });
        b.Entity<ParkingSession>(e =>
        {
            e.ToTable("ParkingSessions", "session");
            e.Property(x => x.Plate).HasMaxLength(16);
            e.HasIndex(x => x.EntryEventId).IsUnique();
            e.HasIndex(x => new { x.Plate, x.Status });
            e.HasIndex(x => new { x.ParkingId, x.EntryTime });
        });
        b.Entity<Subscription>(e =>
        {
            e.ToTable("Subscriptions", "session");
            e.HasIndex(x => new { x.Plate, x.ParkingId });
        });
        b.Entity<BlacklistEntry>(e =>
        {
            e.ToTable("Blacklist", "session");
            e.HasIndex(x => x.Plate).IsUnique();
        });

        // booking
        b.Entity<Booking>(e =>
        {
            e.ToTable("Bookings", "booking");
            e.HasIndex(x => new { x.ParkingId, x.StartTime, x.EndTime });
            e.HasIndex(x => new { x.Plate, x.Status });
        });

        // pricing
        b.Entity<Tariff>(e =>
        {
            e.ToTable("Tariffs", "pricing");
            e.HasMany(x => x.Versions).WithOne().HasForeignKey(x => x.TariffId);
        });
        b.Entity<TariffVersion>(e =>
        {
            e.ToTable("TariffVersions", "pricing");
            e.HasIndex(x => new { x.TariffId, x.ValidFrom }).IsUnique();
        });

        // payment
        b.Entity<Payment>(e =>
        {
            e.ToTable("Payments", "payment");
            e.HasIndex(x => x.ReferenceId);
            e.HasIndex(x => x.UserId);
        });
        b.Entity<Refund>().ToTable("Refunds", "payment");

        // device
        b.Entity<Device>(e =>
        {
            e.ToTable("Devices", "device");
            e.HasIndex(x => new { x.ParkingId, x.Code }).IsUnique();
        });
        b.Entity<Telemetry>(e =>
        {
            e.ToTable("Telemetry", "device");
            e.HasIndex(x => new { x.DeviceId, x.Timestamp });
            e.HasIndex(x => x.Timestamp);
        });
        b.Entity<DeviceCommand>(e =>
        {
            e.ToTable("DeviceCommands", "device");
            e.HasIndex(x => new { x.ParkingId, x.Status });
        });

        // incident
        b.Entity<Incident>(e =>
        {
            e.ToTable("Incidents", "incident");
            e.HasIndex(x => new { x.Status, x.Priority });
            e.HasMany(x => x.Comments).WithOne().HasForeignKey(x => x.IncidentId);
        });
        b.Entity<IncidentComment>().ToTable("IncidentComments", "incident");

        // notification
        b.Entity<Notification>(e =>
        {
            e.ToTable("Notifications", "notification");
            e.HasIndex(x => new { x.UserId, x.IsRead });
        });

        // audit
        b.Entity<AuditLog>(e =>
        {
            e.ToTable("AuditLog", "audit");
            e.Property(x => x.OldValues).HasColumnType("jsonb");
            e.Property(x => x.NewValues).HasColumnType("jsonb");
            e.HasIndex(x => new { x.EntityName, x.EntityId });
            e.HasIndex(x => x.Timestamp);
        });

        // outbox
        b.Entity<OutboxEvent>(e =>
        {
            e.ToTable("OutboxEvents", "outbox");
            e.Property(x => x.Payload).HasColumnType("jsonb");
            e.HasIndex(x => x.ProcessedAt);
        });
        b.Entity<ProcessedEvent>(e =>
        {
            e.ToTable("ProcessedEvents", "outbox");
            e.HasKey(x => new { x.EventId, x.Consumer });
        });
    }
}
