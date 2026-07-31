using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #108 - Document that retried steps must be idempotent.
///
/// Scenario: The usage guide states the requirement
/// Scenario: The requirement is compiled
/// </summary>
/// <remarks>
/// The most consequential thing ADR-0020 implies and the one an author is
/// least likely to think about: a step that charges a card and is retried
/// charges twice. Only the author can prevent that, so the requirement has to
/// reach them before the retry does.
///
/// <para>
/// Two kinds of test here, both deliberate. The examples are compiled and run,
/// so a guide that stops matching the engine breaks the build. The prose is
/// asserted against the file, because a warning that quietly disappears in an
/// unrelated edit is exactly how an author ends up charging a card twice.
/// </para>
/// </remarks>
public class RetryDocumentationTests
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

    // --- "The usage guide states the requirement" ---------------------------

    [Fact]
    public void The_guide_has_a_retry_section()
    {
        Assert.Contains("## Retry", Guide(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_guide_states_that_a_retried_step_runs_again_in_full()
    {
        // Not "resumes from where it threw". A step is the unit of retry, so
        // everything before the throw happens a second time - which is the
        // whole reason idempotency is the author's problem.
        Assert.Contains("runs again in full", Guide(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_guide_states_that_the_engine_offers_no_duplicate_protection()
    {
        // Stated as an absence rather than left to be inferred from silence.
        // An author who assumes the engine deduplicates will not go looking for
        // a sentence that is not there.
        Assert.Contains("no duplicate protection", Guide(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_guide_shows_an_idempotency_key_example()
    {
        var guide = Guide();

        // The phrase, so an author skimming finds it, and the code, so the
        // example shows a key actually being passed rather than only described.
        Assert.Contains("idempotency key", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("var idempotencyKey =", guide, StringComparison.Ordinal);
        Assert.Contains("ChargeAsync(idempotencyKey", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void The_warning_is_in_the_retry_section_not_buried_elsewhere()
    {
        // A requirement documented three sections away from the feature that
        // creates it is documented in name only.
        var guide = Guide();
        var retrySection = guide[guide.IndexOf("## Retry", StringComparison.Ordinal)..];
        var nextSection = retrySection.IndexOf("\n## ", StringComparison.Ordinal);

        if (nextSection > 0)
        {
            retrySection = retrySection[..nextSection];
        }

        Assert.Contains("idempotent", retrySection, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no duplicate protection", retrySection, StringComparison.Ordinal);
    }

    // --- "The requirement is compiled" --------------------------------------

    /// <summary>
    /// The guide's idempotent step, verbatim.
    /// </summary>
    public sealed class ChargeCard(IPaymentGateway gateway) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            // Derived from the instance id, so every attempt at this step sends
            // the same key. A key generated per execution would be a new key on
            // every retry, which is the same as having none.
            var idempotencyKey = $"{context.InstanceId}:{context.StepName}";

            await gateway.ChargeAsync(idempotencyKey, amount: 4200, cancellationToken).ConfigureAwait(false);

            return Outcome.Next;
        }
    }

    public interface IPaymentGateway
    {
        Task ChargeAsync(string idempotencyKey, int amount, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// A gateway that honours idempotency keys, as a real one does.
    /// </summary>
    private sealed class FakeGateway : IPaymentGateway
    {
        private readonly HashSet<string> keys = new(StringComparer.Ordinal);

        public int Charges { get; private set; }

        public int Calls { get; private set; }

        public bool FailNext { get; set; } = true;

        public Task ChargeAsync(string idempotencyKey, int amount, CancellationToken cancellationToken = default)
        {
            this.Calls++;

            if (this.FailNext)
            {
                // The call reached the gateway and the charge was recorded
                // before the response was lost. This is the case that makes
                // retry dangerous: from the step's point of view it failed.
                this.keys.Add(idempotencyKey);
                this.Charges++;
                this.FailNext = false;

                throw new TimeoutException("gateway timed out");
            }

            if (this.keys.Add(idempotencyKey))
            {
                this.Charges++;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class PaymentWorkflow(IPaymentGateway gateway) : IWorkflowDefinition
    {
        public string Id => "payment";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) =>
            builder.AddStep("charge", () => new ChargeCard(gateway), RetryPolicy.FixedDelay(3, TimeSpan.Zero));
    }

    [Fact]
    public async Task The_idempotent_step_sample_charges_once_despite_a_retry()
    {
        // The sample earning its place. The gateway is called twice - the first
        // call charged and then timed out - and the card is charged once,
        // because both calls carried the same key.
        var gateway = new FakeGateway();

        var registry = new WorkflowRegistry();
        registry.Register(new PaymentWorkflow(gateway));

        var instance = await new WorkflowEngine(registry).StartAsync("payment", 1);

        Assert.Equal(InstanceStatus.Completed, instance.Status);
        Assert.Equal(2, gateway.Calls);
        Assert.Equal(1, gateway.Charges);
    }

    [Fact]
    public async Task Without_a_stable_key_the_same_run_charges_twice()
    {
        // The counter-example, so the sample is not just decoration. A key
        // generated per execution is a new key on every retry, and an
        // idempotent gateway cannot help.
        var gateway = new FakeGateway();

        var registry = new WorkflowRegistry();
        registry.Register(new UnstableKeyWorkflow(gateway));

        await new WorkflowEngine(registry).StartAsync("unstable", 1);

        Assert.Equal(2, gateway.Calls);
        Assert.Equal(2, gateway.Charges);
    }

    /// <summary>Generates a fresh key per execution. Deliberately wrong.</summary>
    private sealed class ChargeWithFreshKey(IPaymentGateway gateway) : IStep
    {
        public async ValueTask<Outcome> ExecuteAsync(
            IStepContext context,
            CancellationToken cancellationToken = default)
        {
            await gateway.ChargeAsync(Guid.NewGuid().ToString(), 4200, cancellationToken).ConfigureAwait(false);

            return Outcome.Next;
        }
    }

    private sealed class UnstableKeyWorkflow(IPaymentGateway gateway) : IWorkflowDefinition
    {
        public string Id => "unstable";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) =>
            builder.AddStep("charge", () => new ChargeWithFreshKey(gateway), RetryPolicy.FixedDelay(3, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_step_is_the_unit_of_retry_so_earlier_work_repeats()
    {
        // What "runs again in full" means, asserted rather than asserted about.
        // The step does two things; the retry does both again.
        var effects = new List<string>();

        var registry = new WorkflowRegistry();
        registry.Register(new TwoEffectWorkflow(effects));

        await new WorkflowEngine(registry).StartAsync("two-effects", 1);

        Assert.Equal(["reserve", "reserve", "ship"], effects);
    }

    private sealed class TwoEffects(List<string> effects) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            effects.Add("reserve");

            if (effects.Count(effect => effect == "reserve") == 1)
            {
                throw new InvalidOperationException("shipping unavailable");
            }

            effects.Add("ship");

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class TwoEffectWorkflow(List<string> effects) : IWorkflowDefinition
    {
        public string Id => "two-effects";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) =>
            builder.AddStep("reserve-and-ship", () => new TwoEffects(effects), RetryPolicy.FixedDelay(2, TimeSpan.Zero));
    }
}
