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

            var summary = ev.Summary?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(summary))
            {
                _logger.LogWarning("Evento vacío sin título. Omitiendo.");
                continue;
            }

            // Parsear título: "Nombre Apellido Teléfono" (ej: "María Gómez 3564 562288" o "María Gómez 3564562288")
            var (nombre, telefonoRaw) = ParsearTitulo(summary);

            if (string.IsNullOrEmpty(telefonoRaw))
            {
                _logger.LogWarning("Evento '{Summary}' no contiene un número de teléfono en el título. Omitiendo.", summary);
                continue;
            }

            // Validar y normalizar el número de teléfono al formato internacional argentino (549...)
            var telefonoNormalizado = NormalizarTelefonoArgentino(telefonoRaw);
            if (string.IsNullOrEmpty(telefonoNormalizado))
            {
                _logger.LogWarning("Número de teléfono '{TelefonoRaw}' en evento '{Summary}' no es válido para Argentina. Omitiendo.", telefonoRaw, summary);
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
                Telefono = telefonoNormalizado,
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
    /// Divide el título en Nombre y Teléfono.
    /// Asume que la parte final son dígitos y espacios que componen el teléfono.
    /// Ej: "Luciano Peralta 3564 562288" -> ("Luciano Peralta", "3564 562288")
    /// </summary>
    private static (string Nombre, string Telefono) ParsearTitulo(string summary)
    {
        var partes = summary.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (partes.Length < 2) return (summary, string.Empty);

        // Intentar encontrar el punto de corte desde atrás hacia adelante
        // Buscamos cuántos tokens finales son exclusivamente números
        int numTokensTelefono = 0;
        for (int i = partes.Length - 1; i >= 0; i--)
        {
            var token = partes[i];
            // Si el token es solo números (o contiene guiones/caracteres comunes de teléfono)
            var esNumero = token.All(c => char.IsDigit(c) || c == '-' || c == '+' || c == '(' || c == ')');
            
            if (esNumero)
            {
                numTokensTelefono++;
            }
            else
            {
                break;
            }
        }

        if (numTokensTelefono == 0)
        {
            return (summary, string.Empty);
        }

        var nombre = string.Join(" ", partes.Take(partes.Length - numTokensTelefono)).Trim();
        var telefono = string.Join("", partes.Skip(partes.Length - numTokensTelefono)).Trim();

        return (nombre, telefono);
    }

    /// <summary>
    /// Limpia y normaliza el teléfono al formato internacional de Argentina (549 + prefijo de área sin el 0 + número sin el 15).
    /// </summary>
    private static string? NormalizarTelefonoArgentino(string raw)
    {
        // Limpiar caracteres no numéricos
        var soloDigitos = new string(raw.Where(char.IsDigit).ToArray());

        if (string.IsNullOrEmpty(soloDigitos)) return null;

        // Si ya tiene el prefijo de país completo 549 seguido de 10 dígitos (total 13 caracteres)
        if (soloDigitos.StartsWith("549") && soloDigitos.Length == 13)
        {
            return soloDigitos;
        }

        // Si empieza con 54 pero le falta el '9' de celular internacional (ej. 54 3564 562288 tiene largo 12)
        if (soloDigitos.StartsWith("54") && soloDigitos.Length == 12)
        {
            return "549" + soloDigitos[2..];
        }

        // Si es un número local de 10 dígitos (ej. 3564562288 o 0356415562288)
        // Quitamos el '0' del código de área si estuviera
        if (soloDigitos.StartsWith('0'))
        {
            soloDigitos = soloDigitos[1..];
        }

        // Si contiene el '15' de celular local (ej. prefijo 3564 + 15 + número 562288 -> largo 12)
        // Intentar remover el '15'
        if (soloDigitos.Length == 12 && soloDigitos.Substring(4, 2) == "15")
        {
            soloDigitos = soloDigitos.Remove(4, 2);
        }

        // El número estándar sin 0 y sin 15 debe tener 10 dígitos
        if (soloDigitos.Length == 10)
        {
            return "549" + soloDigitos;
        }

        // Si ya está normalizado de alguna otra forma o tiene largo no esperado, intentamos retornarlo tal cual si parece válido
        if (soloDigitos.Length >= 10 && soloDigitos.Length <= 13)
        {
            if (!soloDigitos.StartsWith("549"))
            {
                if (soloDigitos.StartsWith("54"))
                {
                    return "549" + soloDigitos[2..];
                }
                return "549" + soloDigitos;
            }
            return soloDigitos;
        }

        return null; // No se pudo determinar un formato argentino válido
    }

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
