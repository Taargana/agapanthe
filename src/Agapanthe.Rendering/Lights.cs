using System.Numerics;
using System.Runtime.InteropServices;
using Agapanthe.Core;

namespace Agapanthe.Rendering;

/// <summary>
/// A single directional (infinitely-distant, parallel-ray) light — the scene's key/sun light.
/// <para>
/// <b>Convention.</b> <see cref="Direction"/> is the direction the light <i>travels</i> (from the source
/// toward the lit surfaces), matching the glTF <c>KHR_lights_punctual</c> convention. The PBR shader
/// (M5-05) uses <c>-Direction</c> as the surface-to-light vector <c>L</c>. It need not be unit length here;
/// <see cref="LightsUniforms"/> normalizes it when packing (a zero vector falls back to straight down).
/// </para>
/// </summary>
public struct DirectionalLight
{
    /// <summary>Direction the light travels (source → surface). Normalized when packed into the UBO.</summary>
    public Vector3 Direction;

    /// <summary>Linear RGB radiance color (unclamped; the pipeline is HDR).</summary>
    public Vector3 Color;

    /// <summary>Scalar multiplier on <see cref="Color"/>. HDR values &gt; 1 are allowed.</summary>
    public float Intensity;
}

/// <summary>
/// A single omnidirectional point light with inverse-square falloff clamped by <see cref="Range"/>.
/// </summary>
public struct PointLight
{
    /// <summary>
    /// World-space position of the emitter, in <see cref="Double3"/> (spec §3.3). It is converted to
    /// camera-relative float EVERY frame when the UBO is packed: a light stored in absolute float would light
    /// the wrong place the moment the camera origin is non-zero, and would drift as the camera moves.
    /// </summary>
    public Double3 Position;

    /// <summary>Linear RGB radiance color (unclamped; the pipeline is HDR).</summary>
    public Vector3 Color;

    /// <summary>Scalar multiplier on <see cref="Color"/>. HDR values &gt; 1 are allowed.</summary>
    public float Intensity;

    /// <summary>
    /// Falloff cutoff distance in world units (glTF <c>range</c>). The shader (M5-05) fades the
    /// inverse-square attenuation to zero at this distance; <c>0</c> means "no explicit cutoff".
    /// </summary>
    public float Range;
}

/// <summary>
/// Mutable per-scene lighting state (architect decision 4): one directional key light plus up to
/// <see cref="MaxPointLights"/> point lights, and a constant ambient term. The <see cref="Renderer"/> owns
/// one instance (<see cref="Renderer.Lights"/>) that the Sandbox mutates directly; it is packed into
/// <see cref="LightsUniforms"/> and uploaded to set 0, binding 1 every frame.
/// <para>
/// <b>Defaults</b> are sane for an unconfigured scene: a white directional light of intensity 1 pointing
/// straight down, no point lights, and a low constant <see cref="Ambient"/> (a placeholder for IBL, M7).
/// </para>
/// <para>
/// <b>Zero allocation.</b> The point-light backing store is a fixed-length array allocated once; mutate it
/// in place through <see cref="Points"/> and set <see cref="PointCount"/> — no per-frame allocation.
/// </para>
/// </summary>
public sealed class SceneLights
{
    /// <summary>Maximum number of point lights the UBO holds (architect decision: fixed UBO, active count).</summary>
    public const int MaxPointLights = 4;

    private readonly PointLight[] _points = new PointLight[MaxPointLights];
    private int _pointCount;

    /// <summary>The directional key light. Defaults to white, intensity 1, pointing straight down.</summary>
    public DirectionalLight Directional = new()
    {
        Direction = new Vector3(0f, -1f, 0f),
        Color = Vector3.One,
        Intensity = 1f,
    };

    /// <summary>
    /// Constant ambient linear RGB added to every surface — a stand-in for image-based lighting until M7.
    /// Default is a low neutral grey (0.03).
    /// </summary>
    public Vector3 Ambient = new(0.03f, 0.03f, 0.03f);

    /// <summary>
    /// The fixed-length (<see cref="MaxPointLights"/>) point-light array. Mutate entries in place, e.g.
    /// <c>lights.Points[0] = new PointLight { ... }</c>, then set <see cref="PointCount"/>.
    /// </summary>
    public PointLight[] Points => _points;

    /// <summary>
    /// Number of active point lights (entries <c>0..PointCount-1</c> of <see cref="Points"/> are used).
    /// Assignments are clamped to <c>[0, <see cref="MaxPointLights"/>]</c>. Defaults to 0.
    /// </summary>
    public int PointCount
    {
        get => _pointCount;
        set => _pointCount = Math.Clamp(value, 0, MaxPointLights);
    }
}

/// <summary>
/// Set 0, binding 1 — the per-frame lighting block, packed for std140 as eleven <see cref="Vector4"/> at
/// contiguous 16-byte offsets followed by the directional light's view-projection <see cref="Matrix4x4"/>
/// (240 bytes total: 176 for the lights + 64 for the matrix). Built from <see cref="SceneLights"/> plus the
/// light-space transform via the constructor. The PBR fragment shader (M5/M6) declares a matching
/// <c>layout(std140) uniform</c> in this exact order.
/// <para>
/// <b>Packing</b> (values grouped four-to-a-vec4 so std140 does not pad each scalar to its own 16-byte slot):
/// </para>
/// <list type="bullet">
///   <item><see cref="DirectionalDirection"/> — <c>xyz</c> normalized travel direction, <c>w</c> padding.</item>
///   <item><see cref="DirectionalColorIntensity"/> — <c>rgb</c> color, <c>w</c> intensity.</item>
///   <item><see cref="AmbientPointCount"/> — <c>rgb</c> constant ambient, <c>w</c> active point-light count
///   as a float (the shader reads it as <c>int(w)</c> to bound its loop).</item>
///   <item>Then four point lights, each two vec4s: <c>PositionRange</c> (<c>xyz</c> world position,
///   <c>w</c> range) and <c>ColorIntensity</c> (<c>rgb</c> color, <c>w</c> intensity).</item>
///   <item><see cref="LightViewProj"/> (offset 176) — the directional shadow map's view·proj transform
///   (row-vector, so a world point maps to light-clip space as <c>p · LightViewProj</c>; std140 reads the
///   <c>mat4</c> column-major, i.e. transposed, so the shader multiplies <c>lightViewProj * vec4(worldPos,1)</c>
///   exactly like the camera's <c>proj * view</c>). The Vulkan Y-flip and Z[0,1] are baked in by
///   <see cref="Agapanthe.Core.MathHelpers.OrthographicVulkan"/>.</item>
/// </list>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public readonly struct LightsUniforms
{
    /// <summary>Directional light: xyz = normalized travel direction, w = padding (offset 0).</summary>
    public readonly Vector4 DirectionalDirection;

    /// <summary>Directional light: rgb = color, w = intensity (offset 16).</summary>
    public readonly Vector4 DirectionalColorIntensity;

    /// <summary>rgb = constant ambient, w = active point-light count as float (offset 32).</summary>
    public readonly Vector4 AmbientPointCount;

    /// <summary>Point light 0: xyz = world position, w = range (offset 48).</summary>
    public readonly Vector4 Point0PositionRange;

    /// <summary>Point light 0: rgb = color, w = intensity (offset 64).</summary>
    public readonly Vector4 Point0ColorIntensity;

    /// <summary>Point light 1: xyz = world position, w = range (offset 80).</summary>
    public readonly Vector4 Point1PositionRange;

    /// <summary>Point light 1: rgb = color, w = intensity (offset 96).</summary>
    public readonly Vector4 Point1ColorIntensity;

    /// <summary>Point light 2: xyz = world position, w = range (offset 112).</summary>
    public readonly Vector4 Point2PositionRange;

    /// <summary>Point light 2: rgb = color, w = intensity (offset 128).</summary>
    public readonly Vector4 Point2ColorIntensity;

    /// <summary>Point light 3: xyz = world position, w = range (offset 144).</summary>
    public readonly Vector4 Point3PositionRange;

    /// <summary>Point light 3: rgb = color, w = intensity (offset 160).</summary>
    public readonly Vector4 Point3ColorIntensity;

    /// <summary>
    /// The CSM cascades' light-space view·projections (offset 176, 4×64 = 256 bytes; P3-M5). Cascade <c>i</c> maps a
    /// camera-relative point into the light clip space of atlas tile <c>i</c>; the fragment shader picks the cascade
    /// from the fragment's view depth (see <see cref="CascadeSplits"/>), then derives the tile UV and reference depth.
    /// </summary>
    public readonly Matrix4x4 LightViewProj0;
    public readonly Matrix4x4 LightViewProj1;
    public readonly Matrix4x4 LightViewProj2;
    public readonly Matrix4x4 LightViewProj3;

    /// <summary>
    /// The far VIEW-SPACE depth of each cascade (offset 432, 16 bytes; P3-M5), x→cascade 0 … w→cascade 3. The
    /// fragment shader selects the first cascade whose split depth exceeds the fragment's view depth.
    /// </summary>
    public readonly Vector4 CascadeSplits;

    /// <summary>
    /// Shadow parameters (offset 448, 16 bytes; P3-M5): <c>x</c> = the view depth at which the distance fade-out
    /// starts, <c>yzw</c> reserved. The fade hides the "shadow horizon" when the range is one WE chose
    /// (<c>Cascades.MaxDistance</c> / <c>ShadowDistance</c>); when the range is instead imposed by the camera's far
    /// plane there is no horizon to hide — the geometry is clipped there anyway — so the renderer sends a value
    /// beyond the range and the fade never triggers (audit MAJEUR-2).
    /// </summary>
    public readonly Vector4 ShadowParams;

    /// <summary>
    /// Packs <paramref name="lights"/>, the CSM <paramref name="cascades"/> and their <paramref name="splits"/> into
    /// the std140 block. The directional direction is normalized here (a degenerate zero direction falls back to
    /// straight down). All four point-light slots are copied from <see cref="SceneLights.Points"/>; the shader
    /// ignores slots at or beyond <see cref="SceneLights.PointCount"/>. No heap allocation: the result is a value
    /// type built on the stack.
    /// </summary>
    public LightsUniforms(
        SceneLights lights, ReadOnlySpan<Matrix4x4> cascades, Vector4 splits, float fadeStart, Double3 origin)
    {
        ArgumentNullException.ThrowIfNull(lights);

        var dir = lights.Directional;
        var d = dir.Direction;
        d = d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : new Vector3(0f, -1f, 0f);
        DirectionalDirection = new Vector4(d, 0f);
        DirectionalColorIntensity = new Vector4(dir.Color, dir.Intensity);
        AmbientPointCount = new Vector4(lights.Ambient, lights.PointCount);

        // Camera-relative narrow, redone every frame against THIS frame's origin (spec §3.3). The shader compares
        // these against camera-relative surface positions, so both sides must have had the same origin removed.
        var p = lights.Points;
        Point0PositionRange = new Vector4(p[0].Position.ToVector3(origin), p[0].Range);
        Point0ColorIntensity = new Vector4(p[0].Color, p[0].Intensity);
        Point1PositionRange = new Vector4(p[1].Position.ToVector3(origin), p[1].Range);
        Point1ColorIntensity = new Vector4(p[1].Color, p[1].Intensity);
        Point2PositionRange = new Vector4(p[2].Position.ToVector3(origin), p[2].Range);
        Point2ColorIntensity = new Vector4(p[2].Color, p[2].Intensity);
        Point3PositionRange = new Vector4(p[3].Position.ToVector3(origin), p[3].Range);
        Point3ColorIntensity = new Vector4(p[3].Color, p[3].Intensity);

        // Fewer cascades than the atlas holds → the unused slots repeat the last one, so a fragment that somehow
        // selects them still samples a valid cascade instead of an identity matrix (which would read tile garbage).
        LightViewProj0 = cascades.Length > 0 ? cascades[0] : Matrix4x4.Identity;
        LightViewProj1 = cascades.Length > 1 ? cascades[1] : LightViewProj0;
        LightViewProj2 = cascades.Length > 2 ? cascades[2] : LightViewProj1;
        LightViewProj3 = cascades.Length > 3 ? cascades[3] : LightViewProj2;
        CascadeSplits = splits;
        ShadowParams = new Vector4(fadeStart, 0f, 0f, 0f);
    }
}
