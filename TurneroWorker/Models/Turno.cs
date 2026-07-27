namespace TurneroWorker.Models;

public class Turno
{
    public int Id { get; set; }
    public int ClienteId { get; set; }
    public string Titulo { get; set; } = string.Empty;
    public DateTime FechaInicio { get; set; }
    public DateTime FechaFin { get; set; }
    public string Estado { get; set; } = "confirmado";
    public bool RecordatorioEnviado { get; set; }
    public string? Notas { get; set; }
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    // Propiedad de navegación / Join
    public Cliente? Cliente { get; set; }
}
