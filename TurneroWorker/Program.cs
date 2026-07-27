using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TurneroWorker;
using TurneroWorker.Configuration;
using TurneroWorker.Services;

var builder = WebApplication.CreateBuilder(args);

// ── Configuración ────────────────────────────────────────────────────────────
builder.Services.Configure<AppSettings>(
    builder.Configuration.GetSection("AppSettings"));

// ── HttpClient ───────────────────────────────────────────────────────────────
builder.Services.AddHttpClient();

// ── Servicios Web & Base de Datos ────────────────────────────────────────────
builder.Services.AddRazorPages();
builder.Services.AddControllers();

builder.Services.AddSingleton<DatabaseService>();
builder.Services.AddScoped<WhatsAppService>();
builder.Services.AddScoped<ReminderService>();

// ── Logging ──────────────────────────────────────────────────────────────────
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// ── Worker (Hosted Service en segundo plano) ─────────────────────────────────
builder.Services.AddHostedService<Worker>();

var app = builder.Build();

// Inicializar tablas en PostgreSQL al arrancar la app
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    var dbService = scope.ServiceProvider.GetRequiredService<DatabaseService>();
    try
    {
        logger.LogInformation("Verificando / Inicializando esquema 'turnero' en PostgreSQL...");
        await dbService.InicializarTablasAsync();
        logger.LogInformation("Base de datos inicializada correctamente.");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "No se pudo auto-inicializar la BD (verificar conexión PostgreSQL).");
    }
}

app.UseStaticFiles();
app.UseRouting();

app.MapRazorPages();
app.MapControllers();

// Redireccionar raíz '/' a '/turnos'
app.MapGet("/", () => Results.Redirect("/turnos"));

app.Run();
