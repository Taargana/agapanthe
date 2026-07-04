using Agapanthe.Assets.Model;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// A PBR material bound as descriptor set 1 (<see cref="MaterialLayout"/>): five combined image samplers
/// (base color, normal, metallic-roughness, occlusion, emissive) plus a factor uniform buffer. The set is
/// allocated from the persistent per-material <see cref="DescriptorAllocator"/> and written once at
/// construction, then bound unchanged every frame.
/// <para>
/// <b>Ownership.</b> A material owns only its factor <see cref="GpuBuffer"/> (host-visible, written once).
/// It does <i>not</i> own the images, the sampler, or the descriptor set:
/// </para>
/// <list type="bullet">
///   <item>Texture <see cref="GpuImage"/>s and the 1×1 placeholders are owned by the <see cref="Scene"/> —
///   a glTF image can be referenced by several materials (the Assets image catalog dedups globally by
///   source+color-space), so centralising their ownership avoids a double free. (This is a deliberate
///   refinement of the board's "material owns its images": ownership sits one level up, in the Scene, so
///   shared images have a single owner.)</item>
///   <item>The <see cref="Sampler"/> comes from the shared <see cref="SamplerCache"/>.</item>
///   <item>The descriptor set lives in the <see cref="DescriptorAllocator"/>'s never-reset pool and is
///   freed when that allocator is disposed (owned by the Renderer, M4-10), not here.</item>
/// </list>
/// So <see cref="Dispose"/> releases the factor UBO and nothing else.
/// </summary>
public sealed class Material : IDisposable
{
    private GpuBuffer _uniformBuffer;
    private bool _disposed;

    /// <summary>
    /// Builds a material: creates and fills the factor UBO, allocates its set-1 descriptor set from
    /// <paramref name="allocator"/>, and writes all six bindings (five images through
    /// <paramref name="sampler"/>, plus the UBO). Images are passed pre-resolved in binding order — the
    /// caller (<see cref="SceneBuilder"/>) substitutes shared placeholders for absent slots.
    /// </summary>
    internal Material(
        GraphicsDevice device,
        DescriptorAllocator allocator,
        DescriptorSetLayout layout,
        in MaterialUniforms uniforms,
        AlphaMode alphaMode,
        string name,
        GpuImage baseColor,
        GpuImage normal,
        GpuImage metallicRoughness,
        GpuImage occlusion,
        GpuImage emissive,
        Sampler sampler)
    {
        AlphaMode = alphaMode;
        Name = name;

        // Host-visible: written exactly once here, never touched again — Write<T> is correct and no
        // staging upload is needed (the factor block is tiny and constant for the material's lifetime).
        _uniformBuffer = new GpuBuffer(device, (ulong)System.Runtime.CompilerServices.Unsafe.SizeOf<MaterialUniforms>(), BufferUsage.Uniform);
        _uniformBuffer.Write(new ReadOnlySpan<MaterialUniforms>(in uniforms));

        DescriptorSet = allocator.AllocateSet(layout);
        allocator.WriteCombinedImageSampler(DescriptorSet, MaterialLayout.BaseColorBinding, baseColor, sampler);
        allocator.WriteCombinedImageSampler(DescriptorSet, MaterialLayout.NormalBinding, normal, sampler);
        allocator.WriteCombinedImageSampler(DescriptorSet, MaterialLayout.MetallicRoughnessBinding, metallicRoughness, sampler);
        allocator.WriteCombinedImageSampler(DescriptorSet, MaterialLayout.OcclusionBinding, occlusion, sampler);
        allocator.WriteCombinedImageSampler(DescriptorSet, MaterialLayout.EmissiveBinding, emissive, sampler);
        allocator.WriteUniformBuffer(DescriptorSet, MaterialLayout.UniformsBinding, _uniformBuffer);
    }

    /// <summary>The persistent set-1 handle for this material. Bind it at set index 1 (owned by the allocator).</summary>
    public DescriptorSetHandle DescriptorSet { get; }

    /// <summary>Alpha handling (Opaque or Mask). Informational for M4; the cutoff/mode also travel in the UBO for the shader.</summary>
    public AlphaMode AlphaMode { get; }

    /// <summary>Material name for diagnostics.</summary>
    public string Name { get; }

    /// <summary>Releases the factor UBO (deferred through the device DeletionQueue). Images/sampler/set are not owned.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _uniformBuffer?.Dispose();
        _uniformBuffer = null!;
    }
}
