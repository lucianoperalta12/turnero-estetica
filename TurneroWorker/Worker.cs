using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using TurneroWorker.Configuration;
using TurneroWorker.Services;

namespace TurneroWorker;

public class Worker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScheduleConfig _scheduleConfig;
    private readonly TimeZoneInfo _tz;
    private readonly ILogger<Worker> _logger;

    public Worker(
        IServiceScopeFactory scopeFactory,
        IOptions<AppSettings> options,
        ILogger<Worker> logger)
    {
        _scopeFactory = scopeFactory;
        _scheduleConfig = options.Value.Schedule;
        _logger = logger;
        _tz = GetTimeZoneInfo(_scheduleConfig.TimeZone);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TurneroWorker iniciado. Zona horaria: {TZ}", _tz.Id);
        _logger.LogInformation("Horarios de ejecución: {Times}", string.Join(", ", _scheduleConfig.ExecutionTimes));

        while (!stoppingToken.IsCancellationRequested)
        {
            var (proximo, delay) = CalcularProximaEjecucion();

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested) break;

            // Crear scope por ciclo para servicios Scoped
            await using var scope = _scopeFactory.CreateAsyncScope();
            var reminderService = scope.ServiceProvider.GetRequiredService<ReminderService>();
            await reminderService.EjecutarAsync(stoppingToken);

            // Desconectar WhatsApp al terminar el ciclo para no quedar "en línea"
            var whatsAppService = scope.ServiceProvider.GetRequiredService<WhatsAppService>();
            await whatsAppService.DesconectarAsync();
        }

        _logger.LogInformation("TurneroWorker detenido.");
    }

    /// <summary>
    /// Calcula cuánto tiempo falta hasta el próximo horario configurado (zona: <see cref="_tz"/>).
    /// Los ExecutionTimes duplicados se ignoran. El delay se calcula en UTC para que
    /// Task.Delay sea correcto independientemente de la zona del sistema operativo.
    /// </summary>
    private (DateTime proxLocal, TimeSpan delay) CalcularProximaEjecucion()
    {
        var ahoraSystem = DateTimeOffset.Now;
        var ahoraUtc    = ahoraSystem.UtcDateTime;
        var ahoraLocal  = TimeZoneInfo.ConvertTimeFromUtc(ahoraUtc, _tz);

        // Deduplicar, parsear y ordenar los horarios configurados
        var horasValidas = _scheduleConfig.ExecutionTimes
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(s => TimeOnly.TryParse(s, out var t) ? (TimeOnly?)t : null)
            .Where(t => t.HasValue)
            .Select(t => t!.Value)
            .OrderBy(t => t)
            .ToList();

        if (horasValidas.Count == 0)
        {
            _logger.LogWarning("No se pudieron parsear los ExecutionTimes. Reintentando en 1 hora.");
            return (ahoraLocal.AddHours(1), TimeSpan.FromHours(1));
        }

        // Primer horario futuro de hoy; si todos pasaron, el primero de mañana
        DateTime? proxLocal = null;
        foreach (var hora in horasValidas)
        {
            var candidato = ahoraLocal.Date.Add(hora.ToTimeSpan());
            if (candidato > ahoraLocal)
            {
                proxLocal = candidato;
                break;
            }
        }
        proxLocal ??= ahoraLocal.Date.AddDays(1).Add(horasValidas[0].ToTimeSpan());

        // Delay basado en UTC para que Task.Delay sea exacto
        var proxUtc = TimeZoneInfo.ConvertTimeToUtc(proxLocal.Value, _tz);
        var delay   = proxUtc - ahoraUtc;
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(1);

        _logger.LogInformation(
            "[Scheduler] Hora sistema: {Sistema} | Hora {TZ}: {Local} | Próxima ejecución: {Proximo} (en {Delay:hh\\:mm\\:ss})",
            ahoraSystem.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            _tz.Id,
            ahoraLocal.ToString("yyyy-MM-dd HH:mm:ss"),
            proxLocal.Value.ToString("yyyy-MM-dd HH:mm"),
            delay);

        return (proxLocal.Value, delay);
    }

    private static TimeZoneInfo GetTimeZoneInfo(string tzId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            var windowsId = tzId switch
            {
                "America/Argentina/Buenos_Aires" => "Argentina Standard Time",
                "America/Buenos_Aires" => "Argentina Standard Time",
                _ => tzId
            };
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }
}
