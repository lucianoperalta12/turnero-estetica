using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TurneroWorker.Configuration;
using TurneroWorker.Models;

namespace TurneroWorker.Services;

public class ReminderService
{
    private const string RecordatorioMarca = "[RecordatorioEnviado]";

    private readonly GoogleCalendarService _calendarService;
    private readonly GoogleSheetsService _sheetsService;
    private readonly WhatsAppService _whatsAppService;
    private readonly string _adminPhone;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(
        GoogleCalendarService calendarService,
        GoogleSheetsService sheetsService,
        WhatsAppService whatsAppService,
        IOptions<AppSettings> options,
        ILogger<ReminderService> logger)
    {
        _calendarService = calendarService;
        _sheetsService = sheetsService;
        _whatsAppService = whatsAppService;
        _adminPhone = options.Value.WhatsApp.AdminPhone;
        _logger = logger;
    }

    /// <summary>
    /// Orquesta el ciclo completo: carga directorio → lee turnos → resuelve teléfonos → envía WhatsApp → marca enviados.
    /// Si un paciente no se encuentra en el directorio, notifica al administrador.
    /// </summary>
    public async Task EjecutarAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== Iniciando ciclo de recordatorios ===");

        // 1. Cargar el directorio de contactos desde Google Sheets (una sola vez por ciclo)
        Dictionary<string, string> directorio;
        try
        {
            directorio = await _sheetsService.GetDirectorioAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error crítico al cargar el directorio de Google Sheets. Ciclo abortado.");
            return;
        }

        // 2. Obtener los turnos de hoy desde Google Calendar
        List<TurnoInfo> turnos;
        try
        {
            turnos = await _calendarService.GetTurnosDeHoyAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error crítico al obtener turnos. Ciclo abortado.");
            return;
        }

        if (turnos.Count == 0)
        {
            _logger.LogInformation("Sin turnos para hoy. Ciclo finalizado.");
            return;
        }

        int enviados = 0;
        int noEncontrados = 0;
        int errores = 0;

        foreach (var turno in turnos)
        {
            if (cancellationToken.IsCancellationRequested) break;

            _logger.LogInformation("Procesando turno: {Nombre} | {Fecha} {Hora}",
                turno.Nombre, turno.Fecha, turno.Hora);

            // 3. Resolver teléfono desde el directorio (case-insensitive)
            var clave = turno.Nombre.Trim().ToLowerInvariant();
            if (!directorio.TryGetValue(clave, out var telefono))
            {
                _logger.LogWarning("Paciente '{Nombre}' NO encontrado en el directorio de Google Sheets. Notificando al admin.", turno.Nombre);
                noEncontrados++;

                // Notificar al administrador si está configurado
                if (!string.IsNullOrEmpty(_adminPhone))
                {
                    await EnviarAlertaAdminAsync(turno, cancellationToken);
                }

                continue;
            }

            turno.Telefono = telefono;

            // 4. Enviar recordatorio por WhatsApp
            WhatsAppSendResult resultado;
            try
            {
                resultado = await _whatsAppService.EnviarRecordatorioAsync(turno);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción inesperada al enviar WhatsApp para evento {EventId}", turno.EventId);
                errores++;
                continue;
            }

            if (resultado.Exitoso)
            {
                // 5. Marcar el evento como enviado en Calendar
                try
                {
                    var descActual = await _calendarService.ObtenerDescripcionEventoAsync(turno.EventId) ?? string.Empty;
                    if (!descActual.Contains(RecordatorioMarca, StringComparison.OrdinalIgnoreCase))
                    {
                        var nuevaDesc = descActual.TrimEnd() + "\n" + RecordatorioMarca;
                        await _calendarService.ActualizarDescripcionEventoAsync(turno.EventId, nuevaDesc);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Envío OK pero no se pudo marcar evento {EventId}", turno.EventId);
                }
                enviados++;
            }
            else
            {
                errores++;
            }

            // Pequeña pausa entre envíos para no saturar la API
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        _logger.LogInformation(
            "=== Ciclo finalizado: {Enviados} enviados, {NoEncontrados} no encontrados en directorio, {Errores} errores, {Total} total ===",
            enviados, noEncontrados, errores, turnos.Count);
    }

    private async Task EnviarAlertaAdminAsync(TurnoInfo turno, CancellationToken cancellationToken)
    {
        var mensaje =
            $"⚠️ Alerta Turnero\n\n" +
            $"No se pudo enviar el recordatorio para *{turno.Nombre}* ({turno.Hora} hs) " +
            $"porque no fue encontrado en el directorio de Google Sheets.\n\n" +
            $"Por favor verificá que el nombre esté cargado correctamente en la planilla.";

        var turnoAdmin = new TurnoInfo
        {
            EventId = turno.EventId,
            Nombre = "Administrador",
            Telefono = _adminPhone,
            Fecha = turno.Fecha,
            Hora = turno.Hora
        };

        try
        {
            // Enviamos la alerta directamente al admin usando el servicio de WhatsApp con el mensaje personalizado
            await _whatsAppService.EnviarMensajeDirectoAsync(_adminPhone, mensaje);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo enviar la alerta al administrador ({AdminPhone})", _adminPhone);
        }
    }
}
