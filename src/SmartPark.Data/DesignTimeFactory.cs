using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SmartPark.Data;

/// <summary>Фабрика для інструменту dotnet-ef (створення міграцій без запуску сервера).</summary>
public class DesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=smartpark;Username=smartpark;Password=smartpark")
            .Options;
        return new AppDbContext(options);
    }
}
