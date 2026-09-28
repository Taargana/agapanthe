using Agapanthe.Graphics;
using Agapanthe.Rendering;

namespace Agapanthe.App;

/// <summary>
/// Scene management spec, D8: what an <see cref="ISceneRecipe.PrefetchBackground"/> call running on the
/// sanctioned loader thread (GraphicsDevice thread-safety spec) may safely touch — a fresh, isolated
/// <see cref="Registry"/> the render thread never sees until an explicit hand-off. <c>null</c> when
/// <c>PrefetchBackground</c> runs synchronously for the very first scene load (no thread involved).
/// </summary>
public sealed class BackgroundPresentationContext
{
    /// <summary>The GPU device — the same instance the render thread uses; safe here only through the locked
    /// submit/present/WaitIdle surface the thread-safety spec added.</summary>
    public required GraphicsDevice Device { get; init; }

    /// <summary>A fresh registry, never the active one — built in isolation, handed off only on success.</summary>
    public required ResourceRegistry Registry { get; init; }

    /// <summary>Read-only handle snapshot; needs no external synchronization on the layout itself, only on the
    /// descriptor pool it allocates from — which is per-<see cref="Registry"/>.</summary>
    public required DescriptorSetLayout MaterialSetLayout { get; init; }

    /// <summary>Checked between per-model iterations (once per model, D7) — a later switch request cancels the
    /// in-flight one rather than letting it run to completion for data nobody wants anymore.</summary>
    public required CancellationToken CancellationToken { get; init; }
}
