namespace TurneroWorker.Services;

/// <summary>
/// Lógica centralizada de normalización de números de teléfono argentinos.
/// Formato de salida: 549 + código de área + número (13 dígitos).
/// Retorna <see cref="string.Empty"/> si el número no es válido.
/// </summary>
public static class PhoneHelper
{
    public static string NormalizarTelefono(string? rawPhone)
    {
        if (string.IsNullOrWhiteSpace(rawPhone)) return string.Empty;

        var digits = new string(rawPhone.Where(char.IsDigit).ToArray());

        // Remover '0' inicial de código de área (ej: 03564 → 3564)
        if (digits.StartsWith('0'))
            digits = digits[1..];

        // Ya en formato correcto
        if (digits.StartsWith("549") && digits.Length == 13)
            return digits;

        // 54 + 10 dígitos sin el '9' de móvil (ej: 543564562288 → 5493564562288)
        if (digits.StartsWith("54") && digits.Length == 12)
            return "549" + digits[2..];

        // 10 dígitos locales (ej: 3564562288 → 5493564562288)
        if (digits.Length == 10)
            return "549" + digits;

        // 12 dígitos que pueden contener el '15' de celular local
        // Ejemplo: 356415562288 → quitar '15' en la posición del área → 3564562288 → 5493564562288
        if (digits.Length == 12)
        {
            var sin15De4 = digits.Remove(4, 2);
            if (sin15De4.Length == 10) return "549" + sin15De4;

            var sin15De3 = digits.Remove(3, 2);
            if (sin15De3.Length == 10) return "549" + sin15De3;

            var sin15De2 = digits.Remove(2, 2);
            if (sin15De2.Length == 10) return "549" + sin15De2;
        }

        // Fallback robusto para rangos entre 10 y 13 dígitos
        if (digits.Length >= 10 && digits.Length <= 13)
        {
            if (digits.StartsWith("549")) return digits;
            if (digits.StartsWith("54"))  return "549" + digits[2..];
            if (digits.StartsWith('9') && digits.Length == 11) return "54" + digits;
            return "549" + digits;
        }

        return string.Empty;
    }
}
