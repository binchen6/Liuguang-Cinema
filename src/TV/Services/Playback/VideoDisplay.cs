using Microsoft.Graphics.Display;
using Microsoft.UI;
using SharpDX.DXGI;
using System.Globalization;
using System.Runtime.InteropServices;

namespace TV.Services.Playback;

internal static class VideoDisplay
{
    public static bool IsHdrActive(WindowId window)
    {
        try
        {
            using var display = DisplayInformation.CreateForWindowId(window);
            return display.GetAdvancedColorInfo().CurrentAdvancedColorKind == DisplayAdvancedColorKind.HighDynamicRange;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    public static nint RetainNativeReference(string[] options, string optionName)
    {
        var prefix = $"--{optionName}=0x";
        var handle = options.FirstOrDefault(option => option.StartsWith(prefix, StringComparison.Ordinal));
        if (handle is null || !nint.TryParse(handle.AsSpan(prefix.Length), NumberStyles.HexNumber,
            CultureInfo.InvariantCulture, out var pointer) || pointer == 0)
            throw new InvalidOperationException("无法连接视频渲染表面。");

        Marshal.AddRef(pointer);
        return pointer;
    }

    public static bool TryEnableHdrOutput(nint pointer)
    {
        try
        {
            // VideoView supplies its own composition swap chain. Give VLC 10-bit buffers
            // before it takes its first reference to the back buffer.
            Marshal.AddRef(pointer);
            using var swapChain = new SwapChain1(pointer);
            using var color = swapChain.QueryInterface<SwapChain3>();
            var description = swapChain.Description1;
            var colorSpace = ColorSpaceType.RgbFullG2084NoneP2020;
            if (description.Format != Format.R10G10B10A2_UNorm)
                swapChain.ResizeBuffers(0, description.Width, description.Height, Format.R10G10B10A2_UNorm, description.Flags);
            if (color.CheckColorSpaceSupport(colorSpace).HasFlag(SwapChainColorSpaceSupportFlags.Present))
                return true;

            swapChain.ResizeBuffers(0, description.Width, description.Height, description.Format, description.Flags);
        }
        catch (Exception ex) when (ex is SharpDX.SharpDXException or COMException)
        {
            Diagnostics.Log($"HDR composition surface unavailable (0x{ex.HResult:X8})");
        }
        return false;
    }
}
