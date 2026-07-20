using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TurneroWorker.Services;

public static class NombrePacienteHelper
{
    private static readonly Regex HorarioRegex = new(
        @"^\s*\d{1,2}(?::\d{2})?\s*(?:hs?)?\.?\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EspaciosRegex = new(
        @"\s+",
        RegexOptions.Compiled);

    public static string ObtenerNombrePaciente(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary))
        {
            return string.Empty;
        }

        var nombre = HorarioRegex.Replace(summary, string.Empty);
        return LimpiarTextoVisible(nombre);
    }

    public static string NormalizarClave(string? nombre)
    {
        if (string.IsNullOrWhiteSpace(nombre))
        {
            return string.Empty;
        }

        var limpio = LimpiarTextoVisible(nombre).ToLowerInvariant();
        return QuitarTildes(limpio);
    }

    private static string LimpiarTextoVisible(string value)
    {
        var sinPuntosFinales = value.Trim().TrimEnd('.');
        return EspaciosRegex.Replace(sinPuntosFinales, " ").Trim();
    }

    private static string QuitarTildes(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(capacity: normalized.Length);

        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
