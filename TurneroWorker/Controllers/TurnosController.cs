using Microsoft.AspNetCore.Mvc;
using TurneroWorker.Models;
using TurneroWorker.Services;

namespace TurneroWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TurnosController : ControllerBase
{
    private readonly DatabaseService _dbService;
    private readonly WhatsAppService _whatsAppService;

    public TurnosController(DatabaseService dbService, WhatsAppService whatsAppService)
    {
        _dbService = dbService;
        _whatsAppService = whatsAppService;
    }

    [HttpGet]
    public async Task<IActionResult> GetTurnos([FromQuery] DateTime? start, [FromQuery] DateTime? end)
    {
        var inicio = start ?? DateTime.Today.AddDays(-30);
        var fin = end ?? DateTime.Today.AddDays(30);

        var turnos = await _dbService.GetTurnosRangoAsync(inicio, fin);
        
        // Mapear al formato que consume FullCalendar si se desea
        var eventos = turnos.Select(t => new
        {
            id = t.Id,
            title = t.Cliente != null && !string.IsNullOrWhiteSpace(t.Cliente.Nombre) 
                ? $"{t.Cliente.Nombre} - {t.Titulo}" 
                : t.Titulo,
            start = t.FechaInicio.ToString("yyyy-MM-ddTHH:mm:ss"),
            end = t.FechaFin.ToString("yyyy-MM-ddTHH:mm:ss"),
            extendedProps = new
            {
                clienteId = t.ClienteId,
                clienteNombre = t.Cliente?.Nombre,
                clienteTelefono = t.Cliente?.Telefono,
                tituloOriginal = t.Titulo,
                estado = t.Estado,
                recordatorioEnviado = t.RecordatorioEnviado,
                notas = t.Notas
            }
        });

        return Ok(eventos);
    }

    [HttpPost]
    public async Task<IActionResult> CrearTurno([FromBody] Turno turno)
    {
        if (string.IsNullOrWhiteSpace(turno.Titulo))
        {
            return BadRequest("Debe ingresar un título o tratamiento.");
        }

        if (turno.FechaFin <= turno.FechaInicio)
        {
            turno.FechaFin = turno.FechaInicio.AddHours(1);
        }

        // Asegurar que las fechas se traten como hora local (sin conversión UTC)
        turno.FechaInicio = DateTime.SpecifyKind(turno.FechaInicio, DateTimeKind.Unspecified);
        turno.FechaFin = DateTime.SpecifyKind(turno.FechaFin, DateTimeKind.Unspecified);

        var id = await _dbService.CrearTurnoAsync(turno);
        turno.Id = id;
        return Ok(turno);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateTurno(int id, [FromBody] Turno turno)
    {
        turno.Id = id;
        turno.FechaInicio = DateTime.SpecifyKind(turno.FechaInicio, DateTimeKind.Unspecified);
        turno.FechaFin = DateTime.SpecifyKind(turno.FechaFin, DateTimeKind.Unspecified);
        await _dbService.UpdateTurnoAsync(turno);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteTurno(int id)
    {
        await _dbService.DeleteTurnoAsync(id);
        return NoContent();
    }

    /// <summary>Actualiza manualmente el flag recordatorio_enviado.</summary>
    [HttpPatch("{id}/recordatorio")]
    public async Task<IActionResult> PatchRecordatorio(int id, [FromBody] PatchRecordatorioRequest req)
    {
        await _dbService.SetRecordatorioEnviadoAsync(id, req.Enviado);
        return NoContent();
    }

    /// <summary>Fuerza el envío inmediato del recordatorio WhatsApp para un turno.</summary>
    [HttpPost("{id}/enviar-recordatorio")]
    public async Task<IActionResult> EnviarRecordatorio(int id)
    {
        var turno = await _dbService.GetTurnoByIdAsync(id);
        if (turno is null) return NotFound();
        if (turno.Cliente is null || string.IsNullOrWhiteSpace(turno.Cliente.Telefono))
            return BadRequest(new { error = "El turno no tiene cliente o teléfono válido." });

        var telefono = NormalizarTelefono(turno.Cliente.Telefono);
        var turnoInfo = new TurnoInfo
        {
            EventId  = turno.Id.ToString(),
            Nombre   = turno.Cliente.Nombre,
            Telefono = telefono,
            Fecha    = DateOnly.FromDateTime(turno.FechaInicio),
            Hora     = turno.FechaInicio.ToString("HH:mm")
        };

        var resultado = await _whatsAppService.EnviarRecordatorioAsync(turnoInfo);
        if (resultado.Exitoso)
        {
            await _dbService.MarcarRecordatorioEnviadoAsync(id);
            return Ok(new { ok = true, messageId = resultado.MessageId });
        }

        return StatusCode(502, new
        {
            ok    = false,
            error = resultado.Error ?? resultado.RawResponse,
            httpStatusCode = resultado.StatusCode
        });
    }

    private static string NormalizarTelefono(string rawPhone)
    {
        var digits = new string(rawPhone.Where(char.IsDigit).ToArray());
        if (digits.StartsWith("0")) digits = digits[1..];
        if (digits.Length == 10) digits = "549" + digits;
        else if (digits.Length == 11 && digits.StartsWith("54") && !digits.StartsWith("549"))
            digits = "549" + digits[2..];
        return digits;
    }
}
