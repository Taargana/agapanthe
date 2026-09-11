namespace Agapanthe.Assets.Scene;

/// <summary>Thrown when a <c>.agscene</c> blob is malformed (bad magic, unsupported version, a count that does not
/// match the payload, a key index out of range). The reader never returns a half-built
/// <see cref="SceneDefinition"/> and never reads out of bounds — a corrupt blob is a hard, named failure,
/// exactly like <see cref="Agapanthe.Assets.AgModelException"/>.</summary>
public sealed class AgSceneException : Exception
{
    public AgSceneException(string message)
        : base(message)
    {
    }

    public AgSceneException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
