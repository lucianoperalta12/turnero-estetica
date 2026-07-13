namespace TurneroWorker.Models;

/// <summary>
/// Resultado del intento de envío de WhatsApp.
/// </summary>
public class WhatsAppSendResult
{
    public bool Exitoso { get; init; }

    /// <summary>ID de mensaje devuelto por la API de Meta en caso de éxito.</summary>
    public string? MessageId { get; init; }

    /// <summary>Código HTTP de la respuesta.</summary>
    public int StatusCode { get; init; }

    /// <summary>Cuerpo completo de la respuesta (útil para debugging).</summary>
    public string? RawResponse { get; init; }

    /// <summary>Mensaje de error en caso de fallo de red u otro.</summary>
    public string? Error { get; init; }

    public static WhatsAppSendResult Ok(string? messageId, int statusCode, string rawResponse) =>
        new() { Exitoso = true, MessageId = messageId, StatusCode = statusCode, RawResponse = rawResponse };

    public static WhatsAppSendResult Fallo(int statusCode, string rawResponse) =>
        new() { Exitoso = false, StatusCode = statusCode, RawResponse = rawResponse };

    public static WhatsAppSendResult ErrorRed(string error) =>
        new() { Exitoso = false, Error = error };
}
