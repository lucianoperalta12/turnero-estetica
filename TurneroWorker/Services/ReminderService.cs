using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TurneroWorker.Configuration;
using TurneroWorker.Models;

namespace TurneroWorker.Services;

public class ReminderService
{
    private readonly DatabaseService _dbService;
    private readonly WhatsAppService _whatsAppService;
    private readonly string _adminPhone;
    private readonly ILogger<ReminderService> _logger;

    public ReminderService(
        DatabaseService dbService,
        WhatsAppService whatsAppService,
        IOptions<AppSettings> options,
        ILogger<ReminderService> logger)
    {
        _dbService = dbService;
        _whatsAppService = whatsAppService;
        _adminPhone = options.Value.WhatsApp.AdminPhone;
        _logger = logger;
    }

    /// <summary>
    /// Orquesta el ciclo de recordatorios leyendo los turnos desde la base de datos PostgreSQL,
    /// enviando el WhatsApp mediante WhatsAppService y marcando el turno como enviado.
    /// </summary>
    public async Task EjecutarAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("=== Iniciando ciclo de recordatorios desde PostgreSQL ===");

        // Buscar turnos para la fecha actual (o del día de hoy)
        var hoy = DateTime.Today;
        IEnumerable<Turno> turnosPendientes;

        try
        {
            turnosPendientes = await _dbService.GetTurnosPendientesRecordatorioAsync(hoy);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error crítico al consultar turnos pendientes en PostgreSQL. Ciclo abortado.");
            return;
        }

        var listaTurnos = turnosPendientes.ToList();
        _logger.LogInformation("Turnos pendientes de recordatorio encontrados en PostgreSQL: Count={Count}", listaTurnos.Count);

        if (listaTurnos.Count == 0)
        {
            _logger.LogInformation("Sin turnos pendientes para enviar hoy. Ciclo finalizado.");
            return;
        }

        int enviados = 0;
        int errores = 0;

        foreach (var turno in listaTurnos)
        {
            if (cancellationToken.IsCancellationRequested) break;

            if (turno.Cliente == null || string.IsNullOrWhiteSpace(turno.Cliente.Telefono))
            {
                _logger.LogWarning("Turno Id={TurnoId} para {Titulo} no posee cliente o teléfono válido.", turno.Id, turno.Titulo);
                continue;
            }

            var turnoInfo = new TurnoInfo
            {
                EventId = turno.Id.ToString(),
                Nombre = turno.Cliente.Nombre,
                Telefono = NormalizarTelefono(turno.Cliente.Telefono),
                Fecha = DateOnly.FromDateTime(turno.FechaInicio),
                Hora = turno.FechaInicio.ToString("HH:mm")
            };

            _logger.LogInformation("Procesando turno DB Id={Id}: {Cliente} | {Fecha} {Hora}",
                turno.Id, turnoInfo.Nombre, turnoInfo.Fecha, turnoInfo.Hora);

            WhatsAppSendResult resultado;
            try
            {
                resultado = await _whatsAppService.EnviarRecordatorioAsync(turnoInfo);
                _logger.LogInformation("Resultado WhatsApp Turno Id={Id}: Exitoso={Exitoso}, StatusCode={StatusCode}",
                    turno.Id, resultado.Exitoso, resultado.StatusCode);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Excepción al enviar WhatsApp para el turno Id={Id}", turno.Id);
                errores++;
                continue;
            }

            if (resultado.Exitoso)
            {
                try
                {
                    await _dbService.MarcarRecordatorioEnviadoAsync(turno.Id);
                    enviados++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Envío de WhatsApp exitoso pero falló al actualizar recordatorio_enviado en la BD para turno Id={Id}", turno.Id);
                }
            }
            else
            {
                errores++;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        _logger.LogInformation("=== Ciclo finalizado: {Enviados} enviados, {Errores} errores, {Total} total ===",
            enviados, errores, listaTurnos.Count);
    }

    /// <summary>
    /// Limpia y normaliza números de teléfono al formato internacional (ej. Argentina 549...).
    /// </summary>
    private static string NormalizarTelefono(string rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone)) return string.Empty;

        // Mantener solo dígitos
        var digits = new string(rawPhone.Where(char.IsDigit).ToArray());

        // Remover '0' inicial de código de área (ej: 03564 -> 3564)
        if (digits.StartsWith("0"))
        {
            digits = digits.Substring(1);
        }

        // Si empieza con 15 (móvil local Argentina), quitarlo si viene después del área o ajustarlo
        // Si el número tiene 10 dígitos (ej: 3564562288) y no incluye 54 / 549:
        if (digits.Length == 10)
        {
            digits = "549" + digits;
        }
        else if (digits.Length == 11 && digits.StartsWith("54") && !digits.StartsWith("549"))
        {
            // Ejemplo 54 3564562288 -> agregar el 9 móvil -> 5493564562288
            digits = "549" + digits.Substring(2);
        }

        return digits;
    }
}
