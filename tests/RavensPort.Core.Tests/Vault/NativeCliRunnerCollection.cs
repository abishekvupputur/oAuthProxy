namespace RavensPort.Core.Tests.Vault;

/// <summary>
/// Every test class that drives <c>NativeCliRunner</c>, run one at a time.
///
/// The runner keeps its connection state in static fields — whether the SDK client has been
/// initialised, and when a rebuild was last attempted — because the 1Password SDK caches its client
/// per process and there is no per-instance state to hang that on. xUnit runs test classes in
/// parallel by default, so two classes exercising the runner will interleave writes to those
/// statics and fail each other: one class resetting initialisation mid-test makes another's
/// "Initialize was called exactly once" untrue, with no clue in the failure that another test was
/// involved at all.
///
/// This is not hypothetical. Adding the service-account tests turned it from a latent hazard into a
/// failure that appeared only in the full run and passed in isolation — the worst shape a test
/// failure can take.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeCliRunnerCollection
{
    public const string Name = "NativeCliRunner";
}
