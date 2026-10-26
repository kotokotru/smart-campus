using Campus.Dormitory.Api.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddDbContext<DormitoryDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Dormitory") ?? "Data Source=dormitory.db"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    scope.ServiceProvider.GetRequiredService<DormitoryDbContext>().Database.EnsureCreated();

app.MapControllers();
app.Run();

// нужен для WebApplicationFactory<Program> в проекте тестов
public partial class Program;
