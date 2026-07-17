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
    private readonly IOptionsMonitor<AppSettings> _options;
    private readonly ILogger<Worker> _logger;
    private readonly object _scheduleChangedLock = new();
    private TaskCompletionSource _scheduleChanged = CreateScheduleChangedSignal();

    public Worker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<AppSettings> options,
        ILogger<Worker> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduleConfig = _options.CurrentValue.Schedule;
        var tz = GetTimeZoneInfo(scheduleConfig.TimeZone);
        using var changeRegistration = _options.OnChange(_ => SignalScheduleChanged());

        _logger.LogInformation("TurneroWorker iniciado. Zona horaria: {TZ}", tz.Id);
        _logger.LogInformation("Horarios de ejecucion: {Times}", string.Join(", ", scheduleConfig.ExecutionTimes));

        while (!stoppingToken.IsCancellationRequested)
        {
            var (proximo, delay) = CalcularProximaEjecucion();

            try
            {
                var delayTask = Task.Delay(delay, stoppingToken);
                var scheduleChangedTask = WaitForScheduleChangeAsync();
                var completedTask = await Task.WhenAny(delayTask, scheduleChangedTask);

                if (completedTask == scheduleChangedTask)
                {
                    ResetScheduleChangedSignal();
                    continue;
                }

                await delayTask;
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (stoppingToken.IsCancellationRequested) break;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var reminderService = scope.ServiceProvider.GetRequiredService<ReminderService>();
            await reminderService.EjecutarAsync(stoppingToken);
        }

        _logger.LogInformation("TurneroWorker detenido.");
    }

    /// <summary>
    /// Calcula cuanto tiempo falta hasta el proximo horario configurado.
    /// Los ExecutionTimes duplicados se ignoran. El delay se calcula en UTC para que
    /// Task.Delay sea correcto independientemente de la zona del sistema operativo.
    /// </summary>
    private (DateTime proxLocal, TimeSpan delay) CalcularProximaEjecucion()
    {
        var scheduleConfig = _options.CurrentValue.Schedule;
        var tz = GetTimeZoneInfo(scheduleConfig.TimeZone);
        var ahoraSystem = DateTimeOffset.Now;
        var ahoraUtc = ahoraSystem.UtcDateTime;
        var ahoraLocal = TimeZoneInfo.ConvertTimeFromUtc(ahoraUtc, tz);

        var horasValidas = scheduleConfig.ExecutionTimes
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

        var proxUtc = TimeZoneInfo.ConvertTimeToUtc(proxLocal.Value, tz);
        var delay = proxUtc - ahoraUtc;
        if (delay <= TimeSpan.Zero) delay = TimeSpan.FromSeconds(1);

        _logger.LogInformation(
            "[Scheduler] Hora sistema: {Sistema} | Hora {TZ}: {Local} | Proxima ejecucion: {Proximo} (en {Delay:hh\\:mm\\:ss})",
            ahoraSystem.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            tz.Id,
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

    private Task WaitForScheduleChangeAsync()
    {
        lock (_scheduleChangedLock)
        {
            return _scheduleChanged.Task;
        }
    }

    private void SignalScheduleChanged()
    {
        lock (_scheduleChangedLock)
        {
            _scheduleChanged.TrySetResult();
        }
    }

    private void ResetScheduleChangedSignal()
    {
        lock (_scheduleChangedLock)
        {
            if (_scheduleChanged.Task.IsCompleted)
            {
                _scheduleChanged = CreateScheduleChangedSignal();
            }
        }
    }

    private static TaskCompletionSource CreateScheduleChangedSignal()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
