using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

// White-box parsing tests (M4-04) live in the Agapanthe.Tests assembly and assert on the raw
// schema (accessor/bufferView/mesh counts, GLB chunks). The schema stays internal — it is an
// implementation detail of the loader, consumed only by M4-05/06 in this same assembly — so the
// test project is granted access rather than widening the public surface.
[assembly: InternalsVisibleTo("Agapanthe.Tests")]

namespace Agapanthe.Assets.Gltf;

// ---------------------------------------------------------------------------------------------
// Raw glTF 2.0 JSON schema (Agapanthe.Assets.Gltf, internal).
//
// These POCOs mirror the JSON 1:1 — no geometry decoding, no node flattening, no material
// mapping (those are M4-05/06). Deserialization is done with a System.Text.Json
// source-generated context (see GltfJsonContext): reflection-free, trim/AOT-safe and clean under
// TreatWarningsAsErrors, and far less error-prone than hand-walking JsonDocument across the ~15
// node types of the schema.
//
// Defaults strategy (spec: "champs absents → défauts glTF"):
//   * The System.Text.Json SOURCE GENERATOR does NOT honour C# property initializers on
//     deserialization (verified: an absent "mode" reads back as 0, not a `= 4` default — a known
//     STJ source-gen limitation, unlike the reflection serializer). So any glTF default that is not
//     already default(T) is modelled as a nullable JSON-bound property plus a [JsonIgnore] computed
//     accessor that coalesces to the spec default. Consumers read the clean, non-null accessor.
//   * Defaults that ARE default(T) (byteOffset 0, texCoord 0, normalized false) need no such dance —
//     an absent key already yields the right value.
//   * Optional *references* where absence is semantically meaningful stay nullable (int?):
//     indices, material, mesh, bufferView, source, sampler, byteStride. Null = "not provided".
//   * Factor arrays that may be entirely absent (baseColorFactor, emissiveFactor) stay nullable;
//     the documented glTF default is applied by the consumer (M4-06), keeping this layer a faithful
//     raw view.
//
// Arrays (T[]) rather than IReadOnlyList<T> are used for collection properties so the
// source generator emits straightforward, allocation-predictable converters.
// ---------------------------------------------------------------------------------------------

internal sealed class GltfRoot
{
    [JsonPropertyName("asset")] public GltfAsset? Asset { get; init; }
    [JsonPropertyName("scene")] public int? Scene { get; init; }
    [JsonPropertyName("scenes")] public GltfScene[]? Scenes { get; init; }
    [JsonPropertyName("nodes")] public GltfNode[]? Nodes { get; init; }
    [JsonPropertyName("meshes")] public GltfMesh[]? Meshes { get; init; }
    [JsonPropertyName("accessors")] public GltfAccessor[]? Accessors { get; init; }
    [JsonPropertyName("bufferViews")] public GltfBufferView[]? BufferViews { get; init; }
    [JsonPropertyName("buffers")] public GltfBuffer[]? Buffers { get; init; }
    [JsonPropertyName("materials")] public GltfMaterial[]? Materials { get; init; }
    [JsonPropertyName("textures")] public GltfTexture[]? Textures { get; init; }
    [JsonPropertyName("samplers")] public GltfSampler[]? Samplers { get; init; }
    [JsonPropertyName("images")] public GltfImage[]? Images { get; init; }
    [JsonPropertyName("extensionsUsed")] public string[]? ExtensionsUsed { get; init; }
    [JsonPropertyName("extensionsRequired")] public string[]? ExtensionsRequired { get; init; }
}

internal sealed class GltfAsset
{
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("minVersion")] public string? MinVersion { get; init; }
    [JsonPropertyName("generator")] public string? Generator { get; init; }
}

internal sealed class GltfScene
{
    [JsonPropertyName("nodes")] public int[]? Nodes { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

internal sealed class GltfNode
{
    [JsonPropertyName("children")] public int[]? Children { get; init; }
    /// <summary>Column-major 4x4 (16 floats) when present; mutually exclusive with TRS.</summary>
    [JsonPropertyName("matrix")] public float[]? Matrix { get; init; }
    [JsonPropertyName("translation")] public float[]? Translation { get; init; }
    /// <summary>Quaternion (x, y, z, w).</summary>
    [JsonPropertyName("rotation")] public float[]? Rotation { get; init; }
    [JsonPropertyName("scale")] public float[]? Scale { get; init; }
    [JsonPropertyName("mesh")] public int? Mesh { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

internal sealed class GltfMesh
{
    [JsonPropertyName("primitives")] public GltfPrimitive[]? Primitives { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

internal sealed class GltfPrimitive
{
    [JsonPropertyName("attributes")] public GltfAttributes? Attributes { get; init; }
    [JsonPropertyName("indices")] public int? Indices { get; init; }
    [JsonPropertyName("material")] public int? Material { get; init; }
    [JsonPropertyName("mode")] public int? RawMode { get; init; }

    /// <summary>Primitive topology, defaulted. glTF default 4 (TRIANGLES); M4-05 only accepts 4.</summary>
    [JsonIgnore] public int Mode => RawMode ?? 4;
}

internal sealed class GltfAttributes
{
    [JsonPropertyName("POSITION")] public int? Position { get; init; }
    [JsonPropertyName("NORMAL")] public int? Normal { get; init; }
    [JsonPropertyName("TANGENT")] public int? Tangent { get; init; }
    [JsonPropertyName("TEXCOORD_0")] public int? TexCoord0 { get; init; }
}

internal sealed class GltfAccessor
{
    [JsonPropertyName("bufferView")] public int? BufferView { get; init; }
    [JsonPropertyName("byteOffset")] public int ByteOffset { get; init; }
    /// <summary>GL component type: 5120 BYTE, 5121 UBYTE, 5122 SHORT, 5123 USHORT, 5125 UINT, 5126 FLOAT.</summary>
    [JsonPropertyName("componentType")] public int ComponentType { get; init; }
    [JsonPropertyName("normalized")] public bool Normalized { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; }
    /// <summary>"SCALAR", "VEC2", "VEC3", "VEC4", "MAT4"...</summary>
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("min")] public float[]? Min { get; init; }
    [JsonPropertyName("max")] public float[]? Max { get; init; }
}

internal sealed class GltfBufferView
{
    [JsonPropertyName("buffer")] public int Buffer { get; init; }
    [JsonPropertyName("byteOffset")] public int ByteOffset { get; init; }
    [JsonPropertyName("byteLength")] public int ByteLength { get; init; }
    /// <summary>Null means tightly packed (stride = element size).</summary>
    [JsonPropertyName("byteStride")] public int? ByteStride { get; init; }
    /// <summary>34962 ARRAY_BUFFER (vertex) / 34963 ELEMENT_ARRAY_BUFFER (index); optional hint.</summary>
    [JsonPropertyName("target")] public int? Target { get; init; }
}

internal sealed class GltfBuffer
{
    /// <summary>Relative file path, <c>data:</c> URI, or null for the GLB embedded buffer (index 0).</summary>
    [JsonPropertyName("uri")] public string? Uri { get; init; }
    [JsonPropertyName("byteLength")] public int ByteLength { get; init; }
}

internal sealed class GltfMaterial
{
    [JsonPropertyName("pbrMetallicRoughness")] public GltfPbrMetallicRoughness? PbrMetallicRoughness { get; init; }
    [JsonPropertyName("normalTexture")] public GltfNormalTextureInfo? NormalTexture { get; init; }
    [JsonPropertyName("occlusionTexture")] public GltfOcclusionTextureInfo? OcclusionTexture { get; init; }
    [JsonPropertyName("emissiveTexture")] public GltfTextureInfo? EmissiveTexture { get; init; }
    /// <summary>Null = glTF default (0,0,0).</summary>
    [JsonPropertyName("emissiveFactor")] public float[]? EmissiveFactor { get; init; }
    [JsonPropertyName("alphaMode")] public string? RawAlphaMode { get; init; }
    [JsonPropertyName("alphaCutoff")] public float? RawAlphaCutoff { get; init; }
    [JsonPropertyName("extensions")] public GltfMaterialExtensions? Extensions { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }

    /// <summary>"OPAQUE" (default), "MASK", or "BLEND".</summary>
    [JsonIgnore] public string AlphaMode => RawAlphaMode ?? "OPAQUE";
    /// <summary>Alpha-test threshold, defaulted. glTF default 0.5.</summary>
    [JsonIgnore] public float AlphaCutoff => RawAlphaCutoff ?? 0.5f;
}

internal sealed class GltfPbrMetallicRoughness
{
    /// <summary>Null = glTF default (1,1,1,1).</summary>
    [JsonPropertyName("baseColorFactor")] public float[]? BaseColorFactor { get; init; }
    [JsonPropertyName("baseColorTexture")] public GltfTextureInfo? BaseColorTexture { get; init; }
    [JsonPropertyName("metallicFactor")] public float? RawMetallicFactor { get; init; }
    [JsonPropertyName("roughnessFactor")] public float? RawRoughnessFactor { get; init; }
    [JsonPropertyName("metallicRoughnessTexture")] public GltfTextureInfo? MetallicRoughnessTexture { get; init; }

    /// <summary>Metalness, defaulted. glTF default 1.</summary>
    [JsonIgnore] public float MetallicFactor => RawMetallicFactor ?? 1f;
    /// <summary>Roughness, defaulted. glTF default 1.</summary>
    [JsonIgnore] public float RoughnessFactor => RawRoughnessFactor ?? 1f;
}

internal class GltfTextureInfo
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("texCoord")] public int TexCoord { get; init; }
}

internal sealed class GltfNormalTextureInfo : GltfTextureInfo
{
    [JsonPropertyName("scale")] public float? RawScale { get; init; }
    /// <summary>Normal-map scale, defaulted. glTF default 1.</summary>
    [JsonIgnore] public float Scale => RawScale ?? 1f;
}

internal sealed class GltfOcclusionTextureInfo : GltfTextureInfo
{
    [JsonPropertyName("strength")] public float? RawStrength { get; init; }
    /// <summary>Occlusion strength, defaulted. glTF default 1.</summary>
    [JsonIgnore] public float Strength => RawStrength ?? 1f;
}

internal sealed class GltfMaterialExtensions
{
    [JsonPropertyName("KHR_materials_emissive_strength")]
    public KhrMaterialsEmissiveStrength? EmissiveStrength { get; init; }
}

internal sealed class KhrMaterialsEmissiveStrength
{
    [JsonPropertyName("emissiveStrength")] public float? RawEmissiveStrength { get; init; }
    /// <summary>Emissive strength multiplier, defaulted. Extension default 1.</summary>
    [JsonIgnore] public float EmissiveStrength => RawEmissiveStrength ?? 1f;
}

internal sealed class GltfTexture
{
    [JsonPropertyName("sampler")] public int? Sampler { get; init; }
    [JsonPropertyName("source")] public int? Source { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

internal sealed class GltfSampler
{
    [JsonPropertyName("magFilter")] public int? MagFilter { get; init; }
    [JsonPropertyName("minFilter")] public int? MinFilter { get; init; }
    [JsonPropertyName("wrapS")] public int? RawWrapS { get; init; }
    [JsonPropertyName("wrapT")] public int? RawWrapT { get; init; }

    /// <summary>Wrap S, defaulted. glTF default 10497 (REPEAT).</summary>
    [JsonIgnore] public int WrapS => RawWrapS ?? 10497;
    /// <summary>Wrap T, defaulted. glTF default 10497 (REPEAT).</summary>
    [JsonIgnore] public int WrapT => RawWrapT ?? 10497;
}

internal sealed class GltfImage
{
    /// <summary>Relative file path or <c>data:</c> URI. Mutually exclusive with <see cref="BufferView"/>.</summary>
    [JsonPropertyName("uri")] public string? Uri { get; init; }
    [JsonPropertyName("bufferView")] public int? BufferView { get; init; }
    [JsonPropertyName("mimeType")] public string? MimeType { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
}

/// <summary>
/// Source-generated (reflection-free) serializer metadata for the glTF schema. Deserialization
/// enters only through <see cref="GltfRoot"/>; every nested type is reachable from it.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = false,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
[JsonSerializable(typeof(GltfRoot))]
internal sealed partial class GltfJsonContext : JsonSerializerContext
{
}
