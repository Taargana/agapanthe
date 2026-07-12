namespace Agapanthe.Tests;

// ResourceTracker is a global mutable static. Two test classes touch it: ResourceTrackerTests toggles
// Enabled/Reset and asserts global cleanliness, and ShaderCompilerTests registers "ShaderCompiler" when it
// constructs a compiler. xUnit parallelizes test classes by default, so those two used to race — a concurrent
// registration (or a global Enabled=false) made ResourceTrackerTests' leak-report assertions flaky. Running
// both in one non-parallelizable collection serializes them against each other AND the rest of the suite.
[CollectionDefinition("ResourceTracker", DisableParallelization = true)]
public sealed class ResourceTrackerCollection;
