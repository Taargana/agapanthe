using System.Runtime.InteropServices;
using Agapanthe.Core;

namespace Agapanthe.Tests;

/// <summary>
/// Guards the <see cref="SceneCandidate"/> memory layout (P3-M6): it is written straight into the persistent
/// candidate SSBO and read by <c>scene_cull.comp</c> / <c>shadow_cull.comp</c> as their <c>Candidate</c> struct,
/// so its size and field offsets must match std430 exactly. Moved from <c>Agapanthe.Rendering</c> to
/// <c>Agapanthe.Core</c> (the World fills it now) and extended with the shadow batch id + flags, staying 96 B.
/// A silent reordering here would corrupt every culled instance with no validation error.
/// </summary>
public sealed class SceneCandidateTests
{
    [Fact]
    public void Layout_Is96Bytes_WithStd430Offsets()
    {
        Assert.Equal(96, Marshal.SizeOf<SceneCandidate>());
        Assert.Equal(0, (int)Marshal.OffsetOf<SceneCandidate>(nameof(SceneCandidate.Model)));
        Assert.Equal(64, (int)Marshal.OffsetOf<SceneCandidate>(nameof(SceneCandidate.Sphere)));
        Assert.Equal(80, (int)Marshal.OffsetOf<SceneCandidate>(nameof(SceneCandidate.SceneBatchId)));
        Assert.Equal(84, (int)Marshal.OffsetOf<SceneCandidate>(nameof(SceneCandidate.ShadowBatchId)));
        Assert.Equal(88, (int)Marshal.OffsetOf<SceneCandidate>(nameof(SceneCandidate.Flags)));
    }

    [Fact]
    public void CastsShadowFlag_IsBitZero()
    {
        Assert.Equal(1u, SceneCandidate.FlagCastsShadow);
    }
}
