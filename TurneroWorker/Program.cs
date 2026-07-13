using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TurneroWorker;
using TurneroWorker.Configuration;
using TurneroWorker.Services;

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

// ── Worker (Singleton: usa IServiceScopeFactory para resolver Scoped) ────────
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
