using Microsoft.Extensions.Logging;
using TurneroWorker.Configuration;
using TurneroWorker.Models;
using Microsoft.Extensions.Options;

namespace TurneroWorker.Services;

public class ReminderService
{
    private const string RecordatorioMarca = "[RecordatorioEnviado]";

    private readonly GoogleCalendarService _calendarService;
    private readonly WhatsAppService _whatsAppService;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(
        GoogleCalendarService calendarService,
        WhatsAppService whatsAppService,
        ILogger<ReminderService> logger)
    {
        _calendarService = calendarService;
        _whatsAppService = whatsAppService;
        _logger = logger;
    }

    /// <summary>
    /// Orquesta el ciclo completo: lee turnos → envía WhatsApp → marca enviados.
    /// </summary>
    public async Task EjecutarAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== Iniciando ciclo de recordatorios ===");

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
        int errores = 0;

        foreach (var turno in turnos)
        {
            if (cancellationToken.IsCancellationRequested) break;

            _logger.LogInformation("Procesando turno: {Nombre} | {Tel} | {Fecha} {Hora}",
                turno.Nombre, turno.Telefono, turno.Fecha, turno.Hora);

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
                // Solo marcar si el envío fue exitoso
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
            "=== Ciclo finalizado: {Enviados} enviados, {Errores} errores, {Total} total ===",
            enviados, errores, turnos.Count);
    }
}
