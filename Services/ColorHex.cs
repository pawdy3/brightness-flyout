using Windows.UI;

namespace BrightFlyout.Services;

/// <summary>Parses/formats colors as hex strings (#AARRGGBB or #RRGGBB).</summary>
internal static class ColorHex
{
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        text = text.Trim().TrimStart('#');
        try
        {
            if (text.Length == 8) // AARRGGBB
            {
                var value = Convert.ToUInt32(text, 16);
                color = Color.FromArgb(
                    (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
                return true;
            }

            if (text.Length == 6) // RRGGBB
            {
                var value = Convert.ToUInt32(text, 16);
                color = Color.FromArgb(255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
                return true;
            }
        }
        catch
        {
            // not a hex string
        }

        return false;
    }

    public static Color ParseOrDefault(string? text, Color fallback) =>
        TryParse(text, out var color) ? color : fallback;

    public static string ToHex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
}
