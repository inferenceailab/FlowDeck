using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #9 - Reject starting an unregistered definition.
///
/// Scenario: Unknown definition id is rejected
/// </summary>
public class UnregisteredDefinitionTests
{
    private sealed class NoopStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class Known : IWorkflowDefinition
    {
        public string Id => "known";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new NoopStep());
    }

    [Fact]
    public async Task An_unknown_definition_id_is_rejected()
    {
        // Given no definition registered with id "does-not-exist"
        var engine = new WorkflowEngine(new WorkflowRegistry());

        // When an instance of "does-not-exist" is started
        // Then a DefinitionNotFoundException is thrown
        var ex = await Assert.ThrowsAsync<DefinitionNotFoundException>(
            async () => await engine.StartAsync("does-not-exist", 1));

        Assert.Equal("does-not-exist", ex.DefinitionId);
    }

    [Fact]
    public async Task No_instance_is_created_for_an_unknown_definition()
    {
        // "And no instance is created" - the definition is resolved before any
        // instance is constructed, so a typo cannot leave an orphan behind.
        // Once #11 gives the engine a queryable store this becomes directly
        // observable; for now it is asserted structurally: no step ever runs.
        var executed = false;
        var registry = new WorkflowRegistry();
        registry.Register(new SpyWorkflow(() => executed = true));
        var engine = new WorkflowEngine(registry);

        await Assert.ThrowsAsync<DefinitionNotFoundException>(
            async () => await engine.StartAsync("does-not-exist", 1));

        Assert.False(executed);
    }

    private sealed class SpyWorkflow(Action onExecute) : IWorkflowDefinition
    {
        public string Id => "spy";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new SpyStep(onExecute));
    }

    private sealed class SpyStep(Action onExecute) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            onExecute();
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    [Fact]
    public async Task A_known_id_at_an_unknown_version_is_rejected()
    {
        // The composite key from #1 means a version typo is just as wrong as an
        // id typo, and must fail as loudly.
        var registry = new WorkflowRegistry();
        registry.Register(new Known());
        var engine = new WorkflowEngine(registry);

        var ex = await Assert.ThrowsAsync<DefinitionNotFoundException>(
            async () => await engine.StartAsync("known", 99));

        Assert.Equal("known", ex.DefinitionId);
        Assert.Equal(99, ex.Version);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_definition_id_is_rejected_as_an_argument_error(string id)
    {
        // A blank id is a caller bug, not a missing definition. Distinguishing
        // the two keeps "not found" meaningful.
        var engine = new WorkflowEngine(new WorkflowRegistry());

        await Assert.ThrowsAsync<ArgumentException>(
            async () => await engine.StartAsync(id, 1));
    }

    [Fact]
    public async Task The_failure_message_names_the_definition_and_version()
    {
        // An operator reading this in a log needs both halves of the key.
        var engine = new WorkflowEngine(new WorkflowRegistry());

        var ex = await Assert.ThrowsAsync<DefinitionNotFoundException>(
            async () => await engine.StartAsync("missing", 7));

        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
        Assert.Contains("7", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefinitionNotFoundException_is_a_FlowDeck_exception()
    {
        // Callers must be able to catch engine faults as a family, separately
        // from faults thrown by workflow step code.
        var ex = new DefinitionNotFoundException("x", 1);

        Assert.IsAssignableFrom<FlowDeckException>(ex);
    }
}
