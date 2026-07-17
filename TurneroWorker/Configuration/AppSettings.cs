namespace TurneroWorker.Configuration;

public class AppSettings
{
    public GoogleCalendarConfig GoogleCalendar { get; set; } = new();
    public GoogleSheetsConfig GoogleSheets { get; set; } = new();
    public WhatsAppConfig WhatsApp { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();
}

public class GoogleCalendarConfig
{
    public string CalendarId { get; set; } = string.Empty;
    public string CredentialsFilePath { get; set; } = string.Empty;
}

public class GoogleSheetsConfig
{
    public string SpreadsheetId { get; set; } = string.Empty;
    /// <summary>
    /// Rango de la hoja. Columna A = Nombre, Columna B = Teléfono.
    /// Empieza en A1 asumiendo que no tiene encabezado.
    /// </summary>
    public string Range { get; set; } = "'Hoja 1'!A1:B";
}

public class WhatsAppConfig
{
    /// <summary>URL del microservicio Node.js, p.ej. "http://localhost:3000/send"</summary>
    public string ServiceUrl { get; set; } = "http://localhost:3000/send";
    /// <summary>Número del administrador que recibe alertas cuando un paciente no se encuentra en el directorio.</summary>
    public string AdminPhone { get; set; } = string.Empty;
}

public class ScheduleConfig
{
    public string TimeZone { get; set; } = "America/Argentina/Buenos_Aires";
    public List<string> ExecutionTimes { get; set; } = new();
}
