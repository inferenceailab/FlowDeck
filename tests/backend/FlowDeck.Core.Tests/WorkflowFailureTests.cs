using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #6 - Surface step failure as workflow failure.
///
/// Scenario: Unhandled exception fails the instance
/// </summary>
public class WorkflowFailureTests
{
    private sealed class ThrowingStep(Exception toThrow) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw toThrow;
    }

    private sealed class NoopStep : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class FailingAtSecondStep(Exception toThrow) : IWorkflowDefinition
    {
        public string Id => "fails-at-b";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            builder.AddStep("A", () => new NoopStep());
            builder.AddStep("B", () => new ThrowingStep(toThrow));
            builder.AddStep("C", () => new NoopStep());
        }
    }

    private static WorkflowEngine EngineFor(IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry);
    }

    [Fact]
    public async Task Unhandled_exception_fails_the_instance()
    {
        // Given a step that throws InvalidOperationException
        var engine = EngineFor(new FailingAtSecondStep(new InvalidOperationException("boom")));

        // When the instance executes that step
        var instance = await engine.StartAsync("fails-at-b", 1);

        // Then the instance status becomes Failed
        Assert.Equal(InstanceStatus.Failed, instance.Status);

        // And the recorded error message contains "InvalidOperationException"
        Assert.NotNull(instance.Error);
        Assert.Contains("InvalidOperationException", instance.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("boom", instance.Error.Message);

        // And the failing step name is recorded
        Assert.Equal("B", instance.FailedStepName);
    }

    [Fact]
    public async Task The_exception_does_not_escape_to_the_caller()
    {
        // A workflow failing is a normal outcome, not an exception for the
        // caller to handle. Starting an instance whose step throws must return
        // a failed instance rather than throwing.
        var engine = EngineFor(new FailingAtSecondStep(new InvalidOperationException("boom")));

        var instance = await engine.StartAsync("fails-at-b", 1);

        Assert.Equal(InstanceStatus.Failed, instance.Status);
    }

    [Fact]
    public async Task A_failed_instance_is_terminal_and_timestamped()
    {
        var engine = EngineFor(new FailingAtSecondStep(new InvalidOperationException("boom")));

        var instance = await engine.StartAsync("fails-at-b", 1);

        Assert.True(instance.IsTerminal);
        Assert.NotNull(instance.CompletedAt);
    }

    [Fact]
    public async Task A_successful_instance_records_no_failing_step()
    {
        var registry = new WorkflowRegistry();
        registry.Register(new AllPassing());
        var engine = new WorkflowEngine(registry);

        var instance = await engine.StartAsync("all-passing", 1);

        Assert.Null(instance.FailedStepName);
        Assert.Null(instance.Error);
    }

    private sealed class AllPassing : IWorkflowDefinition
    {
        public string Id => "all-passing";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder.AddStep("only", () => new NoopStep());
    }

    [Fact]
    public async Task The_original_exception_type_is_preserved_not_wrapped()
    {
        // Wrapping would force callers to unwrap before they could match on the
        // failure, and would bury the stack trace an operator needs.
        var original = new TimeoutException("downstream did not answer");
        var engine = EngineFor(new FailingAtSecondStep(original));

        var instance = await engine.StartAsync("fails-at-b", 1);

        Assert.Same(original, instance.Error);
    }
}
