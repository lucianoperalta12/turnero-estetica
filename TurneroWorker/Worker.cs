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

            _logger.LogInformation("Próxima ejecución: {Proximo} (en {Delay:hh\\:mm\\:ss})",
                proximo.ToString("yyyy-MM-dd HH:mm"), delay);

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
        }

        _logger.LogInformation("TurneroWorker detenido.");
    }

    /// <summary>
    /// Calcula cuánto tiempo falta hasta el próximo horario configurado (en zona horaria local).
    /// Si todos los horarios de hoy ya pasaron, calcula el primero del día siguiente.
    /// </summary>
    private (DateTime proximoUtc, TimeSpan delay) CalcularProximaEjecucion()
    {
        var ahoraUtc = DateTime.UtcNow;
        var ahoraLocal = TimeZoneInfo.ConvertTimeFromUtc(ahoraUtc, _tz);

        var candidatos = new List<DateTime>();

        foreach (var timeStr in _scheduleConfig.ExecutionTimes)
        {
            if (!TimeOnly.TryParse(timeStr, out var hora)) continue;

            // Candidato hoy
            var candidatoHoy = ahoraLocal.Date.Add(hora.ToTimeSpan());
            if (candidatoHoy > ahoraLocal)
                candidatos.Add(candidatoHoy);

            // Candidato mañana (para cuando todos los de hoy pasaron)
            candidatos.Add(ahoraLocal.Date.AddDays(1).Add(hora.ToTimeSpan()));
        }

        if (candidatos.Count == 0)
        {
            _logger.LogWarning("No se pudieron parsear los ExecutionTimes. Reintentando en 1 hora.");
            return (ahoraUtc.AddHours(1), TimeSpan.FromHours(1));
        }

        var proxLocal = candidatos.Min();
        var proxUtc = TimeZoneInfo.ConvertTimeToUtc(proxLocal, _tz);
        var delay = proxUtc - ahoraUtc;

        if (delay <= TimeSpan.Zero)
            delay = TimeSpan.FromSeconds(1);

        return (proxUtc, delay);
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
