namespace Agapanthe.World;

/// <summary>
/// Thrown by the transform-propagation system when the parent chain contains a cycle (spec §3.5, system 1). The
/// message carries the offending chain so the bad hierarchy can be identified. A cycle is a construction bug,
/// never a recoverable runtime condition.
/// </summary>
public sealed class WorldHierarchyException(string message) : Exception(message);
