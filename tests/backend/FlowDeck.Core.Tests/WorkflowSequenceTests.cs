using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #4 - Execute steps in declared sequence.
///
/// Scenario: Three steps run in declaration order
/// Scenario: A failing step halts the sequence
/// </summary>
public class WorkflowSequenceTests
{
    /// <summary>
    /// Appends its name to a shared list when executed, so a test can assert on
    /// execution order. Durable execution history is #18; this is the smallest
    /// thing that constrains ordering without pre-empting that design.
    /// </summary>
    private sealed class RecordingStep(string name, List<string> log, Outcome outcome = Outcome.Next) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return ValueTask.FromResult(outcome);
        }
    }

    private sealed class ThrowingStep(string name, List<string> log) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            throw new InvalidOperationException($"step {name} failed");
        }
    }

    /// <summary>A definition assembled from an explicit list of named bodies.</summary>
    private sealed class ComposedWorkflow(params (string Name, IStep Body)[] steps) : IWorkflowDefinition
    {
        public string Id => "composed";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            foreach (var (name, body) in steps)
            {
                builder.AddStep(name, () => body);
            }
        }
    }

    private static WorkflowEngine EngineFor(IWorkflowDefinition definition)
    {
        var registry = new WorkflowRegistry();
        registry.Register(definition);
        return new WorkflowEngine(registry);
    }

    [Fact]
    public async Task Steps_execute_in_declaration_order()
    {
        // Given a definition declaring steps A, B and C in that order
        var log = new List<string>();
        var engine = EngineFor(new ComposedWorkflow(
            ("A", new RecordingStep("A", log)),
            ("B", new RecordingStep("B", log)),
            ("C", new RecordingStep("C", log))));

        // When an instance is started
        var instance = await engine.StartAsync("composed", 1);

        // Then the execution log records A then B then C
        Assert.Equal(["A", "B", "C"], log);
        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    [Fact]
    public async Task A_failing_step_halts_the_sequence()
    {
        // Given a definition declaring steps A, B and C
        // And step B throws an exception
        var log = new List<string>();
        var engine = EngineFor(new ComposedWorkflow(
            ("A", new RecordingStep("A", log)),
            ("B", new ThrowingStep("B", log)),
            ("C", new RecordingStep("C", log))));

        // When an instance is started
        var instance = await engine.StartAsync("composed", 1);

        // Then step C is never executed
        Assert.Equal(["A", "B"], log);
        Assert.DoesNotContain("C", log);

        // And the instance status becomes Failed
        Assert.Equal(InstanceStatus.Failed, instance.Status);
    }

    [Fact]
    public async Task A_suspending_step_halts_the_sequence_without_failing()
    {
        // Suspension stops the run like a failure does, but must leave the
        // instance resumable and positioned on the step that suspended.
        var log = new List<string>();
        var engine = EngineFor(new ComposedWorkflow(
            ("A", new RecordingStep("A", log)),
            ("B", new RecordingStep("B", log, Outcome.Suspend)),
            ("C", new RecordingStep("C", log))));

        var instance = await engine.StartAsync("composed", 1);

        Assert.Equal(["A", "B"], log);
        Assert.Equal(InstanceStatus.Suspended, instance.Status);
        Assert.Equal("B", instance.CurrentStepName);
        Assert.Equal(1, instance.CurrentStepIndex);
        Assert.Null(instance.CompletedAt);
    }

    [Fact]
    public async Task A_completed_sequence_leaves_no_current_step()
    {
        var log = new List<string>();
        var engine = EngineFor(new ComposedWorkflow(
            ("A", new RecordingStep("A", log)),
            ("B", new RecordingStep("B", log))));

        var instance = await engine.StartAsync("composed", 1);

        Assert.Null(instance.CurrentStepName);
        Assert.True(instance.IsTerminal);
    }

    [Fact]
    public async Task Steps_receive_their_own_name_in_context()
    {
        // Each step must see its own identity, not the first step's. Getting
        // this wrong would mislabel every history entry and error message.
        var seen = new List<string>();
        var engine = EngineFor(new ComposedWorkflow(
            ("A", new ContextCapturingStep(seen)),
            ("B", new ContextCapturingStep(seen))));

        await engine.StartAsync("composed", 1);

        Assert.Equal(["A", "B"], seen);
    }

    private sealed class ContextCapturingStep(List<string> seen) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            seen.Add(context.StepName);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    [Fact]
    public async Task All_steps_of_one_instance_share_its_instance_id()
    {
        var ids = new List<Guid>();
        var engine = EngineFor(new ComposedWorkflow(
            ("A", new InstanceIdCapturingStep(ids)),
            ("B", new InstanceIdCapturingStep(ids))));

        var instance = await engine.StartAsync("composed", 1);

        Assert.Equal(2, ids.Count);
        Assert.All(ids, id => Assert.Equal(instance.Id, id));
    }

    private sealed class InstanceIdCapturingStep(List<Guid> ids) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ids.Add(context.InstanceId);
            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
