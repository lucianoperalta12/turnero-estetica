namespace TurneroWorker.Models;

public class TurnoInfo
{
    /// <summary>ID del evento en Google Calendar.</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>Nombre del paciente/cliente (título del evento).</summary>
    public string Nombre { get; set; } = string.Empty;

    /// <summary>
    /// Teléfono en formato E.164 con prefijo Argentina: 549XXXXXXXXXX.
    /// Ya convertido desde el formato de entrada "XXXX XXXXXX".
    /// </summary>
    public string Telefono { get; set; } = string.Empty;

    /// <summary>Fecha del turno.</summary>
    public DateOnly Fecha { get; set; }

    /// <summary>Hora del turno formateada, p.ej. "10:30".</summary>
    public string Hora { get; set; } = string.Empty;
}
