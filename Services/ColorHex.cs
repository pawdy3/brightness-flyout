using Windows.UI;
using CommunityToolkit.WinUI.Helpers;

namespace BrightFlyout.Services;

/// <summary>
/// Null-safe facade over the toolkit's <see cref="ColorHelper"/> (#AARRGGBB,
/// #RRGGBB, short forms, named colors): invalid or empty input falls back
/// instead of throwing.
/// </summary>
internal static class ColorHex
{
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            color = ColorHelper.ToColor(text);
            return true;
        }
        catch
        {
            // not a parseable color
            return false;
        }
    }

    public static Color ParseOrDefault(string? text, Color fallback) =>
        TryParse(text, out var color) ? color : fallback;

    public static string ToHex(Color c) => ColorHelper.ToHex(c);
}
