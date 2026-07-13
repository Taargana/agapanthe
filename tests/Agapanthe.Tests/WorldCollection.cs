namespace Agapanthe.Tests;

// Arch keeps component-type ids in process-global state, assigned on first touch, and that registration is not
// thread-safe: two test classes constructing a GameWorld in parallel raced and produced mismatched chunk arrays
// (a WorldTransform read back as all-zero — reproducible, not flaky). The engine only ever builds one world on
// the main thread (see GameWorld's threading contract), so the fix here is to serialize the test classes that
// touch a world, exactly as ResourceTrackerCollection does for the other global static in this codebase.
[CollectionDefinition("World", DisableParallelization = true)]
public sealed class WorldCollection;
