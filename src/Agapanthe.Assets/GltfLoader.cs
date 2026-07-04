using System.Numerics;
using Agapanthe.Assets.Gltf;
using Agapanthe.Assets.Model;
using Agapanthe.Core;

namespace Agapanthe.Assets;

/// <summary>
/// Loads a glTF 2.0 model (<c>.gltf</c> + external buffers, or <c>.glb</c>) into pure CPU DTOs.
/// Traverses the default scene, flattens the node hierarchy into per-mesh world transforms,
/// decodes geometry through <see cref="AccessorReader"/>, maps metallic-roughness materials and
/// decodes only the images that materials actually reference. Meshes that ship without TANGENT
/// but have a normal map get tangents generated (<see cref="TangentGenerator"/>).
/// </summary>
public static class GltfLoader
{
    /// <summary>Loads and fully resolves a model. Throws <see cref="AssetException"/> on any invalid content.</summary>
    public static ModelAsset Load(string path)
    {
        var document = GltfDocument.Load(path);
        var root = document.Root;
        var reader = new AccessorReader(document);

        var imageCatalog = new ImageCatalog(document);
        var materials = BuildMaterials(root, document.SourcePath, imageCatalog);
        var meshes = BuildMeshes(root, reader, materials, document.SourcePath);

        return new ModelAsset
        {
            Meshes = meshes,
            Materials = materials,
            Images = imageCatalog.DecodedImages,
            Name = Path.GetFileNameWithoutExtension(path),
        };
    }

    // --- Scene traversal / node flattening -----------------------------------------------------

    private static List<MeshAsset> BuildMeshes(
        GltfRoot root, AccessorReader reader, IReadOnlyList<MaterialAsset> materials, string sourcePath)
    {
        var meshes = new List<MeshAsset>();
        var sceneIndex = root.Scene ?? 0;
        var scene = root.Scenes is { } scenes && sceneIndex < scenes.Length
            ? scenes[sceneIndex]
            : throw new AssetException(sourcePath, $"default scene {sceneIndex} does not exist.");

        foreach (var nodeIndex in scene.Nodes ?? [])
        {
            VisitNode(root, nodeIndex, Matrix4x4.Identity, reader, materials, meshes, sourcePath, depth: 0);
        }

        return meshes;
    }

    private static void VisitNode(
        GltfRoot root, int nodeIndex, Matrix4x4 parentWorld, AccessorReader reader,
        IReadOnlyList<MaterialAsset> materials, List<MeshAsset> meshes, string sourcePath, int depth)
    {
        if (depth > 256)
        {
            throw new AssetException(sourcePath, "node hierarchy exceeds depth 256 (cycle?).");
        }

        if (root.Nodes is not { } nodes || nodeIndex < 0 || nodeIndex >= nodes.Length)
        {
            throw new AssetException(sourcePath, $"node index {nodeIndex} out of range.");
        }

        var node = nodes[nodeIndex];
        // Row-vector convention: the child's local transform applies first, so it multiplies on the left.
        var world = LocalTransform(node, sourcePath) * parentWorld;

        if (node.Mesh is { } meshIndex)
        {
            AppendMeshPrimitives(root, meshIndex, world, reader, materials, meshes, sourcePath);
        }

        foreach (var child in node.Children ?? [])
        {
            VisitNode(root, child, world, reader, materials, meshes, sourcePath, depth + 1);
        }
    }

    /// <summary>
    /// Node local transform in the project's row-vector convention.
    /// <para>
    /// glTF stores <c>matrix</c> column-major for column-vector math (<c>v' = M·v</c>). Loading the
    /// 16 floats sequentially into <see cref="Matrix4x4"/>'s row-major fields yields exactly the
    /// transpose — which is the same transform expressed for row vectors (<c>v' = v·Mᵀ</c>). So the
    /// sequential copy below is intentional, not a missed transpose.
    /// For TRS, glTF's column-vector <c>T·R·S</c> (scale first) becomes <c>S·R·T</c> row-vector.
    /// </para>
    /// </summary>
    private static Matrix4x4 LocalTransform(GltfNode node, string sourcePath)
    {
        if (node.Matrix is { } m)
        {
            if (m.Length != 16)
            {
                throw new AssetException(sourcePath, $"node matrix has {m.Length} elements, expected 16.");
            }

            return new Matrix4x4(
                m[0], m[1], m[2], m[3],
                m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11],
                m[12], m[13], m[14], m[15]);
        }

        var scale = node.Scale is { Length: 3 } s ? new Vector3(s[0], s[1], s[2]) : Vector3.One;
        var rotation = node.Rotation is { Length: 4 } r ? new Quaternion(r[0], r[1], r[2], r[3]) : Quaternion.Identity;
        var translation = node.Translation is { Length: 3 } t ? new Vector3(t[0], t[1], t[2]) : Vector3.Zero;

        return Matrix4x4.CreateScale(scale)
             * Matrix4x4.CreateFromQuaternion(rotation)
             * Matrix4x4.CreateTranslation(translation);
    }

    // --- Geometry -------------------------------------------------------------------------------

    private static void AppendMeshPrimitives(
        GltfRoot root, int meshIndex, Matrix4x4 world, AccessorReader reader,
        IReadOnlyList<MaterialAsset> materials, List<MeshAsset> meshes, string sourcePath)
    {
        if (root.Meshes is not { } gltfMeshes || meshIndex < 0 || meshIndex >= gltfMeshes.Length)
        {
            throw new AssetException(sourcePath, $"mesh index {meshIndex} out of range.");
        }

        var gltfMesh = gltfMeshes[meshIndex];
        var primitives = gltfMesh.Primitives
            ?? throw new AssetException(sourcePath, $"mesh '{gltfMesh.Name}' has no primitives.");

        for (var p = 0; p < primitives.Length; p++)
        {
            var primitive = primitives[p];
            var attributes = primitive.Attributes
                ?? throw new AssetException(sourcePath, $"mesh '{gltfMesh.Name}' primitive {p} has no attributes.");
            var positionAccessor = attributes.Position
                ?? throw new AssetException(sourcePath, $"mesh '{gltfMesh.Name}' primitive {p} lacks POSITION.");

            var positions = reader.ReadVec3(positionAccessor);
            var normals = attributes.Normal is { } n ? reader.ReadVec3(n) : [];
            var uvs = attributes.TexCoord0 is { } uv ? reader.ReadVec2(uv) : [];
            var tangents = attributes.Tangent is { } tan ? reader.ReadVec4(tan) : [];

            ValidateStream(normals.Length, positions.Length, "NORMAL", gltfMesh.Name, p, sourcePath);
            ValidateStream(uvs.Length, positions.Length, "TEXCOORD_0", gltfMesh.Name, p, sourcePath);
            ValidateStream(tangents.Length, positions.Length, "TANGENT", gltfMesh.Name, p, sourcePath);

            var indices = primitive.Indices is { } idx
                ? reader.ReadIndices(idx)
                : SequentialIndices(positions.Length);

            var materialIndex = primitive.Material ?? -1;

            // Spec §3.6: TANGENT frequently missing from Khronos models — generate when the
            // material actually needs one (normal map) and the streams allow it.
            if (tangents.Length == 0
                && materialIndex >= 0 && materialIndex < materials.Count
                && materials[materialIndex].NormalImage >= 0
                && normals.Length > 0 && uvs.Length > 0)
            {
                tangents = TangentGenerator.Generate(positions, normals, uvs, indices);
            }

            meshes.Add(new MeshAsset
            {
                Positions = positions,
                Normals = normals,
                Uvs = uvs,
                Tangents = tangents,
                Indices = indices,
                MaterialIndex = materialIndex,
                WorldTransform = world,
                Name = primitives.Length == 1 ? gltfMesh.Name ?? string.Empty : $"{gltfMesh.Name}[{p}]",
            });
        }
    }

    private static void ValidateStream(int length, int expected, string stream, string? mesh, int primitive, string sourcePath)
    {
        if (length != 0 && length != expected)
        {
            throw new AssetException(
                sourcePath,
                $"mesh '{mesh}' primitive {primitive}: {stream} has {length} elements, POSITION has {expected}.");
        }
    }

    private static uint[] SequentialIndices(int count)
    {
        var indices = new uint[count];
        for (var i = 0; i < count; i++)
        {
            indices[i] = (uint)i;
        }

        return indices;
    }

    // --- Materials & images ---------------------------------------------------------------------

    private static List<MaterialAsset> BuildMaterials(GltfRoot root, string sourcePath, ImageCatalog images)
    {
        var materials = new List<MaterialAsset>();
        foreach (var (material, index) in (root.Materials ?? []).Select((m, i) => (m, i)))
        {
            var pbr = material.PbrMetallicRoughness;

            var alphaMode = material.AlphaMode switch
            {
                "OPAQUE" => AlphaMode.Opaque,
                "MASK" => AlphaMode.Mask,
                "BLEND" => WarnBlend(material.Name, index),
                var other => throw new AssetException(sourcePath, $"material '{material.Name}': unknown alphaMode '{other}'."),
            };

            // One glTF sampler per material is assumed (phase 1): take the base color texture's
            // sampler, falling back to the first textured slot.
            var samplerSource = pbr?.BaseColorTexture ?? pbr?.MetallicRoughnessTexture
                ?? material.NormalTexture ?? material.OcclusionTexture ?? material.EmissiveTexture;

            materials.Add(new MaterialAsset
            {
                BaseColorFactor = pbr?.BaseColorFactor is { Length: 4 } bc
                    ? new Vector4(bc[0], bc[1], bc[2], bc[3])
                    : Vector4.One,
                MetallicFactor = pbr?.MetallicFactor ?? 1f,
                RoughnessFactor = pbr?.RoughnessFactor ?? 1f,
                EmissiveFactor = material.EmissiveFactor is { Length: 3 } e ? new Vector3(e[0], e[1], e[2]) : Vector3.Zero,
                EmissiveStrength = material.Extensions?.EmissiveStrength?.EmissiveStrength ?? 1f,
                AlphaMode = alphaMode,
                AlphaCutoff = material.AlphaCutoff,
                BaseColorImage = images.Resolve(pbr?.BaseColorTexture, isSrgb: true),
                NormalImage = images.Resolve(material.NormalTexture, isSrgb: false),
                MetallicRoughnessImage = images.Resolve(pbr?.MetallicRoughnessTexture, isSrgb: false),
                OcclusionImage = images.Resolve(material.OcclusionTexture, isSrgb: false),
                EmissiveImage = images.Resolve(material.EmissiveTexture, isSrgb: true),
                TextureSettings = images.SamplerSettings(samplerSource),
                Name = material.Name ?? string.Empty,
            });
        }

        return materials;
    }

    private static AlphaMode WarnBlend(string? materialName, int index)
    {
        Log.Warn($"GltfLoader: material '{materialName ?? index.ToString()}' uses alphaMode BLEND, " +
                 "which is out of scope for phase 1 — rendered as OPAQUE (spec §3.6).");
        return AlphaMode.Opaque;
    }

    /// <summary>
    /// Decodes glTF images on demand and deduplicates them by (glTF image index, sRGB flag). Images
    /// never referenced by a material slot are never decoded; an image referenced both as sRGB and
    /// linear (rare) is decoded twice, once per color space, because <see cref="ImageAsset.IsSrgb"/>
    /// determines the GPU format downstream.
    /// </summary>
    private sealed class ImageCatalog(GltfDocument document)
    {
        private readonly Dictionary<(int GltfImage, bool IsSrgb), int> _decoded = [];

        public List<ImageAsset> DecodedImages { get; } = [];

        /// <summary>Resolves a texture slot to a decoded image index, or -1 when the slot is empty.</summary>
        public int Resolve(GltfTextureInfo? textureInfo, bool isSrgb)
        {
            if (textureInfo is null)
            {
                return -1;
            }

            var root = document.Root;
            var textures = root.Textures
                ?? throw new AssetException(document.SourcePath, "material references a texture but the model has none.");
            if (textureInfo.Index < 0 || textureInfo.Index >= textures.Length)
            {
                throw new AssetException(document.SourcePath, $"texture index {textureInfo.Index} out of range.");
            }

            var imageIndex = textures[textureInfo.Index].Source
                ?? throw new AssetException(document.SourcePath, $"texture {textureInfo.Index} has no image source.");

            if (_decoded.TryGetValue((imageIndex, isSrgb), out var existing))
            {
                return existing;
            }

            var decoded = Decode(imageIndex, isSrgb);
            var slot = DecodedImages.Count;
            DecodedImages.Add(decoded);
            _decoded[(imageIndex, isSrgb)] = slot;
            return slot;
        }

        /// <summary>Maps the glTF sampler of a texture slot to Assets enums (glTF defaults when absent).</summary>
        public TextureSettings SamplerSettings(GltfTextureInfo? textureInfo)
        {
            if (textureInfo is null || document.Root.Textures is not { } textures
                || textureInfo.Index >= textures.Length
                || textures[textureInfo.Index].Sampler is not { } samplerIndex
                || document.Root.Samplers is not { } samplers || samplerIndex >= samplers.Length)
            {
                return TextureSettings.Default;
            }

            var sampler = samplers[samplerIndex];
            return new TextureSettings(
                WrapU: MapWrap(sampler.WrapS),
                WrapV: MapWrap(sampler.WrapT),
                MinFilter: MapFilter(sampler.MinFilter),
                MagFilter: MapFilter(sampler.MagFilter));
        }

        private ImageAsset Decode(int imageIndex, bool isSrgb)
        {
            var root = document.Root;
            var images = root.Images
                ?? throw new AssetException(document.SourcePath, "texture references an image but the model has none.");
            if (imageIndex < 0 || imageIndex >= images.Length)
            {
                throw new AssetException(document.SourcePath, $"image index {imageIndex} out of range.");
            }

            var image = images[imageIndex];
            var name = image.Name ?? $"image[{imageIndex}]";

            if (image.BufferView is { } viewIndex)
            {
                var views = root.BufferViews
                    ?? throw new AssetException(document.SourcePath, $"image '{name}' references bufferView {viewIndex} but the model has none.");
                if (viewIndex < 0 || viewIndex >= views.Length)
                {
                    throw new AssetException(document.SourcePath, $"image '{name}': bufferView {viewIndex} out of range.");
                }

                var view = views[viewIndex];
                var buffer = document.GetBufferData(view.Buffer);
                if (view.ByteOffset + view.ByteLength > buffer.Length)
                {
                    throw new AssetException(document.SourcePath, $"image '{name}': bufferView exceeds buffer length.");
                }

                return ImageLoader.LoadFromBytes(
                    buffer.Span.Slice(view.ByteOffset, view.ByteLength), isSrgb, $"{document.SourcePath}#{name}");
            }

            if (image.Uri is not { } uri)
            {
                throw new AssetException(document.SourcePath, $"image '{name}' has neither uri nor bufferView.");
            }

            if (uri.StartsWith("data:", StringComparison.Ordinal))
            {
                var comma = uri.IndexOf(',');
                if (comma < 0 || !uri.AsSpan(0, comma).EndsWith(";base64"))
                {
                    throw new AssetException(document.SourcePath, $"image '{name}': unsupported data: URI (base64 expected).");
                }

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(uri[(comma + 1)..]);
                }
                catch (FormatException e)
                {
                    throw new AssetException(document.SourcePath, $"image '{name}': invalid base64 payload.", e);
                }

                return ImageLoader.LoadFromBytes(bytes, isSrgb, $"{document.SourcePath}#{name}");
            }

            var baseDir = Path.GetDirectoryName(document.SourcePath) ?? ".";
            var path = Path.Combine(baseDir, Uri.UnescapeDataString(uri));
            return ImageLoader.Load(path, isSrgb);
        }

        private static TextureWrap MapWrap(int gltfWrap) => gltfWrap switch
        {
            10497 => TextureWrap.Repeat,
            33071 => TextureWrap.ClampToEdge,
            33648 => TextureWrap.MirroredRepeat,
            _ => TextureWrap.Repeat,
        };

        /// <summary>Collapses glTF min filters to their base filter: NEAREST* → Nearest, LINEAR* → Linear.</summary>
        private static TextureFilter MapFilter(int? gltfFilter) => gltfFilter switch
        {
            9728 or 9984 or 9986 => TextureFilter.Nearest, // NEAREST, NEAREST_MIPMAP_NEAREST, NEAREST_MIPMAP_LINEAR
            _ => TextureFilter.Linear,                     // LINEAR, LINEAR_MIPMAP_*, absent
        };
    }
}
