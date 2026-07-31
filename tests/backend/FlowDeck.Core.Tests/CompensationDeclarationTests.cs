using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #118 - Declare a compensating action for a step.
///
/// Scenario: A compensating action is declared beside its step
/// Scenario: WithCompensation applies to the preceding step
/// Scenario: Declaring compensation before any step is rejected
/// </summary>
/// <remarks>
/// Declaration only. Nothing invokes the action yet - that is #119.
///
/// <para>
/// <c>WithCompensation</c> applies <b>backwards</b>, to the step just declared,
/// unlike <c>WithRetryPolicy</c> which sets a forward default. A retry policy is
/// a sensible thing to apply broadly; an undo action is specific to the one
/// thing it undoes, so a compensation default would be a category error
/// (ADR-0021).
/// </para>
/// </remarks>
public class CompensationDeclarationTests
{
    private sealed class Noop : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class Undo : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class Declared(Action<IWorkflowBuilder> declare) : IWorkflowDefinition
    {
        public string Id => "declared";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => declare(builder);
    }

    /// <summary>Compiles a definition the way the engine does.</summary>
    private static IReadOnlyList<StepDeclaration> Compile(Action<IWorkflowBuilder> declare)
    {
        var builder = new WorkflowBuilder("declared");
        declare(builder);
        return builder.Build();
    }

    [Fact]
    public void A_compensating_action_is_declared_beside_its_step()
    {
        // Given a workflow declaring a step with WithCompensation
        // When the definition is compiled
        var steps = Compile(builder => builder
            .AddStep("charge", () => new Noop())
            .WithCompensation(() => new Undo()));

        // Then that step carries its compensating action
        Assert.NotNull(steps[0].Compensation);
        Assert.IsType<Undo>(steps[0].Compensation!());
    }

    [Fact]
    public void WithCompensation_applies_to_the_preceding_step()
    {
        // Given two steps, the first with a compensating action
        var steps = Compile(builder => builder
            .AddStep("charge", () => new Noop())
            .WithCompensation(() => new Undo())
            .AddStep("ship", () => new Noop()));

        // Then only the first carries one. Applying forwards - the way
        // WithRetryPolicy works - would silently give "ship" an undo action
        // written for "charge".
        Assert.NotNull(steps[0].Compensation);
        Assert.Null(steps[1].Compensation);
    }

    [Fact]
    public void Declaring_compensation_before_any_step_is_rejected()
    {
        // Given a workflow calling WithCompensation before AddStep
        // Then InvalidWorkflowDefinitionException is raised.
        //
        // There is no step to attach it to, and silently attaching it to the
        // *next* step would be the forward reading this API deliberately does
        // not have.
        var ex = Assert.Throws<InvalidWorkflowDefinitionException>(
            () => Compile(builder => builder.WithCompensation(() => new Undo())));

        Assert.Contains("compensation", ex.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_step_without_compensation_carries_none()
    {
        // Null, not an empty action. "Nothing to undo" and "undo that does
        // nothing" are different, and #119 skips the first rather than
        // recording a rollback that did not happen.
        var steps = Compile(builder => builder.AddStep("charge", () => new Noop()));

        Assert.Null(steps[0].Compensation);
    }

    [Fact]
    public void A_null_compensating_action_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(
            () => Compile(builder => builder.AddStep("charge", () => new Noop()).WithCompensation(null!)));
    }

    [Fact]
    public void Declaring_compensation_twice_replaces_the_first()
    {
        // Consistent with WithRetryPolicy, where a later declaration wins.
        // Throwing would be defensible; being inconsistent with the sibling
        // method on the same builder would not.
        var steps = Compile(builder => builder
            .AddStep("charge", () => new Noop())
            .WithCompensation(() => new Undo())
            .WithCompensation(() => new Noop()));

        Assert.IsType<Noop>(steps[0].Compensation!());
    }

    [Fact]
    public void The_action_is_a_factory_so_two_instances_never_share_it()
    {
        // The same reason step bodies are factories: a compensating action is
        // author code that may hold per-execution state, and two instances
        // rolling back at once must not share it.
        var steps = Compile(builder => builder
            .AddStep("charge", () => new Noop())
            .WithCompensation(() => new Undo()));

        Assert.NotSame(steps[0].Compensation!(), steps[0].Compensation!());
    }

    [Fact]
    public async Task A_declared_action_does_not_run_on_a_successful_workflow()
    {
        // Rollback is for failure. A compensating action firing on a run that
        // succeeded would undo work the author meant to keep.
        var undone = false;

        var registry = new WorkflowRegistry();
        registry.Register(new Declared(builder => builder
            .AddStep("charge", () => new Noop())
            .WithCompensation(() => new Recording(() => undone = true))));

        var instance = await new WorkflowEngine(registry).StartAsync("declared", 1);

        Assert.Equal(InstanceStatus.Completed, instance.Status);
        Assert.False(undone);
    }

    private sealed class Recording(Action onRun) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            onRun();
            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
