using System.Runtime.InteropServices;
using Noesis;

namespace NoesisAotProbe;

// Diagnostic-only device (never a real backend): dumps every Batch's raw fields — including the
// VertexUniform0/1 / PixelUniform0/1 byte content — to reverse-engineer the exact layout Noesis
// expects for Path_Solid/Path_AA_Solid, since that is only documented in the Native SDK's reference
// shader source (not available via the Managed/NuGet packages). Implements just enough of the
// abstract contract to not crash; correctness of the (non-existent) rendering is irrelevant here.
public sealed class LoggingRenderDevice : RenderDevice
{
    public override DeviceCaps Caps => new()
    {
        CenterPixelOffset = 0f,
        LinearRendering = false,
        SubpixelRendering = false,
        DepthRangeZeroToOne = true,
        ClipSpaceYInverted = false,
    };

    public override RenderTarget CreateRenderTarget(string label, uint width, uint height, uint sampleCount, bool needsStencil)
    {
        Console.WriteLine($"CreateRenderTarget label={label} {width}x{height} samples={sampleCount} stencil={needsStencil}");
        return new LoggingRenderTarget(new LoggingTexture(width, height));
    }

    public override RenderTarget CloneRenderTarget(string label, RenderTarget surface)
    {
        Console.WriteLine($"CloneRenderTarget label={label}");
        return new LoggingRenderTarget(new LoggingTexture(surface.Texture.Width, surface.Texture.Height));
    }

    public override void SetRenderTarget(RenderTarget surface) => Console.WriteLine("SetRenderTarget");

    public override void BeginTile(RenderTarget surface, Tile tile) { }
    public override void EndTile(RenderTarget surface) { }
    public override void ResolveRenderTarget(RenderTarget surface, Tile[] tiles) { }

    public override Texture CreateTexture(string label, uint width, uint height, uint numLevels, TextureFormat format, IntPtr data)
    {
        Console.WriteLine($"CreateTexture label={label} {width}x{height} levels={numLevels} format={format} hasData={data != IntPtr.Zero}");
        return new LoggingTexture(width, height);
    }

    public override void UpdateTexture(Texture texture, uint level, uint x, uint y, uint width, uint height, IntPtr data)
    {
        Console.WriteLine($"UpdateTexture level={level} at=({x},{y}) size={width}x{height}");
    }

    public override void BeginOffscreenRender() => Console.WriteLine("BeginOffscreenRender");
    public override void EndOffscreenRender() => Console.WriteLine("EndOffscreenRender");
    public override void BeginOnscreenRender() => Console.WriteLine("BeginOnscreenRender");
    public override void EndOnscreenRender() => Console.WriteLine("EndOnscreenRender");

    private byte[] _vertices = new byte[1 << 20];
    private byte[] _indices = new byte[1 << 20];
    private GCHandle _vertexHandle;
    private GCHandle _indexHandle;

    public override IntPtr MapVertices(uint bytes)
    {
        if (bytes > _vertices.Length) _vertices = new byte[bytes];
        _vertexHandle = GCHandle.Alloc(_vertices, GCHandleType.Pinned);
        return _vertexHandle.AddrOfPinnedObject();
    }

    public override void UnmapVertices() => _vertexHandle.Free();

    public override IntPtr MapIndices(uint bytes)
    {
        if (bytes > _indices.Length) _indices = new byte[bytes];
        _indexHandle = GCHandle.Alloc(_indices, GCHandleType.Pinned);
        return _indexHandle.AddrOfPinnedObject();
    }

    public override void UnmapIndices() => _indexHandle.Free();

    private int _batchCount;

    public override void DrawBatch(ref Batch batch)
    {
        _batchCount++;
        Console.WriteLine(
            $"--- Batch #{_batchCount}: Shader={batch.Shader.Name} RenderState(Blend={batch.RenderState.BlendMode},Stencil={batch.RenderState.StencilMode}) " +
            $"VertexOffset={batch.VertexOffset} NumVertices={batch.NumVertices} StartIndex={batch.StartIndex} NumIndices={batch.NumIndices}");

        DumpUniform("VertexUniform0", batch.VertexUniform0);
        DumpUniform("VertexUniform1", batch.VertexUniform1);
        DumpUniform("PixelUniform0", batch.PixelUniform0);
        DumpUniform("PixelUniform1", batch.PixelUniform1);

        // Dump the first 2 vertices raw: PosColor is 12 bytes (2 floats pos + 4 bytes color).
        for (var i = 0; i < Math.Min(2u, batch.NumVertices); i++)
        {
            var off = (int)(batch.VertexOffset + (uint)i * 12);
            var x = BitConverter.ToSingle(_vertices, off);
            var y = BitConverter.ToSingle(_vertices, off + 4);
            var r = _vertices[off + 8];
            var g = _vertices[off + 9];
            var b = _vertices[off + 10];
            var a = _vertices[off + 11];
            Console.WriteLine($"    vertex[{i}]: pos=({x},{y}) colorBytes=({r},{g},{b},{a})");
        }
    }

    private static void DumpUniform(string name, UniformData data)
    {
        if (data.Values == IntPtr.Zero || data.NumWords == 0)
        {
            Console.WriteLine($"    {name}: (empty)");
            return;
        }

        var floats = new float[data.NumWords];
        System.Runtime.InteropServices.Marshal.Copy(data.Values, floats, 0, (int)data.NumWords);
        Console.WriteLine($"    {name}: NumWords={data.NumWords} Hash={data.Hash} floats=[{string.Join(", ", floats)}]");
    }
}

public sealed class LoggingTexture(uint width, uint height) : Texture
{
    public override uint Width => width;
    public override uint Height => height;
    public override bool HasMipMaps => false;
    public override bool IsInverted => false;
    public override bool HasAlpha => true;
}

public sealed class LoggingRenderTarget(Texture texture) : RenderTarget
{
    public override Texture Texture => texture;
}
