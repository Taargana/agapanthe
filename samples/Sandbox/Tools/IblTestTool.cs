using Agapanthe.App;
using Agapanthe.Assets;
using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Platform;
using Agapanthe.Rendering;

namespace Sandbox;

/// <summary>
/// Headless IBL generation + capture (M7-04 verification): <c>AGAPANTHE_IBL_TEST=&lt;prefix&gt;</c> generates IBL
/// maps from the HDRI fixture, dumps the environment cube faces + irradiance + BRDF LUT as PPMs, then exits — no
/// scene, no render loop. Orthogonal to <see cref="AppHost"/>, so it owns a minimal window + device of its own.
/// </summary>
internal static class IblTestTool
{
    public static int Run(string prefix)
    {
        var modelsDir = Path.Combine(AppContext.BaseDirectory, "models");
        var hdrPath = Path.Combine(modelsDir, "studio_small_1k.hdr");
        if (!File.Exists(hdrPath))
        {
            Log.Error($"Sandbox IBL test: HDRI fixture not found at '{hdrPath}'.");
            return 1;
        }

        var shaderDir = AppHost.ResolveShaderDirectory();
        var clean = false;
        using var window = new EngineWindow("Agapanthe — IBL test", 64, 64);
        GraphicsDevice? device = null;

        window.Loaded += () =>
        {
            device = new GraphicsDevice("Agapanthe IBL test", window.GetRequiredVulkanExtensions(), window.VkSurface!);

            var hdr = HdrImageLoader.Load(hdrPath);
            Log.Info($"Sandbox IBL test: loaded HDRI {hdr.Width}x{hdr.Height} from '{hdrPath}'.");

            using var generator = new IblGenerator(device, shaderDir);
            var maps = generator.Generate(hdr);
            try
            {
                for (var face = 0u; face < 6; face++)
                {
                    var bytes = GpuReadback.ReadImage(device, maps.Environment, ImageLayoutState.ShaderReadOnly, 8, face);
                    WriteHalfPpm($"{prefix}_env_f{face}.ppm", bytes, maps.Environment.Width, maps.Environment.Height);
                }

                var irr = GpuReadback.ReadImage(device, maps.Irradiance, ImageLayoutState.ShaderReadOnly, 8, 4);
                WriteHalfPpm($"{prefix}_irradiance_f4.ppm", irr, maps.Irradiance.Width, maps.Irradiance.Height);

                var lut = GpuReadback.ReadImage(device, maps.BrdfLut, ImageLayoutState.ShaderReadOnly, 4);
                WriteRgHalfPpm($"{prefix}_brdf_lut.ppm", lut, maps.BrdfLut.Width, maps.BrdfLut.Height);

                Log.Info($"Sandbox IBL test: wrote captures with prefix '{prefix}'.");
            }
            finally
            {
                maps.Dispose();
            }

            window.Close();
        };

        try
        {
            window.Run();
        }
        finally
        {
            device?.DeletionQueue.FlushAll();
            device?.Dispose();
            clean = ResourceTracker.Report();
        }

        return clean ? 0 : 1;
    }

    private static void WriteHalfPpm(string path, byte[] bytes, uint width, uint height)
    {
        var halfs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        var row = new byte[width * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * (int)width) + x) * 4;
                row[(x * 3) + 0] = EncodeTonemapped((float)halfs[i + 0]);
                row[(x * 3) + 1] = EncodeTonemapped((float)halfs[i + 1]);
                row[(x * 3) + 2] = EncodeTonemapped((float)halfs[i + 2]);
            }

            output.Write(row);
        }

        static byte EncodeTonemapped(float v)
        {
            var x = Math.Max(v, 0f);
            x /= 1f + x;
            var srgb = x <= 0.0031308f ? x * 12.92f : (1.055f * MathF.Pow(x, 1f / 2.4f)) - 0.055f;
            return (byte)Math.Clamp((int)((srgb * 255f) + 0.5f), 0, 255);
        }
    }

    private static void WriteRgHalfPpm(string path, byte[] bytes, uint width, uint height)
    {
        var halfs = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Half>(bytes);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write);
        output.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n"));
        var row = new byte[width * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = ((y * (int)width) + x) * 2;
                row[(x * 3) + 0] = (byte)Math.Clamp((int)(((float)halfs[i + 0] * 255f) + 0.5f), 0, 255);
                row[(x * 3) + 1] = (byte)Math.Clamp((int)(((float)halfs[i + 1] * 255f) + 0.5f), 0, 255);
                row[(x * 3) + 2] = 0;
            }

            output.Write(row);
        }
    }
}
