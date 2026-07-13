namespace TurneroWorker.Configuration;

public class AppSettings
{
    public GoogleCalendarConfig GoogleCalendar { get; set; } = new();
    public WhatsAppConfig WhatsApp { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();
}

public class GoogleCalendarConfig
{
    public string CalendarId { get; set; } = string.Empty;
    public string CredentialsFilePath { get; set; } = string.Empty;
}

public class WhatsAppConfig
{
    /// <summary>
    /// URL base de Evolution API, p.ej. "http://localhost:8080"
    /// </summary>
    public string BaseUrl { get; set; } = "http://localhost:8080";

    /// <summary>
    /// API Key configurada en Evolution API (AUTHENTICATION_API_KEY del docker-compose)
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Nombre de la instancia de WhatsApp creada en Evolution API
    /// </summary>
    public string Instance { get; set; } = "turnero";
}

public class ScheduleConfig
{
    public string TimeZone { get; set; } = "America/Argentina/Buenos_Aires";
    public List<string> ExecutionTimes { get; set; } = new() { "09:00", "18:00" };
}
