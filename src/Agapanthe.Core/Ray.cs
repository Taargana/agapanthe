using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// A half-line in world space (physics queries): a double-precision origin (matching the
/// engine's <c>WorldPosition</c> component's own storage — a ray can originate arbitrarily far
/// from the quantized world origin) and a unit direction (float precision — a direction has no
/// large-magnitude precision problem the way a position does).
/// <para>
/// <see cref="Direction"/> must already be normalized. This type does not silently normalize on
/// construction — matches the project's explicit-input convention (no hidden work on a hot-path
/// math type); a non-unit direction does not crash anything downstream, it just scales the
/// reported hit distance, a caller correctness bug to catch in a test, not a type invariant to
/// enforce at runtime cost.
/// </para>
/// </summary>
public readonly record struct Ray(Double3 Origin, Vector3 Direction);
