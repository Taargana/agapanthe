using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Agapanthe.Graphics;
using Agapanthe.Graphics.Memory;
using Noesis;
using AgapantheBlendMode = Agapanthe.Graphics.BlendMode;

namespace Agapanthe.Ui.Noesis;

/// <summary>
/// Vulkan implementation of <see cref="global::Noesis.RenderDevice"/> (vertical slice — solid-color
/// fill only: <c>Path_Solid</c>/<c>Path_AA_Solid</c>. No gradients, images/patterns, text/glyphs, LCD
/// subpixel, blur/shadow/custom effects, tiled rendering or stencil clipping — see the design plan for
/// the full deferred list). Verified against the real Noesis/Managed source
/// (<c>Src/Noesis/Core/Src/Core/RenderDevice.cs</c>) rather than guessed: the abstract surface below is
/// exactly what that file declares.
/// <para>
/// Architectural note: unlike <c>Agapanthe.Rendering</c>'s passes (which receive a <c>CommandList</c>
/// already open on the frame's single command buffer), Noesis's <see cref="global::Noesis.RenderDevice"/>
/// contract passes NO command buffer into any call — the device is expected to manage its own command
/// recording. <see cref="GraphicsDevice.SubmitImmediate"/> is the only way to do that from outside
/// <c>Agapanthe.Graphics</c> (its <c>CommandList</c> constructor and Vulkan queue are <c>internal</c>),
/// so every <see cref="DrawBatch"/> call opens, records and fully blocks-submits its own one-shot
/// command buffer. This is the correctness-first, not performance-first, vertical-slice choice — each
/// submission fully drains the GPU before returning, so no cross-batch synchronization is needed at
/// all (a real concern for later: batching many draws into one submission).
/// </para>
/// </summary>
public sealed class VulkanRenderDevice : global::Noesis.RenderDevice
{
    private readonly GraphicsDevice _device;
    private readonly ShaderCompiler _compiler;
    private readonly string _shaderDirectory;

    private GraphicsPipeline? _solidPipeline;
    private GraphicsPipeline? _solidAaPipeline;
    private ShaderModule? _solidVert;
    private ShaderModule? _solidAaVert;
    private ShaderModule? _solidFrag;

    private GpuBuffer? _vertexBuffer;
    private GpuBuffer? _indexBuffer;

    private VulkanRenderTarget? _currentTarget;
    private bool _disposed;

    // Every GpuImage this device creates (render targets and textures alike) — tracked and disposed
    // together in Dispose(). A real (non-vertical-slice) implementation would instead hook Noesis's own
    // native refcounting on RenderTarget/Texture (both BaseComponent) so an image is released exactly
    // when Noesis itself is done with it; this vertical slice creates exactly one render target for its
    // one demo call, so a flat list disposed at device teardown is sufficient and honest about the gap.
    private readonly List<GpuImage> _ownedImages = [];

    public VulkanRenderDevice(GraphicsDevice device, string shaderDirectory)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _shaderDirectory = shaderDirectory;
        _compiler = ShaderCompiler.CreateForBuild();
    }

    public override DeviceCaps Caps => new()
    {
        // Modern D3D11/Vulkan-style pixel centers (no half-texel offset, unlike old D3D9).
        CenterPixelOffset = 0f,
        // Noesis renders into its own dedicated offscreen GpuImage, composited afterward as a plain
        // sRGB overlay by a separate pass — its internal working space does not need to match
        // Agapanthe's HDR/linear scene pipeline.
        LinearRendering = false,
        // No dual-source blending set up in this slice — SDF_LCD_* shaders are out of scope anyway.
        SubpixelRendering = false,
        // Vulkan mandates a [0,1] NDC depth range (unlike OpenGL's [-1,1]) — spec fact, not a guess.
        DepthRangeZeroToOne = true,
        // UNVERIFIED (flagged per the design plan's "verify rather than assume"): Vulkan's default
        // clip space has Y pointing down, opposite of OpenGL's Y-up — the well-established convention
        // across engines supporting both backends is to report this as true so Noesis does not apply
        // an extra flip on top of the Y-flip already baked into its own projection matrix (confirmed
        // empirically present — see noesis_solid.vert). If the composited output renders upside down,
        // this is the first value to flip.
        ClipSpaceYInverted = true,
    };

    public override RenderTarget CreateRenderTarget(string label, uint width, uint height, uint sampleCount, bool needsStencil)
    {
        // Multisampling and stencil-based clipping are out of scope for this slice (no Mask/StencilMode
        // batches expected from a solid-color-only XAML) — fail loudly rather than silently ignore a
        // request this device cannot actually satisfy.
        if (sampleCount > 1)
        {
            throw new NotSupportedException(
                $"VulkanRenderDevice (vertical slice): CreateRenderTarget requested sampleCount={sampleCount}; " +
                "multisampling is out of scope.");
        }

        if (needsStencil)
        {
            throw new NotSupportedException(
                "VulkanRenderDevice (vertical slice): CreateRenderTarget requested a stencil buffer; " +
                "stencil-based clipping (Mask shader) is out of scope.");
        }

        var image = new GpuImage(_device, width, height, PixelFormat.Rgba8Unorm, ImageUsage.ColorAttachment | ImageUsage.Sampled | ImageUsage.TransferSrc);
        _ownedImages.Add(image);
        return new VulkanRenderTarget(image);
    }

    public override RenderTarget CloneRenderTarget(string label, RenderTarget surface)
        => CreateRenderTarget(label, surface.Texture.Width, surface.Texture.Height, 1, false);

    public override void SetRenderTarget(RenderTarget surface)
    {
        _currentTarget = surface as VulkanRenderTarget
            ?? throw new ArgumentException(
                "VulkanRenderDevice received a RenderTarget it did not create.", nameof(surface));
    }

    // Tiled rendering is an optimization for large offscreen content on tile-based GPU architectures —
    // out of scope for this slice (single full-surface render only). No-ops, not silently-wrong
    // approximations: nothing downstream of these calls assumes tiling actually happened.
    public override void BeginTile(RenderTarget surface, Tile tile) { }
    public override void EndTile(RenderTarget surface) { }
    public override void ResolveRenderTarget(RenderTarget surface, Tile[] tiles) { }

    public override Texture CreateTexture(string label, uint width, uint height, uint numLevels, TextureFormat format, IntPtr data)
    {
        if (data != IntPtr.Zero)
        {
            // Immutable (pre-populated) textures are never created by this slice's proven demo (a
            // solid-color <Grid> creates only dynamic Ramps/Glyphs textures, both with data=null —
            // confirmed empirically via the LoggingRenderDevice diagnostic). Implementing the tightly-
            // packed multi-mip upload correctly needs a real consumer to test against; fail loudly
            // rather than guess at the packing.
            throw new NotSupportedException(
                $"VulkanRenderDevice (vertical slice): CreateTexture('{label}') was given immutable data; " +
                "only dynamic (data=null) textures are supported — this path is unverified and unreached " +
                "by the proven solid-fill demo.");
        }

        var pixelFormat = format switch
        {
            TextureFormat.RGBA8 => PixelFormat.Rgba8Unorm,
            TextureFormat.RGBX8 => PixelFormat.Rgba8Unorm, // alpha channel simply unused
            TextureFormat.R8 => PixelFormat.R8Unorm,
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unmapped Noesis TextureFormat."),
        };

        var image = new GpuImage(_device, width, height, pixelFormat, ImageUsage.Sampled | ImageUsage.TransferDst, numLevels);
        _ownedImages.Add(image);
        return new VulkanTexture(image);
    }

    public override void UpdateTexture(Texture texture, uint level, uint x, uint y, uint width, uint height, IntPtr data)
    {
        // Never reached by this slice's proven demo (a solid-color <Grid> draws no text, so the glyph
        // atlas is created but never updated — confirmed empirically). Agapanthe.Graphics's GpuUploader
        // only supports a full-image upload, not a sub-rectangle, so a correct implementation needs new
        // Agapanthe.Graphics surface, not something to improvise here.
        throw new NotSupportedException(
            "VulkanRenderDevice (vertical slice): UpdateTexture is out of scope (unreached by the proven " +
            "solid-fill demo; would need a sub-rectangle upload path Agapanthe.Graphics does not have yet).");
    }

    // No cross-call command buffer to manage (see the class remarks) — Begin/End only need to exist to
    // satisfy the abstract contract.
    public override void BeginOffscreenRender() { }
    public override void EndOffscreenRender() { }
    public override void BeginOnscreenRender() { }
    public override void EndOnscreenRender() { }

    public override IntPtr MapVertices(uint bytes) => MapScratch(ref _vertexBuffer, bytes, BufferUsage.Vertex);

    public override void UnmapVertices() { } // host-visible + coherent memory: nothing to flush.

    public override IntPtr MapIndices(uint bytes) => MapScratch(ref _indexBuffer, bytes, BufferUsage.Index);

    public override void UnmapIndices() { }

    private unsafe IntPtr MapScratch(ref GpuBuffer? buffer, uint bytes, BufferUsage usage)
    {
        if (buffer is null || buffer.SizeBytes < bytes)
        {
            buffer?.Dispose();
            buffer = new GpuBuffer(_device, bytes, usage, MemoryDomain.HostVisible);
        }

        if (bytes == 0)
        {
            return IntPtr.Zero;
        }

        var span = buffer.MappedSpan<byte>((int)bytes);
        return (IntPtr)Unsafe.AsPointer(ref MemoryMarshal.GetReference(span));
    }

    public override unsafe void DrawBatch(ref Batch batch)
    {
        if (_currentTarget is null)
        {
            throw new InvalidOperationException("VulkanRenderDevice.DrawBatch called before SetRenderTarget.");
        }

        var pipeline = batch.Shader.Index switch
        {
            var i when i == (int)Shader.Enum.Path_Solid => EnsureSolidPipeline(),
            var i when i == (int)Shader.Enum.Path_AA_Solid => EnsureSolidAaPipeline(),
            _ => throw new NotSupportedException(
                $"VulkanRenderDevice (vertical slice): Shader.Enum.{batch.Shader.Name} is out of scope " +
                "(only Path_Solid/Path_AA_Solid are implemented)."),
        };

        // VertexUniform0 is the only uniform this slice's proven demo ever populates (a 16-float
        // row-major projection matrix) — VertexUniform1/PixelUniform0/PixelUniform1 are asserted empty
        // rather than silently ignored, so a future batch that actually needs them fails loudly instead
        // of rendering silently wrong output.
        if (batch.VertexUniform0.NumWords != 16)
        {
            throw new NotSupportedException(
                $"VulkanRenderDevice (vertical slice): expected VertexUniform0 to carry a 16-float " +
                $"projection matrix, got NumWords={batch.VertexUniform0.NumWords}.");
        }

        AssertEmpty(batch.VertexUniform1, nameof(batch.VertexUniform1));
        AssertEmpty(batch.PixelUniform0, nameof(batch.PixelUniform0));
        AssertEmpty(batch.PixelUniform1, nameof(batch.PixelUniform1));

        // Noesis's raw VertexUniform0 bytes are row-major (verified empirically). Two bugs found and
        // fixed live (branch spike/noesis-probe) before this rendered anything — see noesis_solid.vert's
        // header comment for both: (1) a `row_major` GLSL qualifier on the push_constant block did not
        // take effect with this shaderc/glslang version, fixed by transposing here on the CPU instead;
        // (2) Noesis's Z row is OpenGL-convention ([-1,1]) and was clipped entirely by Vulkan's [0,1]
        // near/far volume, fixed in-shader (gl_Position.z forced to 0, since this pipeline never
        // depth-tests). Verified live: readback showed the full 800x600 quad, solid opaque red, at both
        // the center and the (0,0) corner, in this Sandbox session, then confirmed by human visual
        // verdict (PASS).
        var projection = Matrix4x4.Transpose(*(Matrix4x4*)batch.VertexUniform0.Values);
        var target = _currentTarget;
        var vertexBuffer = _vertexBuffer ?? throw new InvalidOperationException("DrawBatch called before MapVertices.");
        var indexBuffer = _indexBuffer ?? throw new InvalidOperationException("DrawBatch called before MapIndices.");
        // `batch` is a ref parameter and cannot be captured by the SubmitImmediate closure below — copy
        // the plain values it needs out first.
        var numIndices = batch.NumIndices;
        var startIndex = batch.StartIndex;
        var vertexOffset = (int)batch.VertexOffset;

        _device.SubmitImmediate(cmd =>
        {
            var firstTouch = !target.EverRendered;
            if (firstTouch)
            {
                cmd.TransitionImage(target.Image, ImageLayoutState.Undefined, ImageLayoutState.ColorAttachment);
                target.EverRendered = true;
            }

            cmd.BeginRendering(new RenderingAttachments
            {
                Color = new ColorAttachmentInfo
                {
                    Target = new RenderTargetView(target.Image),
                    LoadOp = firstTouch ? AttachmentLoadAction.Clear : AttachmentLoadAction.Load,
                    ClearColor = (0f, 0f, 0f, 0f),
                },
                Width = target.Image.Width,
                Height = target.Image.Height,
            });
            cmd.SetViewportScissor(target.Image.Width, target.Image.Height);

            cmd.BindPipeline(pipeline);
            // The whole shared buffer is bound at offset 0; DrawIndexed's vertexOffset (below) is what
            // actually selects this batch's window — Vulkan adds it to each fetched index before
            // indexing into the vertex buffer, the standard shared-buffer multi-batch pattern already
            // used elsewhere in this engine (P3-M4's per-batch instance offset via push constant).
            cmd.BindVertexBuffer(vertexBuffer);
            cmd.BindIndexBuffer(indexBuffer, IndexFormat.UInt16);
            cmd.PushConstants(pipeline, ShaderStages.Vertex, in projection);
            cmd.DrawIndexed(
                numIndices,
                instanceCount: 1,
                firstIndex: startIndex,
                vertexOffset: vertexOffset);

            cmd.EndRendering();
        });
    }

    private static void AssertEmpty(UniformData data, string name)
    {
        if (data.Values != IntPtr.Zero && data.NumWords != 0)
        {
            throw new NotSupportedException(
                $"VulkanRenderDevice (vertical slice): expected {name} to be empty, got NumWords={data.NumWords}.");
        }
    }

    private GraphicsPipeline EnsureSolidPipeline()
    {
        if (_solidPipeline is not null)
        {
            return _solidPipeline;
        }

        _solidVert = LoadShader("noesis_solid.vert", ShaderStage.Vertex);
        _solidFrag ??= LoadShader("noesis_solid.frag", ShaderStage.Fragment);

        _solidPipeline = new GraphicsPipeline(_device, new GraphicsPipelineDesc
        {
            VertexShader = _solidVert,
            FragmentShader = _solidFrag,
            VertexLayout = new VertexLayout(12, [
                new VertexAttribute(0, 0, PixelFormat.R32G32Sfloat),
                new VertexAttribute(1, 8, PixelFormat.Rgba8Unorm),
            ]),
            PushConstants = [new PushConstantRange(0, 64, ShaderStages.Vertex)],
            ColorFormat = PixelFormat.Rgba8Unorm,
            DepthTest = false,
            Cull = CullMode.None,
            Blend = AgapantheBlendMode.PremultipliedAlpha,
        });
        return _solidPipeline;
    }

    private GraphicsPipeline EnsureSolidAaPipeline()
    {
        if (_solidAaPipeline is not null)
        {
            return _solidAaPipeline;
        }

        _solidAaVert = LoadShader("noesis_solid_aa.vert", ShaderStage.Vertex);
        _solidFrag ??= LoadShader("noesis_solid.frag", ShaderStage.Fragment);

        _solidAaPipeline = new GraphicsPipeline(_device, new GraphicsPipelineDesc
        {
            VertexShader = _solidAaVert,
            FragmentShader = _solidFrag,
            VertexLayout = new VertexLayout(16, [
                new VertexAttribute(0, 0, PixelFormat.R32G32Sfloat),
                new VertexAttribute(1, 8, PixelFormat.Rgba8Unorm),
                new VertexAttribute(2, 12, PixelFormat.R32Sfloat), // coverage, single float
            ]),
            PushConstants = [new PushConstantRange(0, 64, ShaderStages.Vertex)],
            ColorFormat = PixelFormat.Rgba8Unorm,
            DepthTest = false,
            Cull = CullMode.None,
            Blend = AgapantheBlendMode.PremultipliedAlpha,
        });
        return _solidAaPipeline;
    }

    private ShaderModule LoadShader(string fileName, ShaderStage stage)
    {
        var path = System.IO.Path.Combine(_shaderDirectory, fileName);
        var spirv = _compiler.CompileFile(path, stage);
        return new ShaderModule(_device, spirv, stage);
    }

    public new void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _solidPipeline?.Dispose();
        _solidAaPipeline?.Dispose();
        _solidVert?.Dispose();
        _solidAaVert?.Dispose();
        _solidFrag?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        foreach (var image in _ownedImages)
        {
            image.Dispose();
        }

        _ownedImages.Clear();
        _compiler.Dispose();

        // Releases the underlying native Noesis proxy (BaseComponent) — the `new` hide above only skips
        // the inherited signature, it does not stop this class from still calling it explicitly.
        base.Dispose();
    }
}
