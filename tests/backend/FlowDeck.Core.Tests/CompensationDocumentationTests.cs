using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #123 - Document compensation in the workflow guide.
///
/// Scenario: The guide has a compensation section
/// Scenario: The example is compiled
/// </summary>
/// <remarks>
/// Follows the shape of #108: the prose is asserted against the file, not only
/// the examples compiled. A limit that quietly disappears in an unrelated edit
/// is how an author comes to rely on a guarantee the engine does not make -
/// and compensation makes fewer guarantees than it looks like it does.
/// </remarks>
public class CompensationDocumentationTests
{
    private static string Guide() =>
        File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "guides", "defining-a-workflow.md"));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test output path.");
    }

    /// <summary>The compensation section, up to the next heading of its level.</summary>
    private static string Section()
    {
        var guide = Guide();
        var start = guide.IndexOf("## Compensation", StringComparison.Ordinal);

        Assert.True(start >= 0, "the guide has no '## Compensation' section");

        var section = guide[start..];
        var next = section.IndexOf("\n## ", StringComparison.Ordinal);

        return next > 0 ? section[..next] : section;
    }

    // --- "The guide has a compensation section" -----------------------------

    [Fact]
    public void The_guide_shows_how_to_declare_a_compensating_action()
    {
        Assert.Contains("WithCompensation", Section(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_guide_states_that_rollback_runs_in_reverse_order()
    {
        Assert.Contains("reverse", Section(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_guide_states_that_rollback_continues_past_a_failing_action()
    {
        // The half an author will not guess. The forward pass stops at the
        // first failure, so assuming the reverse pass does too is reasonable
        // and wrong.
        var section = Section();

        Assert.Contains("continues", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not stop", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_guide_states_that_compensation_is_best_effort()
    {
        // The most important sentence in the section. The engine tries
        // everything and reports honestly; it does not guarantee the world is
        // back where it started, and an author who believes otherwise will not
        // build the reconciliation they need.
        Assert.Contains("best-effort", Section(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_guide_states_that_a_failed_step_is_still_compensated()
    {
        // The least obvious rule, and the one most likely to surprise: a step
        // that threw still gets its undo action, because it may have had an
        // effect before it threw.
        var section = Section();

        Assert.Contains("exhaust", section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("once", section, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_guide_documents_the_new_terminal_statuses()
    {
        var section = Section();

        Assert.Contains("Compensated", section, StringComparison.Ordinal);
        Assert.Contains("CompensationFailed", section, StringComparison.Ordinal);
    }

    [Fact]
    public void The_lifecycle_table_lists_every_status_the_engine_can_report()
    {
        // A status missing from the table is a status an operator meets in the
        // dashboard with nothing to look it up in.
        var guide = Guide();

        foreach (var status in Enum.GetNames<InstanceStatus>())
        {
            Assert.Contains($"`{status}`", guide, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_guide_states_that_cancelling_does_not_compensate()
    {
        // Deliberately not decided (#124), so it has to be stated. An operator
        // cancelling an instance and expecting a rollback would otherwise find
        // out from the side effects.
        Assert.Contains("Cancelling", Section(), StringComparison.OrdinalIgnoreCase);
    }

    // --- "The example is compiled" ------------------------------------------

    /// <summary>The guide's compensating workflow, verbatim.</summary>
    public sealed class FulfilOrder(IOrders orders) : IWorkflowDefinition
    {
        public string Id => "fulfil-order";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) => builder
            .AddStep("reserve-stock", () => new ReserveStock(orders))
                .WithCompensation(() => new ReleaseStock(orders))
            .AddStep("charge", () => new Charge(orders))
                .WithCompensation(() => new Refund(orders))
            .AddStep("ship", () => new Ship(orders));
    }

    public interface IOrders
    {
        void Reserve(Guid instanceId);

        void Release(Guid instanceId);

        void Charge(Guid instanceId);

        void Refund(Guid instanceId);

        void Ship(Guid instanceId);
    }

    public sealed class ReserveStock(IOrders orders) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            orders.Reserve(context.InstanceId);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    public sealed class ReleaseStock(IOrders orders) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            orders.Release(context.InstanceId);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    public sealed class Charge(IOrders orders) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            orders.Charge(context.InstanceId);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    public sealed class Refund(IOrders orders) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            orders.Refund(context.InstanceId);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    public sealed class Ship(IOrders orders) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            orders.Ship(context.InstanceId);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Records the calls, and fails to ship.</summary>
    private sealed class FakeOrders(List<string> log, bool shipFails = true) : IOrders
    {
        public void Reserve(Guid instanceId) => log.Add("reserve");

        public void Release(Guid instanceId) => log.Add("release");

        public void Charge(Guid instanceId) => log.Add("charge");

        public void Refund(Guid instanceId) => log.Add("refund");

        public void Ship(Guid instanceId)
        {
            log.Add("ship");

            if (shipFails)
            {
                throw new InvalidOperationException("no carrier available");
            }
        }
    }

    [Fact]
    public async Task The_guide_example_rolls_back_in_reverse_order()
    {
        var log = new List<string>();

        var registry = new WorkflowRegistry();
        registry.Register(new FulfilOrder(new FakeOrders(log)));

        var instance = await new WorkflowEngine(registry).StartAsync("fulfil-order", 1);

        // Refund before release: the charge happened last, so it is undone
        // first. Releasing the stock the charge paid for before refunding it
        // would invert the dependency the forward pass established.
        Assert.Equal(["reserve", "charge", "ship", "refund", "release"], log);
        Assert.Equal(InstanceStatus.Compensated, instance.Status);
    }

    [Fact]
    public async Task The_guide_example_undoes_nothing_when_it_succeeds()
    {
        var log = new List<string>();

        var registry = new WorkflowRegistry();
        registry.Register(new FulfilOrder(new FakeOrders(log, shipFails: false)));

        var instance = await new WorkflowEngine(registry).StartAsync("fulfil-order", 1);

        Assert.Equal(["reserve", "charge", "ship"], log);
        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }
}
