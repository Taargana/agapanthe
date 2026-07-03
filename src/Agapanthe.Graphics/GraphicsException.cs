namespace Agapanthe.Graphics;

/// <summary>Thrown when a Vulkan call or a graphics-module invariant fails.</summary>
public class GraphicsException : Exception
{
    public GraphicsException(string message)
        : base(message)
    {
    }

    public GraphicsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
