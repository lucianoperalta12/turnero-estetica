using Microsoft.AspNetCore.Mvc;
using TurneroWorker.Models;
using TurneroWorker.Services;

namespace TurneroWorker.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ClientesController : ControllerBase
{
    private readonly DatabaseService _dbService;

    public ClientesController(DatabaseService dbService)
    {
        _dbService = dbService;
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
}
