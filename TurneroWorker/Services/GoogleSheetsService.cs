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

            directorio[clave] = telefono;
        }

        _logger.LogInformation("Directorio cargado desde Google Sheets: {Count} contacto(s)", directorio.Count);
        return directorio;
    }
}
