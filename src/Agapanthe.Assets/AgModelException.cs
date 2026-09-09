namespace Agapanthe.Assets;

/// <summary>Thrown when a <c>.agmodel</c> blob is malformed (bad magic, unsupported version, a count that does
/// not match the payload length, a non-finite value, an out-of-range index). The reader never returns a
/// half-built <see cref="Agapanthe.Assets.Model.ModelAsset"/> and never reads out of bounds — a corrupt blob is
/// a hard, named failure, exactly like <see cref="Agapanthe.Assets.Font.FontAssetException"/>.</summary>
public sealed class AgModelException : Exception
{
    public AgModelException(string message)
        : base(message)
    {
    }

    public AgModelException(string message, Exception inner)
        : base(message, inner)
    {
    }
}
