using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #1 - Define a workflow with a stable identifier.
///
/// Scenario: A definition exposes id and version
/// Scenario: Duplicate id and version is rejected
/// </summary>
public class WorkflowRegistryTests
{
    /// <summary>
    /// The registry stores definitions without compiling them, so these doubles
    /// declare a step only to satisfy the interface.
    /// </summary>
    private sealed class OrderFulfilment : IWorkflowDefinition
    {
        public string Id => "order-fulfilment";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("noop", () => new NoopStep());
    }

    private sealed class OrderFulfilmentV2 : IWorkflowDefinition
    {
        public string Id => "order-fulfilment";

        public int Version => 2;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("noop", () => new NoopStep());
    }

    private sealed class NoopStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    [Fact]
    public void Registered_definition_is_returned_by_id_and_version()
    {
        // Given a class implementing IWorkflowDefinition with id "order-fulfilment" and version 1
        var registry = new WorkflowRegistry();
        var definition = new OrderFulfilment();

        // When the definition is registered with the engine
        registry.Register(definition);

        // Then the registry returns it for id "order-fulfilment" version 1
        var resolved = registry.Get("order-fulfilment", 1);

        Assert.Same(definition, resolved);
    }

    [Fact]
    public void Registering_the_same_id_and_version_twice_is_rejected()
    {
        // Given a definition "order-fulfilment" version 1 is already registered
        var registry = new WorkflowRegistry();
        registry.Register(new OrderFulfilment());

        // When a second definition with the same id and version is registered
        var act = () => registry.Register(new OrderFulfilment());

        // Then registration fails with a DuplicateDefinitionException
        var ex = Assert.Throws<DuplicateDefinitionException>(act);
        Assert.Equal("order-fulfilment", ex.DefinitionId);
        Assert.Equal(1, ex.Version);
    }

    [Fact]
    public void Same_id_with_a_different_version_is_allowed()
    {
        // Versioning is the whole point of the composite key: two versions of
        // the same workflow must coexist so in-flight instances keep running
        // against the definition they started with.
        var registry = new WorkflowRegistry();
        var v1 = new OrderFulfilment();
        var v2 = new OrderFulfilmentV2();

        registry.Register(v1);
        registry.Register(v2);

        Assert.Same(v1, registry.Get("order-fulfilment", 1));
        Assert.Same(v2, registry.Get("order-fulfilment", 2));
    }

    [Fact]
    public void Unknown_definition_is_reported_as_not_found()
    {
        var registry = new WorkflowRegistry();

        var ex = Assert.Throws<DefinitionNotFoundException>(
            () => registry.Get("does-not-exist", 1));

        Assert.Equal("does-not-exist", ex.DefinitionId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Definition_id_must_not_be_blank(string id)
    {
        var registry = new WorkflowRegistry();

        Assert.Throws<ArgumentException>(() => registry.Register(new BlankId(id)));
    }

    private sealed class BlankId(string id) : IWorkflowDefinition
    {
        public string Id { get; } = id;

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("noop", () => new NoopStep());
    }
}
