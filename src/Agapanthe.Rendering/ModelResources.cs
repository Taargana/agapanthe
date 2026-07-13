using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// What one loaded model contributes: its GPU resources (owned by the <see cref="ResourceRegistry"/> once
/// registered) and one <see cref="MeshEntry"/> per drawable. Internal — it is the hand-off between
/// <see cref="SceneBuilder"/> (which creates GPU objects) and the registry (which assigns the global handles),
/// so the builder no longer needs to know how handles are minted.
/// </summary>
internal sealed record ModelResources(
    Mesh[] Meshes,
    Material[] Materials,
    GpuImage[] Textures,
    GpuImage[] Placeholders,
    SamplerCache SamplerCache,
    DescriptorAllocator MaterialAllocator,
    string Name,
    MeshEntry[] Entries);

/// <summary>
/// One drawable of a model, in terms LOCAL to that model: the index into its own material array (already
/// resolved — an absent/out-of-range glTF material index points at the engine default), its baked transform split
/// into a <see cref="Double3"/> position and the rotation/scale matrix (spec §3.3 — the position must stay in
/// double so the model can be placed far from the world origin), and its bounds (float vertex fold, widened).
/// Positions and bounds are relative to the model's own origin; the registry offsets them by the world origin the
/// model is loaded at, and turns the local material index into a global <see cref="MaterialHandle"/>.
/// </summary>
internal readonly record struct MeshEntry(
    int LocalMaterialIndex,
    Double3 Position,
    Matrix4x4 RotationScale,
    Double3 BoundsMin,
    Double3 BoundsMax);
