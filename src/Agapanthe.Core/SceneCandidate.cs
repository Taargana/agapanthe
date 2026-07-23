using System.Numerics;
using System.Runtime.InteropServices;

namespace Agapanthe.Core;

/// <summary>
/// One input to the GPU culls (P3-M6): a drawable candidate the compute shaders test against the frustum planes
/// and, if kept, append to a batch region of a compacted instance buffer. Lives in <c>Agapanthe.Core</c> (GPU-free,
/// blittable math like <see cref="RenderItem"/>) so the <c>GameWorld</c> can fill it without referencing the
/// graphics layer — it is the payload of the persistent candidate buffer the World maintains and the Renderer
/// uploads. std430-compatible (16-byte aligned, 96 bytes), matching <c>scene_cull.comp</c> / <c>shadow_cull.comp</c>'s
/// <c>Candidate</c> struct.
/// <para>
/// The scene cull batches material-major (<see cref="SceneBatchId"/>); the shadow cull batches mesh-major
/// (<see cref="ShadowBatchId"/>) and skips non-casters via <see cref="Flags"/> bit 0. All three static fields are
/// set at a structural rebuild and stay constant until the next one — a per-entity dirty patch only rewrites
/// <see cref="Model"/> and <see cref="Sphere"/>.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 96)]
public struct SceneCandidate
{
    /// <summary><see cref="Flags"/> bit set when the drawable casts a shadow (not tagged <c>NoShadowCast</c>).</summary>
    public const uint FlagCastsShadow = 1u;

    public Matrix4x4 Model;   // 64 — origin-relative model matrix (ComposeModel output), copied to the instance buffer if kept
    public Vector4 Sphere;    // 16 — origin-relative bounding sphere (xyz centre, w radius) for the frustum tests
    public uint SceneBatchId; //  4 — material-major batch index (scene cull); its base is batchBase[SceneBatchId]
    public uint ShadowBatchId;//  4 — mesh-major batch index (shadow cull); its per-cascade base folds in meshBatchBase
    public uint Flags;        //  4 — bit 0 = casts shadow
    public uint Pad0;         //  4 — keeps the std430 array stride at 96
}
