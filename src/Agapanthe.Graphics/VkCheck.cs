using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

internal static class VkCheck
{
    /// <summary>
    /// Throws on any non-Success result. Calls whose non-error codes are meaningful
    /// (SUBOPTIMAL_KHR / OUT_OF_DATE_KHR on acquire/present) must handle those codes
    /// before delegating the rest here.
    /// </summary>
    public static void ThrowIfFailed(Result result, string call)
    {
        if (result != Result.Success)
        {
            throw new GraphicsException($"{call} failed: {result}");
        }
    }
}
