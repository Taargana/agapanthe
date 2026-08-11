namespace Agapanthe.Assets.Font;

/// <summary>
/// Thrown when a <c>.agfont</c> cannot be read: bad magic, unsupported version, truncated payload, or counts that
/// disagree with the byte length. A dedicated type (rather than a bare <see cref="AssetException"/>) so a caller can
/// tell "this font file is broken" from "this glTF is broken" — mirroring VS-1's dedicated snapshot exception.
/// <para>Never thrown at draw time: a missing glyph is a content condition handled by the layout, not an error.</para>
/// </summary>
public sealed class FontAssetException : AssetException
{
    public FontAssetException(string message)
        : base(message)
    {
    }

    public FontAssetException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
