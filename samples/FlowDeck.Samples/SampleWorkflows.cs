using FlowDeck.Core;

namespace FlowDeck.Samples;

/// <summary>
/// What an order carries when it is placed.
/// </summary>
/// <remarks>
/// A record rather than loose arguments, because the engine validates the
/// declared input type before any step runs - a mismatched start is rejected
/// rather than half-executed.
/// </remarks>
public sealed record OrderPlaced(string Reference, decimal Total);

/// <summary>
/// Four workflows that between them exercise everything the dashboard draws.
/// </summary>
/// <remarks>
/// Not a tutorial and not a test suite. These exist so that a clone of FlowDeck
/// starts up with something on the screen: a definition list with more than one
/// shape in it, and instances in enough different states that every badge,
/// every action and every branch marker has an example.
///
/// <para>
/// Registered in Development only (see <c>Program.cs</c>). They are business
/// fiction, and a deployed FlowDeck should list the definitions its host
/// registered and nothing else.
/// </para>
/// </remarks>
public static class SampleWorkflows
{
    /// <summary>
    /// Registers every sample definition.
    /// </summary>
    public static void AddSamples(this WorkflowRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        // Two versions of the same id, deliberately. Version is half the
        // identity of a definition, and a list showing only one of them hides
        // the thing that makes an in-flight instance safe to leave running
        // across a deployment.
        registry.Register(new OrderFulfilment());
        registry.Register(new OrderFulfilmentV2());

        registry.Register(new DocumentReview());
        registry.Register(new NightlyReconciliation());
    }

    /// <summary>
    /// A straight line with compensations: the shape most workflows start as.
    /// </summary>
    public sealed class OrderFulfilment : IWorkflowDefinition<OrderPlaced>
    {
        public string Id => "order-fulfilment";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder
                .AddStep("reserve-stock", () => new SampleSteps.Records("reservation", "3 units held"))
                    .WithCompensation(() => new SampleSteps.Undoes("reservation"))

                // Three attempts, not one. The step fails its first attempt on
                // purpose, so a completed instance here has a retry in its
                // history - which is what makes the history view worth opening.
                .AddStep(
                    "charge-card",
                    () => new SampleSteps.FailsOnce("payment"),
                    RetryPolicy.FixedDelay(3, TimeSpan.FromMilliseconds(200)))
                    .WithCompensation(() => new SampleSteps.Undoes("payment"))

                .AddStep("ship", () => new SampleSteps.Records("shipment", "handed to the courier"));
        }
    }

    /// <summary>
    /// The same order, packed and announced at the same time.
    /// </summary>
    /// <remarks>
    /// Version 2 rather than an edit to version 1. The two are listed side by
    /// side and drawn differently, which is the point: a fork is a different
    /// shape, not a different label.
    /// </remarks>
    public sealed class OrderFulfilmentV2 : IWorkflowDefinition<OrderPlaced>
    {
        public string Id => "order-fulfilment";

        public int Version => 2;

        public void Build(IWorkflowBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder
                .AddStep("reserve-stock", () => new SampleSteps.Records("reservation", "3 units held"))
                    .WithCompensation(() => new SampleSteps.Undoes("reservation"))

                .AddStep(
                    "charge-card",
                    () => new SampleSteps.FailsOnce("payment"),
                    RetryPolicy.FixedDelay(3, TimeSpan.FromMilliseconds(200)))
                    .WithCompensation(() => new SampleSteps.Undoes("payment"))

                // Neither arm needs the other's result, so neither waits for it.
                .Fork(
                    pack => pack.AddStep(
                        "pack-parcel",
                        () => new SampleSteps.Takes(TimeSpan.FromSeconds(1), "parcel")),
                    notify => notify.AddStep(
                        "notify-customer",
                        () => new SampleSteps.Records("notification", "dispatch email queued")))

                .AddStep("ship", () => new SampleSteps.Records("shipment", "handed to the courier"));
        }
    }

    /// <summary>
    /// Stops and waits for a human.
    /// </summary>
    /// <remarks>
    /// The reason a workflow engine exists rather than a background job: this
    /// instance is suspended for as long as the reviewer takes, survives a
    /// restart while it waits, and resumes into the step it stopped on.
    /// </remarks>
    public sealed class DocumentReview : IWorkflowDefinition
    {
        public string Id => "document-review";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder
                .AddStep("submit-draft", () => new SampleSteps.Records("draft", "v3 submitted"))
                .AddStep("await-approval", () => new SampleSteps.WaitsFor("approval"))

                // Runs only if the draft was actually approved. Resuming an
                // instance whose approval never arrived suspends it again, and
                // this branch stays untaken.
                .BranchWhen(
                    "publish",
                    data => data.TryGet<bool>("approval.received", out var approved) && approved,
                    publish => publish.AddStep("publish", () => new SampleSteps.Records("published", "live")));
        }
    }

    /// <summary>
    /// Fails on purpose, and rolls back what it had already done.
    /// </summary>
    /// <remarks>
    /// A failure with nothing to undo teaches nothing. Both steps before the
    /// failing one have compensations, so a completed rollback leaves visible
    /// evidence in workflow data - and the instance ends
    /// <c>Compensated</c> rather than <c>Failed</c>, which is a distinction an
    /// operator has to be able to see before they can act on it.
    /// </remarks>
    public sealed class NightlyReconciliation : IWorkflowDefinition
    {
        public string Id => "nightly-reconciliation";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder
                .AddStep("load-ledger", () => new SampleSteps.Records("ledger", "4,812 rows"))
                    .WithCompensation(() => new SampleSteps.Undoes("ledger"))

                .AddStep("match-transactions", () => new SampleSteps.Records("matched", "4,796 of 4,812"))
                    .WithCompensation(() => new SampleSteps.Undoes("matched"))

                .AddStep(
                    "post-adjustments",
                    () => new SampleSteps.Fails("The adjustments account is closed for the period."));
        }
    }
}
