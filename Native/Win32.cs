using System;

namespace BrightFlyout.Native;

/// <summary>
/// Constants that are not part of the Windows SDK metadata CsWin32 generates from
/// (HID usages, MCCS VCP codes) plus helpers for the fixed-size char buffers that
/// blittable-only structs expose.
/// </summary>
internal static class Win32
{
    // HID consumer-control usages (USB HID usage tables, not win32metadata).
    public const ushort HidUsagePageConsumer = 0x0C;
    public const ushort HidUsageConsumerControl = 0x01;
    public const ushort HidUsageBrightnessIncrement = 0x006F;
    public const ushort HidUsageBrightnessDecrement = 0x0070;

    // MCCS VCP code for luminance (DDC/CI).
    public const byte VcpCodeLuminance = 0x10;

    // NT status space of hid.dll: HIDP_STATUS_SUCCESS.
    public const int HidpStatusSuccess = 0x00110000;

    /// <summary>Writes a null-terminated string into a fixed-size char buffer
    /// (CsWin32 inline array, e.g. NOTIFYICONDATAW.szTip).</summary>
    public static void WriteFixedString(Span<char> buffer, string value)
    {
        buffer.Clear();
        value.AsSpan(0, Math.Min(value.Length, buffer.Length - 1)).CopyTo(buffer);
    }
}
