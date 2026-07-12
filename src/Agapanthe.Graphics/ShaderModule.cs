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
            GC.SuppressFinalize(this);
            throw new GraphicsException($"SPIR-V blob length {spirv.Length} is not a positive multiple of 4.");
        }

        _device = device;
        Stage = stage;

        try
        {
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
        }
        catch
        {
            // No handle was registered; stop the finalizer reporting a phantom leak.
            GC.SuppressFinalize(this);
            throw;
        }

        ResourceTracker.Register("VkShaderModule");
    }

    ~ShaderModule() => ResourceTracker.ReportFinalizerLeak(nameof(ShaderModule));

    public ShaderStage Stage { get; }

    internal Silk.NET.Vulkan.ShaderModule Handle => _handle;

    /// <summary>
    /// Deferred disposal (default, spec §3.2.1/§3.2.5): the module is released once the frame that used it
    /// (via a pipeline referencing it) leaves flight. Safe only because shader/pipeline swaps happen at the
    /// frame boundary before recording; N+FramesInFlight then covers every in-flight frame.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Non-capturing deferred destroy (zero managed allocation): the raw handle travels by value in the
        // payload (Handle0 = VkShaderModule) and the destructor is a cached static delegate.
        var payload = new DeletionPayload(_handle.Handle);
        _handle = default;
        _device.EnqueueDestroy(DestroyDelegate, in payload);
        GC.SuppressFinalize(this);
    }

    // Allocated once per type: passing this reference on the deferred path costs no allocation.
    private static readonly Action<GraphicsDevice, DeletionPayload> DestroyDelegate = DestroyDeferred;

    // Deferred destructor for the non-capturing DeletionQueue path (Dispose): rebuilds the handle from the
    // value-type payload and destroys it. Runs after the frame leaves flight.
    private static void DestroyDeferred(GraphicsDevice device, DeletionPayload payload)
    {
        var handle = new Silk.NET.Vulkan.ShaderModule(payload.Handle0);
        if (handle.Handle != 0)
        {
            device.Api.DestroyShaderModule(device.Device, handle, null);
            ResourceTracker.Unregister("VkShaderModule");
        }
    }
}
