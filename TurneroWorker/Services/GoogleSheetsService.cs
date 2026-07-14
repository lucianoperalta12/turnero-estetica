using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TurneroWorker.Configuration;

namespace TurneroWorker.Services;

public class GoogleSheetsService
{
    private readonly GoogleSheetsConfig _config;
    private readonly GoogleCalendarConfig _calendarConfig;
    private readonly ILogger<GoogleSheetsService> _logger;

    public GoogleSheetsService(IOptions<AppSettings> options, ILogger<GoogleSheetsService> logger)
    {
        _config = options.Value.GoogleSheets;
        _calendarConfig = options.Value.GoogleCalendar;
        _logger = logger;
    }

    private SheetsService BuildSheetsService()
    {
        GoogleCredential credential;
        using var stream = new FileStream(_calendarConfig.CredentialsFilePath, FileMode.Open, FileAccess.Read);
        credential = GoogleCredential
            .FromStream(stream)
            .CreateScoped(SheetsService.Scope.SpreadsheetsReadonly);

        return new SheetsService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "TurneroWorker"
        });
    }

    /// <summary>
    /// Lee la hoja de contactos y devuelve un diccionario nombre_lowercase → teléfono.
    /// Columna A = Nombre, Columna B = Teléfono.
    /// </summary>
    public async Task<Dictionary<string, string>> GetDirectorioAsync()
    {
        var service = BuildSheetsService();
        var request = service.Spreadsheets.Values.Get(_config.SpreadsheetId, _config.Range);

        Google.Apis.Sheets.v4.Data.ValueRange response;
        try
        {
            response = await request.ExecuteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error al leer el Google Sheet de contactos");
            return new Dictionary<string, string>();
        }

        var directorio = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var filas = response.Values;

        if (filas == null || filas.Count == 0)
        {
            _logger.LogWarning("El Google Sheet de contactos está vacío o no tiene datos en el rango '{Range}'", _config.Range);
            return directorio;
        }

        foreach (var fila in filas)
        {
            if (fila.Count < 2) continue;

            var nombre = fila[0]?.ToString()?.Trim() ?? string.Empty;
            var telefono = fila[1]?.ToString()?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(nombre) || string.IsNullOrEmpty(telefono))
            {
                _logger.LogDebug("Fila omitida: nombre='{Nombre}', telefono='{Tel}'", nombre, telefono);
                continue;
            }

            var clave = nombre.ToLowerInvariant();
            if (directorio.ContainsKey(clave))
            {
                _logger.LogWarning("Nombre duplicado en el Sheet: '{Nombre}'. Se usa el primero encontrado.", nombre);
                continue;
            }

            var telefonoNormalizado = NormalizarTelefonoArgentino(telefono);
            if (string.IsNullOrEmpty(telefonoNormalizado))
            {
                _logger.LogWarning("Teléfono '{Telefono}' para el contacto '{Nombre}' no es válido para Argentina. Omitiendo.", telefono, nombre);
                continue;
            }

            directorio[clave] = telefonoNormalizado;
        }

        _logger.LogInformation("Directorio cargado desde Google Sheets: {Count} contacto(s)", directorio.Count);
        return directorio;
    }

    /// <summary>
    /// Limpia y normaliza el teléfono al formato internacional de Argentina (549 + prefijo sin 0 + número sin 15).
    /// </summary>
    private static string? NormalizarTelefonoArgentino(string raw)
    {
        // Limpiar caracteres no numéricos
        var soloDigitos = new string(raw.Where(char.IsDigit).ToArray());

        if (string.IsNullOrEmpty(soloDigitos)) return null;

        // Si ya tiene el prefijo de país completo 549 seguido de 10 dígitos (total 13 caracteres)
        if (soloDigitos.StartsWith("549") && soloDigitos.Length == 13)
        {
            return soloDigitos;
        }

        // Si empieza con 54 pero le falta el '9' de celular internacional (ej. 54 3564 562288 tiene largo 12)
        if (soloDigitos.StartsWith("54") && soloDigitos.Length == 12)
        {
            return "549" + soloDigitos[2..];
        }

        // Si es un número local de 10 dígitos (ej. 3564562288 o 0356415562288)
        // Quitamos el '0' del código de área si estuviera
        if (soloDigitos.StartsWith('0'))
        {
            soloDigitos = soloDigitos[1..];
        }

        // Si contiene el '15' de celular local (ej. prefijo 3564 + 15 + número 562288 -> largo 12)
        // Intentar remover el '15'
        if (soloDigitos.Length == 12 && soloDigitos.Substring(4, 2) == "15")
        {
            soloDigitos = soloDigitos.Remove(4, 2);
        }

        // El número estándar sin 0 y sin 15 debe tener 10 dígitos
        if (soloDigitos.Length == 10)
        {
            return "549" + soloDigitos;
        }

        // Si ya está normalizado de alguna otra forma o tiene largo no esperado, intentamos retornarlo tal cual si parece válido
        if (soloDigitos.Length >= 10 && soloDigitos.Length <= 13)
        {
            if (!soloDigitos.StartsWith("549"))
            {
                if (soloDigitos.StartsWith("54"))
                {
                    return "549" + soloDigitos[2..];
                }
                return "549" + soloDigitos;
            }
            return soloDigitos;
        }

        return null;
    }
}
