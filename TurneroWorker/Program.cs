using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TurneroWorker;
using TurneroWorker.Configuration;
using TurneroWorker.Services;

bool forceRun = args.Contains("--force");

var builder = Host.CreateApplicationBuilder(args);

// ── Configuración ────────────────────────────────────────────────────────────
builder.Services.Configure<AppSettings>(
    builder.Configuration.GetSection("AppSettings"));

// ── HttpClient ───────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();

// ── Servicios (Scoped: se crea un scope por ciclo de ejecución en Worker) ────
builder.Services.AddScoped<GoogleCalendarService>();
builder.Services.AddScoped<WhatsAppService>();
builder.Services.AddScoped<ReminderService>();

// ── Logging ──────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

if (forceRun)
{
    // Modo one-shot: ejecutar ahora y salir
    var app = builder.Build();
    var logger = app.Services.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("=== Modo --force: ejecutando recordatorios ahora ===");

    await using var scope = app.Services.CreateAsyncScope();
    var reminder = scope.ServiceProvider.GetRequiredService<ReminderService>();
    await reminder.EjecutarAsync();

    logger.LogInformation("=== Ejecución forzada completada. Saliendo. ===");
    return;
}

// ── Worker (Singleton: usa IServiceScopeFactory para resolver Scoped) ────────
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
