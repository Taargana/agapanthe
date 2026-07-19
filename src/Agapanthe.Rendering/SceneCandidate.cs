using System.Numerics;
using System.Runtime.InteropServices;

namespace Agapanthe.Rendering;

/// <summary>
/// One input to the GPU scene cull (P3-M4 W1): a drawable candidate the compute shader tests against the frustum
/// planes and, if visible, appends to its batch's region of the compacted instance buffer. std430-compatible
/// (16-byte aligned, 96 bytes): mat4 at 0, vec4 sphere at 64, uint batchId at 80, padded to 96 — matching
/// <c>scene_cull.comp</c>'s <c>Candidate</c> struct.
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 96)]
internal struct SceneCandidate
{
    public Matrix4x4 Model;   // 64 — camera-relative model matrix, copied to the instance buffer if visible
    public Vector4 Sphere;    // 16 — camera-relative bounding sphere (xyz centre, w radius) for the frustum test
    public uint BatchId;      //  4 — index into the batch table; its base offset is batchBase[BatchId]
    // 12 bytes of tail padding (Size = 96) keep the std430 array stride at 96.
}
