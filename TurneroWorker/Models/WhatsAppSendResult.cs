namespace TurneroWorker.Models;

/// <summary>
/// Clasificación del tipo de error para el log en la base de datos.
/// </summary>
public enum TipoErrorWhatsApp
{
    HttpError,   // respuesta HTTP no exitosa (4xx, 5xx)
    Red,         // excepción de red / conexión rechazada / timeout
    Parse,       // error al parsear la respuesta JSON
    Otro
}

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

    /// <summary>Clasificación del tipo de fallo (solo relevante cuando Exitoso = false).</summary>
    public TipoErrorWhatsApp? TipoError { get; init; }

    /// <summary>Stack trace capturado en caso de excepción de código.</summary>
    public string? StackTrace { get; init; }

    public static WhatsAppSendResult Ok(string? messageId, int statusCode, string rawResponse) =>
        new() { Exitoso = true, MessageId = messageId, StatusCode = statusCode, RawResponse = rawResponse };

    public static WhatsAppSendResult Fallo(int statusCode, string rawResponse) =>
        new() { Exitoso = false, StatusCode = statusCode, RawResponse = rawResponse, TipoError = TipoErrorWhatsApp.HttpError };

    public static WhatsAppSendResult ErrorRed(string error, string? stackTrace = null) =>
        new() { Exitoso = false, Error = error, TipoError = TipoErrorWhatsApp.Red, StackTrace = stackTrace };

    public static WhatsAppSendResult ErrorCodigo(string error, string? stackTrace = null) =>
        new() { Exitoso = false, Error = error, TipoError = TipoErrorWhatsApp.Otro, StackTrace = stackTrace };
}
