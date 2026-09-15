using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TurneroWorker.Configuration;
using TurneroWorker.Models;

namespace TurneroWorker.Services;

public class WhatsAppService
{
    private readonly WhatsAppConfig _config;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<WhatsAppService> _logger;

    public WhatsAppService(
        IOptions<AppSettings> options,
        IHttpClientFactory httpClientFactory,
        ILogger<WhatsAppService> logger)
    {
        _config = options.Value.WhatsApp;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    /// <summary>
    /// Envía un recordatorio de turno llamando al microservicio local de Baileys.
    /// </summary>
    public async Task<WhatsAppSendResult> EnviarRecordatorioAsync(TurnoInfo turno)
    {
        var url = _config.ServiceUrl;
        var mensaje = turno.TipoServicio == "depilacion"
            ? BuildMensajeDepilacion(turno)
            : BuildMensaje(turno);

        var payload = new
        {
            phone = turno.Telefono,
            message = mensaje
        };

        var json = JsonSerializer.Serialize(payload);
        _logger.LogInformation("WhatsAppService EnviarRecordatorio: Url='{Url}', Nombre='{Nombre}', Telefono='{Telefono}', MensajeLength={MensajeLength}, Payload='{Json}'", url, turno.Nombre, turno.Telefono, mensaje.Length, json);

        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error de red al conectar con el servicio local de WhatsApp en {Url}", url);
            return WhatsAppSendResult.ErrorRed(ex.Message);
        }

        var body = await response.Content.ReadAsStringAsync();
        var statusCode = (int)response.StatusCode;
        _logger.LogInformation("WhatsAppService respuesta recordatorio: StatusCode={StatusCode}, IsSuccess={IsSuccess}, Body='{Body}'", statusCode, response.IsSuccessStatusCode, body);

        if (response.IsSuccessStatusCode)
        {
            var messageId = ExtraerMessageId(body);
            _logger.LogInformation("WhatsApp enviado exitosamente a {Nombre} ({Tel}). ID: {MsgId}",
                turno.Nombre, turno.Telefono, messageId ?? "n/a");
            return WhatsAppSendResult.Ok(messageId, statusCode, body);
        }

        _logger.LogError("Error en servicio de WhatsApp [{Status}] para {Nombre} ({Tel}): {Body}",
            statusCode, turno.Nombre, turno.Telefono, body);
        return WhatsAppSendResult.Fallo(statusCode, body);
    }

    /// <summary>
    /// Envía un mensaje de texto libre a un número específico.
    /// Usado principalmente para alertas al administrador.
    /// </summary>
    public async Task<WhatsAppSendResult> EnviarMensajeDirectoAsync(string phone, string mensaje)
    {
        var url = _config.ServiceUrl;
        var payload = new { phone, message = mensaje };

        var json = JsonSerializer.Serialize(payload);
        _logger.LogInformation("WhatsAppService EnviarMensajeDirecto: Url='{Url}', Phone='{Phone}', MensajeLength={MensajeLength}, Payload='{Json}'", url, phone, mensaje.Length, json);

        using var client = _httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error de red al enviar mensaje directo a {Phone}", phone);
            return WhatsAppSendResult.ErrorRed(ex.Message);
        }

        var body = await response.Content.ReadAsStringAsync();
        var statusCode = (int)response.StatusCode;
        _logger.LogInformation("WhatsAppService respuesta directa: StatusCode={StatusCode}, IsSuccess={IsSuccess}, Body='{Body}'", statusCode, response.IsSuccessStatusCode, body);

        if (response.IsSuccessStatusCode)
        {
            _logger.LogInformation("Mensaje directo enviado a {Phone}", phone);
            return WhatsAppSendResult.Ok(ExtraerMessageId(body), statusCode, body);
        }

        _logger.LogError("Error al enviar mensaje directo a {Phone} [{Status}]: {Body}", phone, statusCode, body);
        return WhatsAppSendResult.Fallo(statusCode, body);
    }

    private static string BuildMensaje(TurnoInfo turno)
    {
        var nombre = turno.Nombre
    .Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

        var variante = Random.Shared.Next(5);
        return variante switch
        {
            0 => $"Hola {nombre} 👋\n\n" +
                 $"Te recordamos que hoy tenés turno en la Estética a las {turno.Hora}.\n\n" +
                 "Si necesitás reprogramarlo, escribinos por este medio.\n\n" +
                 "¡Te esperamos! 💖",

            1 => $"¡Hola, {nombre}! 😊\n\n" +
                 $"Hoy te esperamos en la Estética. Tu turno está programado para las {turno.Hora}.\n\n" +
                 "Si querés modificar el horario, respondé este mensaje.\n\n" +
                 "¡Nos vemos pronto! ✨",

            2 => $"Buen día, {nombre} 🌸\n\n" +
                 $"Queríamos confirmarte tu turno de hoy a las {turno.Hora}.\n\n" +
                 "Si no podés asistir o necesitás cambiarlo, avisanos con este mensaje.\n\n" +
                 "¡Que tengas un lindo día! 💅",

            3 => $"Hola {nombre}.\n\n" +
                 $"Este es un recordatorio de tu turno de hoy en la Estética, previsto para las {turno.Hora}.\n\n" +
                 "Ante cualquier inconveniente, escribinos para ayudarte con el cambio.\n\n" +
                 "¡Te esperamos!",

            _ => $"¡Hola! 👋\n\n" +
                 $"{nombre}, te esperamos hoy a las {turno.Hora} para tu turno.\n\n" +
                 "Si necesitás reprogramarlo, respondé este WhatsApp.\n\n" +
                 "¡Nos vemos!"
        };
    }

    private static readonly System.Globalization.CultureInfo EsArCulture = System.Globalization.CultureInfo.GetCultureInfo("es-AR");

    /// <summary>
    /// Mensaje de recordatorio para turnos de depilación definitiva (se envía el día anterior a las 13 hs).
    /// Elige aleatoriamente entre las 3 plantillas de "Centro de Belleza SV".
    /// </summary>
    private static string BuildMensajeDepilacion(TurnoInfo turno)
    {
        var partes = (turno.Nombre ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var nombre = partes.Length > 0 ? partes[0] : string.Empty;
        var saludoNombre = string.IsNullOrEmpty(nombre) ? string.Empty : $" {nombre}";

        var diaSemana = EsArCulture.DateTimeFormat.GetDayName(turno.Fecha.DayOfWeek);
        var diaTexto = $"{diaSemana} {turno.Fecha.Day}";
        var diaTextoCap = char.ToUpper(diaSemana[0]) + diaSemana[1..] + $" {turno.Fecha.Day}";

        var variante = Random.Shared.Next(3);
        return variante switch
        {
            0 => $"Hola{saludoNombre} 😊 Te recordamos tu turno para el {diaTexto} a las {turno.Hora} hs.\n\n" +
                 "✨ Antes de venir:\n" +
                 "• Rasurá la zona con Gillette nueva la noche anterior.\n" +
                 "• Traé TOALLA.\n" +
                 "• No tomes sol ni uses cabina solar 24 hs antes ni después.\n" +
                 "• Si tenés tatuajes en la zona, cubrilos con cinta de papel.\n\n" +
                 "⚠️ IMPORTANTE:\n" +
                 "Una vez recibido este mensaje:\n" +
                 "• Cancelación/reprogramación antes del turno: 50% del valor.\n" +
                 "• Cancelación el mismo día o ausencia sin aviso: 100% del valor.\n\n" +
                 "💬 Respondé este mensaje para confirmar tu turno.\n\n" +
                 "✨ Centro de Belleza SV ✨",

            1 => $"🌷 ¡Hola{saludoNombre}! Te escribimos para recordarte que este {diaTexto} a las {turno.Hora} hs tenés tu sesión de depilación definitiva.\n\n" +
                 "Antes de asistir:\n" +
                 "• Rasurá la zona la noche anterior con Gillette nueva.\n" +
                 "• Traé una toalla.\n" +
                 "• Evitá sol/cabina solar 24 hs antes y después.\n" +
                 "• Si hay tatuajes en la zona, cubrilos con cinta de papel.\n\n" +
                 "📌 Cancelaciones: luego de recibir este recordatorio, la cancelación antes del turno tiene un cargo del 50%. Si cancelás el mismo día o no asistís, corresponde el 100%.\n\n" +
                 "¿Nos confirmás tu asistencia respondiendo este mensaje? 🤍\n\n" +
                 "Centro de Belleza SV",

            _ => $"✨ ¡Hola{saludoNombre}! Recordatorio de tu turno ✨\n\n" +
                 $"📅 {diaTextoCap}\n" +
                 $"🕐 {turno.Hora} hs\n" +
                 "🌸 Depilación definitiva\n\n" +
                 "Para tu sesión, recordá:\n" +
                 "✅ Rasurar la zona la noche anterior con Gillette nueva.\n" +
                 "✅ Traer TOALLA.\n" +
                 "✅ Evitar sol/cabina solar 24 hs antes y después.\n" +
                 "✅ Cubrir tatuajes de la zona con cinta de papel.\n\n" +
                 "⚠️ Después de recibir este aviso:\n" +
                 "• Cancelación antes del turno → 50%.\n" +
                 "• Cancelación el mismo día o inasistencia → 100%.\n\n" +
                 "💬 Por favor, confirmá tu turno respondiendo este mensaje.\n\n" +
                 "🤍 Centro de Belleza SV"
        };
    }

    private static string? ExtraerMessageId(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?["messageId"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }
}
