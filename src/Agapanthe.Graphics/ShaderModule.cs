using Agapanthe.Core;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>A VkShaderModule created from SPIR-V. Owns the handle; disposal is deterministic.</summary>
public sealed unsafe class ShaderModule : IDisposable
{
    private readonly GraphicsDevice _device;
    private Silk.NET.Vulkan.ShaderModule _handle;
    private bool _disposed;

    public ShaderModule(GraphicsDevice device, ReadOnlySpan<byte> spirv, ShaderStage stage)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (spirv.Length == 0 || spirv.Length % 4 != 0)
        {
            throw new GraphicsException($"SPIR-V blob length {spirv.Length} is not a positive multiple of 4.");
        }

        _device = device;
        Stage = stage;

        fixed (byte* code = spirv)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)spirv.Length,
                PCode = (uint*)code,
            };

            Silk.NET.Vulkan.ShaderModule handle;
            VkCheck.ThrowIfFailed(
                device.Api.CreateShaderModule(device.Device, &createInfo, null, &handle),
                "vkCreateShaderModule");
            _handle = handle;
        }

        ResourceTracker.Register("VkShaderModule");
    }

    ~ShaderModule() => ResourceTracker.ReportFinalizerLeak(nameof(ShaderModule));

    public ShaderStage Stage { get; }

    internal Silk.NET.Vulkan.ShaderModule Handle => _handle;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _device.Api.DestroyShaderModule(_device.Device, _handle, null);
        ResourceTracker.Unregister("VkShaderModule");
        GC.SuppressFinalize(this);
    }
}
