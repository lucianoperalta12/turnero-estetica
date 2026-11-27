namespace TurneroWorker.Configuration;

public class AppSettings
{
    public WhatsAppConfig WhatsApp { get; set; } = new();
    public ScheduleConfig Schedule { get; set; } = new();
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
