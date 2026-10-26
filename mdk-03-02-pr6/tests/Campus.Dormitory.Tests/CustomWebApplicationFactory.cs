using Campus.Dormitory.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Campus.Dormitory.Tests;

/// <summary>
/// Поднимает API в памяти и подменяет файловую базу на SQLite In-Memory.
/// Каждый экземпляр фабрики получает своё открытое соединение, то есть свою отдельную пустую базу:
/// база живёт, пока открыто соединение, и исчезает при Dispose.
/// </summary>
public class CustomWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    public CustomWebApplicationFactory() => _connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            // убираем регистрацию рабочей базы dormitory.db
            services.RemoveAll<DbContextOptions<DormitoryDbContext>>();
            services.AddDbContext<DormitoryDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _connection.Dispose();
    }
}
