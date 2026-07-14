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
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // Limpiar caracteres no numéricos
        var soloDigitos = new string(raw.Where(char.IsDigit).ToArray());

        // Si empieza con 0, remover el 0 inicial (ej: 03564... -> 3564...)
        if (soloDigitos.StartsWith('0'))
        {
            soloDigitos = soloDigitos[1..];
        }

        // Si ya empieza con 549 y tiene el largo correcto (13 dígitos), es correcto
        if (soloDigitos.StartsWith("549") && soloDigitos.Length == 13)
        {
            return soloDigitos;
        }

        // Si empieza con 54 pero no tiene el 9 de móvil (ej: 543564562288, largo 12) -> transformamos a 549...
        if (soloDigitos.StartsWith("54") && soloDigitos.Length == 12)
        {
            return "549" + soloDigitos[2..];
        }

        // Si empieza con 54 y tiene largo 10 (sin el 9 y sin el 54 real en formato correcto),
        // o si es un número local de 10 dígitos (ej: 3564562288)
        if (soloDigitos.Length == 10)
        {
            return "549" + soloDigitos;
        }

        // Si tiene 12 dígitos y contiene un "15" (celular local argentino clásico, ej: 356415562288)
        // El "15" suele estar después del código de área. San Francisco es 3564 (4 dígitos).
        // En códigos de área de 3 dígitos (ej: 341 - Rosario) el 15 está en la posición 3.
        // Un enfoque seguro: si detectamos "15" y removiéndolo nos da un número de 10 dígitos.
        if (soloDigitos.Length == 12)
        {
            // Intentar detectar y remover el 15 del medio
            // Probamos si removiendo el 15 de la posición típica de 4 dígitos de área
            var sin15De4 = soloDigitos.Remove(4, 2);
            if (sin15De4.Length == 10)
            {
                return "549" + sin15De4;
            }

            // Probamos si es de 3 dígitos de área
            var sin15De3 = soloDigitos.Remove(3, 2);
            if (sin15De3.Length == 10)
            {
                return "549" + sin15De3;
            }

            // Probamos si es de 2 dígitos de área (ej: 11 - BsAs)
            var sin15De2 = soloDigitos.Remove(2, 2);
            if (sin15De2.Length == 10)
            {
                return "549" + sin15De2;
            }
        }

        // Fallback robusto: si no coincide con las anteriores pero tiene entre 10 y 13 dígitos
        if (soloDigitos.Length >= 10 && soloDigitos.Length <= 13)
        {
            if (soloDigitos.StartsWith("549")) return soloDigitos;
            if (soloDigitos.StartsWith("54")) return "549" + soloDigitos[2..];
            
            // Si tiene 11 dígitos y empieza con 9 (ej: 93564562288) -> transformamos a 549...
            if (soloDigitos.StartsWith('9') && soloDigitos.Length == 11)
            {
                return "54" + soloDigitos;
            }

            return "549" + soloDigitos;
        }

        return null;
    }
}
