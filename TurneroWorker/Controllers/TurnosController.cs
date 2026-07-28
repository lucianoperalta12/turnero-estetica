using Microsoft.AspNetCore.Mvc;
using TurneroWorker.Models;
using TurneroWorker.Services;

namespace TurneroWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TurnosController : ControllerBase
{
    private readonly DatabaseService _dbService;

    public TurnosController(DatabaseService dbService)
    {
        _dbService = dbService;
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
}
