#if !MASTER
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Agapanthe.Graphics;
using Hexa.NET.ImGui;

namespace Agapanthe.DebugUi;

/// <summary>
/// The ImGui debug-overlay Vulkan backend (D4) — home-grown on top of <see cref="Agapanthe.Graphics"/>'s public
/// primitives, never a type from <c>Hexa.NET.ImGui</c>'s own reference backends (those want raw <c>VkDevice</c>/
/// <c>VkCommandBuffer</c>, which <see cref="Agapanthe.Graphics"/> never lets out).
/// <para>
/// Vertices live in a per-frame SSBO ring, read via <c>gl_VertexIndex</c> — same technique as <c>ui.vert</c>, but
/// this time paired with a REAL GPU index buffer: Vulkan defines <c>gl_VertexIndex</c> for an indexed draw as
/// <c>indexBuffer[element] + vertexOffset</c>, so <c>ImDrawCmd.VtxOffset</c> maps directly onto
/// <see cref="CommandList.DrawIndexed"/>'s own <c>vertexOffset</c> parameter.
/// </para>
/// <para>
/// v1 texture scope (D4): exactly one texture, the font atlas. <c>ImDrawData.Textures</c>' general multi-texture
/// machinery (the ImGui 1.92+ dynamic-texture-update API — confirmed by reflection at AW-007a, no classic
/// upfront atlas-upload API exists anymore) is handled minimally: create/update always re-uploads the whole
/// texture (no partial-rect updates), and every draw command binds the same one descriptor set — no per-command
/// texture switching, since nothing else is ever created in this scope.
/// </para>
/// </summary>
internal sealed unsafe class ImGuiVulkanBackend : IDisposable
{
    // Real bug found live (first Sandbox run: garbled/shifted geometry, no validation error) — GLSL std430 rounds
    // a struct's array stride up to the alignment of its largest member (vec2 = 8 bytes here): {vec2,vec2,uint}
    // is 20 bytes of DATA but a 24-byte STRIDE. The CLR packs this struct tightly at 20 bytes with no padding
    // (confirmed via Unsafe.SizeOf), so vertex N>0 silently read from the wrong offset — every vertex after the
    // first drifted further. The exact trap ui.vert's own comment already warns about ("the params vector is
    // what makes the 48-byte std430 stride match the CPU struct") — missed once anyway. Explicit 4-byte pad.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Vertex(Vector2 Pos, Vector2 Uv, uint Col, uint Pad);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct PushConstants(Vector2 InvScreenSize);

    private readonly GraphicsDevice _device;
    private readonly DescriptorSetLayout _setLayout;
    private readonly GraphicsPipeline _pipeline;
    private readonly GpuBuffer?[] _vertexBuffers = new GpuBuffer?[GraphicsDevice.FramesInFlight];
    private readonly GpuBuffer?[] _indexBuffers = new GpuBuffer?[GraphicsDevice.FramesInFlight];
    private GpuImage? _fontAtlas;
    private Sampler? _fontSampler;
    private bool _disposed;

    public ImGuiVulkanBackend(GraphicsDevice device, PixelFormat colorFormat)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;

        _setLayout = new DescriptorSetLayout(
            device,
            [
                new DescriptorBinding(0, DescriptorKind.CombinedImageSampler, ShaderStages.Fragment),
                new DescriptorBinding(1, DescriptorKind.StorageBuffer, ShaderStages.Vertex),
            ]);

        try
        {
            var vertSpirv = ReadEmbeddedSpirv("imgui.vert.spv");
            var fragSpirv = ReadEmbeddedSpirv("imgui.frag.spv");
            using var vertModule = new ShaderModule(device, vertSpirv, ShaderStage.Vertex);
            using var fragModule = new ShaderModule(device, fragSpirv, ShaderStage.Fragment);

            _pipeline = new GraphicsPipeline(device, new GraphicsPipelineDesc
            {
                VertexShader = vertModule,
                FragmentShader = fragModule,
                // No vertex buffer: vertices come from the SSBO via gl_VertexIndex (patron ui.vert).
                VertexLayout = null,
                SetLayouts = [_setLayout],
                PushConstants = [new PushConstantRange(0, (uint)Unsafe.SizeOf<PushConstants>(), ShaderStages.Vertex)],
                ColorFormat = colorFormat,
                DepthTest = false,
                Cull = CullMode.None,
                // The shader outputs linear RGB already multiplied by alpha (patron ui.vert/ui.frag).
                Blend = BlendMode.PremultipliedAlpha,
            });
        }
        catch
        {
            _setLayout.Dispose();
            throw;
        }
    }

    private static byte[] ReadEmbeddedSpirv(string logicalName)
    {
        using var stream = typeof(ImGuiVulkanBackend).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{logicalName}' not found in Agapanthe.DebugUi — the shader precompile " +
                "target did not run or its LogicalName does not match.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Walks <c>drawData.Textures</c> and creates/updates/destroys the font atlas. Call once per frame,
    /// before <see cref="Render"/>, only while no command buffer is being recorded against this backend's
    /// resources (texture (re)creation here is synchronous, patron <see cref="Rendering.FontResources"/>).</summary>
    public void UpdateTextures(ImDrawDataPtr drawData)
    {
        var textures = drawData.Textures;
        if (textures.Data is null)
        {
            return;
        }

        for (var i = 0; i < textures.Size; i++)
        {
            var tex = textures[i];
            switch (tex.Status)
            {
                case ImTextureStatus.WantCreate:
                case ImTextureStatus.WantUpdates: // v1 simplification: full re-upload, no partial-rect updates
                    CreateOrReplaceTexture(tex);
                    break;
                case ImTextureStatus.WantDestroy:
                    DestroyTexture();
                    tex.SetStatus(ImTextureStatus.Destroyed);
                    break;
            }
        }
    }

    private void CreateOrReplaceTexture(ImTextureDataPtr tex)
    {
        // Real finding, live (imgui.frag's own comment): this ImGui version's default font atlas is actually
        // Rgba32, not the Alpha8 single-channel coverage originally assumed — both branches map correctly to a
        // GpuImage format, the fragment shader modulates the full RGBA sample so either is handled correctly.
        var format = tex.Format == ImTextureFormat.Alpha8 ? PixelFormat.R8Unorm : PixelFormat.Rgba8Unorm;

        DestroyTexture();

        _fontAtlas = new GpuImage(
            _device, (uint)tex.Width, (uint)tex.Height, format,
            ImageUsage.Sampled | ImageUsage.TransferDst, mipLevels: 1);

        try
        {
            using var uploader = new GpuUploader(_device);
            var pixels = new ReadOnlySpan<byte>(tex.GetPixels(), tex.GetSizeInBytes());
            uploader.Upload(_fontAtlas, pixels);

            // Linear filtering: same reasoning as FontResources — smooths the SDF/coverage edge between texels.
            // ClampToEdge: a glyph at the atlas border must not wrap and sample another glyph.
            _fontSampler = new Sampler(_device, new SamplerDesc(
                Filter: SamplerFilter.Linear, MipFilter: SamplerFilter.Nearest,
                AddressMode: SamplerAddressMode.ClampToEdge));
        }
        catch
        {
            _fontAtlas.Dispose();
            _fontAtlas = null;
            throw;
        }

        // v1: a single texture only, never routed by ID — the handle value itself is unused, but ImGui's internal
        // texture bookkeeping still wants a non-null one after Status=Ok.
        tex.SetTexID((ImTextureID)1UL);
        tex.SetStatus(ImTextureStatus.Ok);
    }

    private void DestroyTexture()
    {
        _fontSampler?.Dispose();
        _fontSampler = null;
        _fontAtlas?.Dispose();
        _fontAtlas = null;
    }

    /// <summary>Records the draw commands for one frame's <see cref="ImDrawDataPtr"/>, opening and closing its
    /// own render pass instance (patron <c>Renderer.DrawUi</c> — a real bug found live at first run: an earlier
    /// version drew without ever calling <see cref="CommandList.BeginRendering"/>, a validation-caught
    /// <c>vkCmdDrawIndexed</c>-outside-a-render-pass fault). No-op when there is nothing to draw or no texture
    /// is ready yet (the very first frame, before <see cref="UpdateTextures"/> has had a chance to create the
    /// atlas).</summary>
    public void Render(CommandList cmd, FrameContext frame, int slot, SwapchainTarget target, ImDrawDataPtr drawData)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var framebufferWidth = target.Width;
        var framebufferHeight = target.Height;
        if (_fontAtlas is null || _fontSampler is null || drawData.CmdListsCount == 0 || framebufferWidth == 0 || framebufferHeight == 0)
        {
            return;
        }

        var totalVtx = Math.Max(drawData.TotalVtxCount, 1);
        var totalIdx = Math.Max(drawData.TotalIdxCount, 1);
        var vertexBuffer = EnsureBuffer(_vertexBuffers, slot, totalVtx, (ulong)Unsafe.SizeOf<Vertex>(), BufferUsage.Storage);
        var indexBuffer = EnsureBuffer(_indexBuffers, slot, totalIdx, sizeof(ushort), BufferUsage.Index);

        var vtxDst = vertexBuffer.MappedSpan<Vertex>(totalVtx);
        var idxDst = indexBuffer.MappedSpan<ushort>(totalIdx);
        var vtxWritten = 0;
        var idxWritten = 0;

        var set = frame.AllocateSet(_setLayout);
        frame.WriteCombinedImageSampler(set, 0, _fontAtlas, _fontSampler);
        frame.WriteStorageBuffer(set, 1, vertexBuffer);

        // The pass just before this one (the UI overlay pass, or the tonemap if UI drew nothing) already wrote
        // this image in its own render pass instance — patron Renderer.DrawUi's own ColorAttachmentBarrier.
        cmd.ColorAttachmentBarrier(target.View);
        cmd.BeginRendering(new RenderingAttachments
        {
            Color = new ColorAttachmentInfo
            {
                Target = target.View,
                LoadOp = AttachmentLoadAction.Load,
            },
            Width = framebufferWidth,
            Height = framebufferHeight,
        });

        cmd.SetViewportScissor(framebufferWidth, framebufferHeight);
        cmd.BindPipeline(_pipeline);
        cmd.BindDescriptorSet(_pipeline, 0, set);
        var push = new PushConstants(new Vector2(1f / framebufferWidth, 1f / framebufferHeight));
        cmd.PushConstants(_pipeline, ShaderStages.Vertex, in push);
        cmd.BindIndexBuffer(indexBuffer, IndexFormat.UInt16);

        for (var listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            var list = drawData.CmdLists[listIndex];

            var srcVerts = new ReadOnlySpan<ImDrawVert>(list.VtxBuffer.Data, list.VtxBuffer.Size);
            for (var i = 0; i < srcVerts.Length; i++)
            {
                var v = srcVerts[i];
                vtxDst[vtxWritten + i] = new Vertex(v.Pos, v.Uv, v.Col, 0);
            }

            var srcIdx = new ReadOnlySpan<ushort>(list.IdxBuffer.Data, list.IdxBuffer.Size);
            srcIdx.CopyTo(idxDst.Slice(idxWritten, srcIdx.Length));

            for (var c = 0; c < list.CmdBuffer.Size; c++)
            {
                var drawCmd = list.CmdBuffer[c];
                if (drawCmd.ElemCount == 0)
                {
                    continue;
                }

                // Clamp to the framebuffer (D4): a raw ImGui clip rect can be negative or exceed the framebuffer
                // (a partially off-screen window) — a negative Offset2D is invalid to Vulkan.
                var clip = drawCmd.ClipRect;
                var x = Math.Max(0, (int)clip.X);
                var y = Math.Max(0, (int)clip.Y);
                var x1 = Math.Min((int)framebufferWidth, (int)clip.Z);
                var y1 = Math.Min((int)framebufferHeight, (int)clip.W);
                var w = (uint)Math.Max(0, x1 - x);
                var h = (uint)Math.Max(0, y1 - y);
                if (w == 0 || h == 0)
                {
                    continue;
                }

                cmd.SetScissor(x, y, w, h);
                cmd.DrawIndexed(
                    drawCmd.ElemCount, 1,
                    (uint)idxWritten + drawCmd.IdxOffset,
                    vtxWritten + (int)drawCmd.VtxOffset,
                    0);
            }

            vtxWritten += srcVerts.Length;
            idxWritten += srcIdx.Length;
        }

        cmd.EndRendering();
    }

    private GpuBuffer EnsureBuffer(GpuBuffer?[] ring, int slot, int count, ulong stride, BufferUsage usage)
    {
        var needed = (ulong)count * stride;
        var existing = ring[slot];
        if (existing is not null && existing.SizeBytes >= needed)
        {
            return existing;
        }

        var newSize = existing is null ? needed : existing.SizeBytes * 2;
        if (newSize < needed)
        {
            newSize = needed;
        }

        existing?.Dispose(); // deferred N+2 by the deletion queue
        var buffer = new GpuBuffer(_device, newSize, usage);
        ring[slot] = buffer;
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyTexture();
        for (var i = 0; i < _vertexBuffers.Length; i++)
        {
            _vertexBuffers[i]?.Dispose();
            _indexBuffers[i]?.Dispose();
        }

        _pipeline.Dispose();
        _setLayout.Dispose();
    }
}
#endif
