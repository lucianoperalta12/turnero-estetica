using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TurneroWorker.Configuration;
using TurneroWorker.Models;

namespace TurneroWorker.Services;

public class GoogleCalendarService
{
    private const string RecordatorioMarca = "[RecordatorioEnviado]";

    private readonly GoogleCalendarConfig _config;
    private readonly TimeZoneInfo _tz;
    private readonly ILogger<GoogleCalendarService> _logger;

    public GoogleCalendarService(IOptions<AppSettings> options, ILogger<GoogleCalendarService> logger)
    {
        _config = options.Value.GoogleCalendar;
        _logger = logger;

        var tzId = options.Value.Schedule.TimeZone;
        _tz = GetTimeZoneInfo(tzId);
    }

    // ── Servicio Calendar ────────────────────────────────────────────────────

    private CalendarService BuildCalendarService()
    {
        GoogleCredential credential;

        using var stream = new FileStream(_config.CredentialsFilePath, FileMode.Open, FileAccess.Read);
        credential = GoogleCredential
            .FromStream(stream)
            .CreateScoped(CalendarService.Scope.Calendar);

        return new CalendarService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "TurneroWorker"
        });
    }

    // ── Consulta de turnos ───────────────────────────────────────────────────

    /// <summary>
    /// Devuelve los turnos del día de HOY que NO tienen la marca de recordatorio enviado.
    /// </summary>
    public async Task<List<TurnoInfo>> GetTurnosDeHoyAsync()
    {
        var service = BuildCalendarService();

        var ahoraLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _tz);
        var hoy = ahoraLocal.Date;

        // Rango en UTC para el día de hoy
        var timeMin = TimeZoneInfo.ConvertTimeToUtc(hoy, _tz);
        var timeMax = TimeZoneInfo.ConvertTimeToUtc(hoy.AddDays(1), _tz);

        _logger.LogInformation("Consultando Calendar para el día de HOY: {Fecha} (UTC {Min} → {Max})",
            hoy.ToString("yyyy-MM-dd"), timeMin, timeMax);

        var request = service.Events.List(_config.CalendarId);
        request.TimeMinDateTimeOffset = timeMin;
        request.TimeMaxDateTimeOffset = timeMax;
        request.SingleEvents = true;
        request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

        Events events;
        try
        {
            events = await request.ExecuteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al consultar Google Calendar");
            return new List<TurnoInfo>();
        }

        var turnos = new List<TurnoInfo>();

        foreach (var ev in events.Items ?? Enumerable.Empty<Event>())
        {
            // Ignorar cancelados
            if (ev.Status == "cancelled") continue;

            var descripcion = ev.Description ?? string.Empty;

            // Ignorar ya marcados
            if (descripcion.Contains(RecordatorioMarca, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Evento '{Summary}' ya tiene marca. Omitiendo.", ev.Summary);
                continue;
            }

            // El título del evento es el nombre del paciente (sin teléfono)
            var nombre = ev.Summary?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(nombre))
            {
                _logger.LogWarning("Evento sin título. Omitiendo.");
                continue;
            }

            // Determinar fecha/hora local del evento
            DateTimeOffset inicio;
            if (ev.Start.DateTimeDateTimeOffset.HasValue)
                inicio = TimeZoneInfo.ConvertTime(ev.Start.DateTimeDateTimeOffset.Value, _tz);
            else
            {
                // Evento de todo el día
                if (!DateOnly.TryParse(ev.Start.Date, out var soloFecha)) continue;
                inicio = new DateTimeOffset(soloFecha.ToDateTime(TimeOnly.MinValue), _tz.GetUtcOffset(soloFecha.ToDateTime(TimeOnly.MinValue)));
            }

            turnos.Add(new TurnoInfo
            {
                EventId = ev.Id,
                Nombre = nombre,
                Telefono = string.Empty, // Se resolverá desde Google Sheets en ReminderService
                Fecha = DateOnly.FromDateTime(inicio.DateTime),
                Hora = inicio.ToString("HH:mm")
            });
        }

        _logger.LogInformation("Turnos encontrados para hoy ({Fecha}): {Count}", hoy.ToString("yyyy-MM-dd"), turnos.Count);
        return turnos;
    }

    // ── Actualización de descripción ─────────────────────────────────────────

    /// <summary>
    /// Reemplaza la descripción completa de un evento.
    /// Uso típico: agregar [RecordatorioEnviado] u otras marcas al texto existente.
    /// </summary>
    public async Task ActualizarDescripcionEventoAsync(string eventId, string nuevaDescripcion)
    {
        var service = BuildCalendarService();

        var patch = new Event { Description = nuevaDescripcion };
        try
        {
            await service.Events.Patch(patch, _config.CalendarId, eventId).ExecuteAsync();
            _logger.LogInformation("Descripción del evento {EventId} actualizada", eventId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo actualizar la descripción del evento {EventId}", eventId);
        }
    }

    /// <summary>
    /// Devuelve la descripción actual de un evento, o null si no se pudo obtener.
    /// </summary>
    public async Task<string?> ObtenerDescripcionEventoAsync(string eventId)
    {
        var service = BuildCalendarService();
        try
        {
            var ev = await service.Events.Get(_config.CalendarId, eventId).ExecuteAsync();
            return ev.Description;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo obtener la descripción del evento {EventId}", eventId);
            return null;
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Obtiene TimeZoneInfo compatible con Windows y Linux/Mac (IANA → Windows).
    /// </summary>
    private static TimeZoneInfo GetTimeZoneInfo(string tzId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            // En Windows, intentar con el alias conocido
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
