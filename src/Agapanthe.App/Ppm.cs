using Agapanthe.Graphics;

namespace Agapanthe.App;

/// <summary>Binary PPM writers for the headless capture harness.</summary>
internal static class Ppm
{
    /// <summary>
    /// Writes a presented-image snapshot as a binary PPM (UI-1). The swapchain image is ALREADY sRGB-encoded (the
    /// format's OETF ran when the fragment was stored), so the bytes go through untouched — no exposure, no
    /// tonemap, no gamma. Only the channel order is fixed up, since the surface format is usually BGRA.
    /// </summary>
    public static void WriteSwapchain(string path, byte[] bytes, int width, int height, PixelFormat format)
    {
        var needed = checked(width * height * 4);
        if (bytes.Length < needed)
        {
            throw new ArgumentException(
                $"swapchain capture buffer is {bytes.Length} B but {width}×{height}×4 = {needed} B are needed — "
                + "the presented extent and the read-back buffer disagree.", nameof(bytes));
        }

        // Bgra8Srgb is the only BGRA format the swapchain uses (see PixelFormat); every other 8-bit format is RGBA.
        var bgra = format == PixelFormat.Bgra8Srgb;
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));

        var row = new byte[width * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * width) + x) * 4;
                row[(x * 3) + 0] = bgra ? bytes[i + 2] : bytes[i + 0];
                row[(x * 3) + 1] = bytes[i + 1];
                row[(x * 3) + 2] = bgra ? bytes[i + 0] : bytes[i + 2];
            }

            output.Write(row);
        }
    }
}
