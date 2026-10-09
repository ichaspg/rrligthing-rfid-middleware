namespace RfidMiddleware.Core;

public static class EpcNormalizer
{
    public static bool TryNormalize(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized.Length is < 2 or > 512 || normalized.Length % 2 != 0)
        {
            normalized = string.Empty;
            return false;
        }

        foreach (var character in normalized)
        {
            if (!Uri.IsHexDigit(character))
            {
                normalized = string.Empty;
                return false;
            }
        }

        return true;
    }

    public static string Normalize(string value) =>
        TryNormalize(value, out var normalized)
            ? normalized
            : throw new FormatException($"Invalid EPC value: '{value}'.");
}

