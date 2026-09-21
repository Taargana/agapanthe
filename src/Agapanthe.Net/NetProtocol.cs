namespace Agapanthe.Net;

/// <summary>
/// Net-1's wire protocol tags — the single source of truth both <c>DedicatedServer</c> and <c>ThinClient</c>
/// build against. Previously each sample redeclared the same byte constants independently (a duplication an
/// audit finding flagged: a divergence of one value between the two copies would be a silent wire mismatch,
/// caught by nothing). Opaque bytes by design (mirrors <see cref="Agapanthe.Engine.SimCommand.Kind"/>'s own
/// documented contract: "the application defines its own constants") — this project owns the meaning, not
/// <c>Agapanthe.Net</c> itself, which only owns the numbers so they can't drift.
/// </summary>
public static class NetProtocol
{
    public const byte AssignEntityTag = 0;
    public const byte EntityIntroduceTag = 1;
    public const byte PositionUpdateTag = 2;
    public const byte SimCommandTag = 3;

    /// <summary><see cref="Agapanthe.Engine.SimCommand.Kind"/>'s value for a movement-intent command.</summary>
    public const byte MoveKind = 1;
}
