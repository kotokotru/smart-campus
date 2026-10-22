using Campus.Sports.Api.Data;
using Campus.Sports.Api.Telemetry;
using Npgsql;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<MigrationOptions>(builder.Configuration.GetSection(MigrationOptions.Section));

// --- Хранилище: PostgreSQL в production, in-memory — для локального запуска и CI ---
var connectionString = builder.Configuration.GetConnectionString("Campus");
if (!string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
    builder.Services.AddScoped<IBookingRepository, NpgsqlBookingRepository>();
}
else
{
    builder.Services.AddSingleton<IBookingRepository, InMemoryBookingRepository>();
}

// --- OpenTelemetry: единый цифровой след запрос → span → Jira-задача → бронь ---
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r
        .AddService("campus-sports-api", serviceVersion: CampusTelemetry.ServiceVersion)
        .AddAttributes(new KeyValuePair<string, object>[]
        {
            new("deployment.environment", builder.Environment.EnvironmentName),
            new("vcs.repository.ref", Environment.GetEnvironmentVariable("GIT_SHA") ?? "local")
        }))
    .WithTracing(t => t
        .AddSource(CampusTelemetry.SourceName)
        .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
        .AddOtlpExporter());   // адрес коллектора — OTEL_EXPORTER_OTLP_ENDPOINT

var app = builder.Build();

app.UseExceptionHandler();
app.MapHealthChecks("/health/ready");
app.MapControllers();

app.Run();

/// <summary>Нужен для WebApplicationFactory в тестах.</summary>
public partial class Program { }
