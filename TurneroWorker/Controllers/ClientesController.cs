using Microsoft.AspNetCore.Mvc;
using TurneroWorker.Models;
using TurneroWorker.Services;

namespace TurneroWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ClientesController : ControllerBase
{
    private readonly DatabaseService _dbService;
    private readonly WhatsAppService _whatsAppService;

    public ClientesController(DatabaseService dbService, WhatsAppService whatsAppService)
    {
        _dbService = dbService;
        _whatsAppService = whatsAppService;
    }

    [HttpGet]
    public async Task<IActionResult> GetClientes([FromQuery] string? busqueda)
    {
        var clientes = await _dbService.GetClientesAsync(busqueda);
        return Ok(clientes);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetCliente(int id)
    {
        var cliente = await _dbService.GetClienteByIdAsync(id);
        if (cliente == null) return NotFound();
        return Ok(cliente);
    }

    [HttpPost]
    public async Task<IActionResult> CrearCliente([FromBody] Cliente cliente)
    {
        if (string.IsNullOrWhiteSpace(cliente.Nombre) || string.IsNullOrWhiteSpace(cliente.Telefono))
        {
            return BadRequest("El nombre y teléfono son obligatorios.");
        }

        var id = await _dbService.CrearClienteAsync(cliente);
        cliente.Id = id;
        return CreatedAtAction(nameof(GetCliente), new { id = cliente.Id }, cliente);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateCliente(int id, [FromBody] Cliente cliente)
    {
        cliente.Id = id;
        await _dbService.UpdateClienteAsync(cliente);
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteCliente(int id)
    {
        await _dbService.DeleteClienteAsync(id);
        return NoContent();
    }

    public record DifusionRequest(string Mensaje);

    [HttpPost("difusion")]
    public async Task<IActionResult> EnviarDifusion([FromBody] DifusionRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Mensaje))
            return BadRequest("El mensaje no puede estar vacío.");

        if (req.Mensaje.Length > 1000)
            return BadRequest("El mensaje no puede superar los 1000 caracteres.");

        var clientes = await _dbService.GetClientesAsync(null);
        var lista = clientes.Where(c => !string.IsNullOrWhiteSpace(c.Telefono)).ToList();

        if (lista.Count == 0)
            return Ok(new { enviados = 0, errores = 0, total = 0 });

        // Disparar en background para no bloquear la respuesta HTTP
        _ = _whatsAppService.EnviarDifusionAsync(lista, req.Mensaje);

        return Accepted(new { total = lista.Count, mensaje = $"Difusión iniciada para {lista.Count} clientes." });
    }
}
