using FlowDeck.Core.Persistence;

namespace FlowDeck.Core.Tests.Persistence;

/// <summary>
/// Runs the shared conformance suite against <see cref="InMemoryWorkflowStore"/>.
/// </summary>
/// <remarks>
/// Issue #16. This class is deliberately almost empty: the value is in the
/// inherited suite, which #17's EF Core provider will subclass identically.
/// Anything asserted only here would be a behaviour the contract does not
/// actually require.
/// </remarks>
public sealed class InMemoryWorkflowStoreTests : WorkflowStoreConformanceTests
{
    protected override Task<IWorkflowStore> CreateStoreAsync() =>
        Task.FromResult<IWorkflowStore>(new InMemoryWorkflowStore());
}
