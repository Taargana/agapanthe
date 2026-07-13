namespace Agapanthe.Tests;

// Belt-and-braces serialization of the world-touching test classes.
//
// History: two test classes constructing a GameWorld in parallel used to produce mismatched chunk arrays (a
// WorldTransform read back as all-zero — reproducible, not flaky). Root cause: Arch assigns component-type ids
// in process-global state on first touch, without a lock, and that first touch happened lazily at World.Create.
// ComponentRegistry.Root<T> now forces the assignment inside its own lock, before any world exists, which closes
// that race by construction — verified: with this collection removed, these classes pass in parallel 5/5, where
// they previously failed 3/3.
//
// The collection is kept anyway because Arch's world CREATION (its static world table) is still unguarded, and a
// leak/ECS gate that can false-pass is worse than a slow test suite. See GameWorld's threading contract.
[CollectionDefinition("World", DisableParallelization = true)]
public sealed class WorldCollection;
