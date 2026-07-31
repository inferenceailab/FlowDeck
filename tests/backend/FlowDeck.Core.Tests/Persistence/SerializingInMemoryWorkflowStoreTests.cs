using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Runs the conformance suite against an in-memory store that round-trips data
/// through <see cref="WorkflowDataSerializer"/>.
/// </summary>
/// <remarks>
/// Issue #15. The plain in-memory store keeps values as live objects, so it
/// cannot catch a workflow that stores something a text-backed provider could
/// not persist. This configuration can, which means #17's provider is not the
/// first thing to discover a serialisation problem.
/// </remarks>
public sealed class SerializingInMemoryWorkflowStoreTests : WorkflowStoreConformanceTests
{
    protected override Task<IWorkflowStore> CreateStoreAsync() =>
        Task.FromResult<IWorkflowStore>(
            new InMemoryWorkflowStore(new WorkflowDataSerializer()));
}
