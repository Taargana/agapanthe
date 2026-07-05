using System.Numerics;
using System.Runtime.InteropServices;

namespace Agapanthe.Rendering;

/// <summary>
/// Set 0, binding 0 — the per-frame camera block. Two column-major-consumed <see cref="Matrix4x4"/>
/// (view, projection), 128 bytes, matching the <c>CameraUbo { mat4 view; mat4 proj; }</c> declared in
/// <c>shaders/mesh.vert</c>.
/// <para>
/// <b>Convention.</b> The matrices are stored in <c>System.Numerics</c> row-vector form. std140 reads a
/// <c>mat4</c> column-major, i.e. as the transpose of the CPU layout, which is exactly what turns the
/// row-vector matrices into the column-vector transposes the shader multiplies as
/// <c>proj * view * model * position</c> (see the matching note in <c>mesh.vert</c>). The Vulkan clip-space
/// quirks (Y flip, depth [0,1]) already live inside <see cref="Camera.ProjectionMatrix"/>.
/// </para>
/// <para>
/// This is a deliberate duplicate of the struct previously wired by hand in the Sandbox; ownership of the
/// camera UBO moves into <see cref="Renderer"/> here (M4-10) and the Sandbox stops declaring its own copy
/// when it migrates onto the Renderer (M4-11).
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct CameraUniforms
{
    /// <summary>View matrix (offset 0, 64 bytes).</summary>
    public readonly Matrix4x4 View;

    /// <summary>Projection matrix (offset 64, 64 bytes).</summary>
    public readonly Matrix4x4 Proj;

    /// <summary>Eye position in world space: xyz + padding (offset 128, 16 bytes — total 144).
    /// The PBR shader derives the view vector V from it (spec §3.4).</summary>
    public readonly Vector4 Position;

    public CameraUniforms(Matrix4x4 view, Matrix4x4 proj, Vector3 position)
    {
        View = view;
        Proj = proj;
        Position = new Vector4(position, 0f);
    }
}
