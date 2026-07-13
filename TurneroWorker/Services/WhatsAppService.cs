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
        var url = $"{_config.BaseUrl.TrimEnd('/')}/send";
        var mensaje = BuildMensaje(turno);

        var payload = new
        {
            phone = NormalizarTelefono(turno.Telefono),
            message = mensaje
        };

        var json = JsonSerializer.Serialize(payload);
        _logger.LogDebug("Enviando payload a {Url}: {Json}", url, json);

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
    /// Normaliza el teléfono
    /// </summary>
    private static string NormalizarTelefono(string telefono)
    {
        var soloDigitos = new string(telefono.Where(char.IsDigit).ToArray());
        return soloDigitos;
    }

    private static string BuildMensaje(TurnoInfo turno)
    {
        // Rotar entre variantes para no enviar siempre el mismo texto
        var variante = Random.Shared.Next(3);
        return variante switch
        {
            0 => $"Hola {turno.Nombre} 👋\n\n" +
                 $"Te recordamos que hoy tenés turno en la Estética a las {turno.Hora}.\n\n" +
                 "Si necesitás reprogramarlo avisanos por este medio.\n\n" +
                 "¡Te esperamos! 💖",

            1 => $"¡Hola {turno.Nombre}! 😊\n\n" +
                 $"Solo para recordarte tu turno de hoy a las {turno.Hora} en la Estética.\n\n" +
                 "Ante cualquier cambio no dudes en escribirnos.\n\n" +
                 "¡Hasta pronto! ✨",

            _ => $"Hola {turno.Nombre}, buenas 🌸\n\n" +
                 $"Te avisamos que tu turno es hoy a las {turno.Hora}.\n\n" +
                 "Si necesitás cancelar o cambiar la hora, respondé este mensaje.\n\n" +
                 "¡Te esperamos con todo listo! 💅"
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
