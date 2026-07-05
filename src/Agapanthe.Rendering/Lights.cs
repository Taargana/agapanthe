using System.Numerics;
using System.Runtime.InteropServices;

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
    /// <summary>World-space position of the emitter.</summary>
    public Vector3 Position;

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
/// Set 0, binding 1 — the per-frame lighting block, packed for std140 as twelve <see cref="Vector4"/> at
/// contiguous 16-byte offsets (176 bytes total). Built from <see cref="SceneLights"/> via the constructor.
/// The PBR fragment shader (M5-05) declares a matching <c>layout(std140) uniform</c> in this exact order.
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
    /// Packs <paramref name="lights"/> into the std140 block. The directional direction is normalized here
    /// (a degenerate zero direction falls back to straight down). All four point-light slots are copied from
    /// <see cref="SceneLights.Points"/>; the shader ignores slots at or beyond <see cref="SceneLights.PointCount"/>.
    /// No heap allocation: the result is a value type built on the stack.
    /// </summary>
    public LightsUniforms(SceneLights lights)
    {
        ArgumentNullException.ThrowIfNull(lights);

        var dir = lights.Directional;
        var d = dir.Direction;
        d = d.LengthSquared() > 1e-12f ? Vector3.Normalize(d) : new Vector3(0f, -1f, 0f);
        DirectionalDirection = new Vector4(d, 0f);
        DirectionalColorIntensity = new Vector4(dir.Color, dir.Intensity);
        AmbientPointCount = new Vector4(lights.Ambient, lights.PointCount);

        var p = lights.Points;
        Point0PositionRange = new Vector4(p[0].Position, p[0].Range);
        Point0ColorIntensity = new Vector4(p[0].Color, p[0].Intensity);
        Point1PositionRange = new Vector4(p[1].Position, p[1].Range);
        Point1ColorIntensity = new Vector4(p[1].Color, p[1].Intensity);
        Point2PositionRange = new Vector4(p[2].Position, p[2].Range);
        Point2ColorIntensity = new Vector4(p[2].Color, p[2].Intensity);
        Point3PositionRange = new Vector4(p[3].Position, p[3].Range);
        Point3ColorIntensity = new Vector4(p[3].Color, p[3].Intensity);
    }
}
