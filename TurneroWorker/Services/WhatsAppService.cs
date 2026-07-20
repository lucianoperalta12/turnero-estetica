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
        var mensaje = BuildMensaje(turno);

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
