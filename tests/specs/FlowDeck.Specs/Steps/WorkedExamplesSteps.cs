using FlowDeck.Core;
using FlowDeck.Samples;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Samples/WorkedExamples.feature.
/// </summary>
/// <remarks>
/// Runs <see cref="SampleData.SeedAsync"/> itself rather than a copy of it. A
/// spec that re-implements the seed would assert its own arrangement and pass
/// while the seed a developer actually gets is broken.
/// </remarks>
[Binding]
[Scope(Feature = "FlowDeck runs locally with worked examples")]
public sealed class WorkedExamplesSteps(EngineContext world, ApiContext api)
{
    private readonly WorkflowRegistry registry = new();

    private IReadOnlyList<WorkflowInstance> instances = [];
    private WorkflowInstance? review;
    private WorkflowEngine? engine;

    /// <summary>One engine over one registry, however many steps ask for it.</summary>
    /// <remarks>
    /// Registration is not idempotent - a definition registered twice is a
    /// duplicate rather than a no-op - so a scenario with both a Given and a
    /// When that need the samples must not register them twice.
    /// </remarks>
    private WorkflowEngine Engine()
    {
        if (this.engine is null)
        {
            this.registry.AddSamples();
            this.engine = new WorkflowEngine(this.registry, store: world.Store);
        }

        return this.engine;
    }

    [Given("the sample definitions are registered")]
    public void GivenTheSamples() => this.Engine();

    [When("every sample is run")]
    public async Task WhenEverySampleIsRun()
    {
        await SampleData.SeedAsync(this.Engine());

        this.instances = await this.Engine().ListInstancesAsync();
    }

    [Given("a suspended sample review")]
    public async Task GivenASuspendedReview()
    {
        await this.WhenEverySampleIsRun();

        this.review = this.instances.Single(instance =>
            instance.DefinitionId == "document-review" && instance.Status == InstanceStatus.Suspended);
    }

    [When("it is resumed")]
    public async Task WhenResumed() => this.review = await this.Engine().ResumeAsync(this.review!.Id);

    [Then("one is a straight line, one forks, and one branches on a condition")]
    public void ThenEveryShapeIsCovered()
    {
        var shapes = this.registry.GetAll().Select(WorkflowGraph.Of).ToList();

        Assert.Contains(shapes, steps => steps.All(step => step.Branches.Count == 0));
        Assert.Contains(shapes, steps => steps.Any(step => step.Branches.Any(branch => branch.IsParallel)));

        // A condition rather than any non-parallel branch: a choice selected by
        // a predicate is what the run view can explain, and it is the one the
        // dashboard draws differently.
        Assert.Contains(
            shapes,
            steps => steps.Any(step => step.Branches.Any(branch => branch.Condition is not null)));
    }

    [Then("at least one step declares a compensation")]
    public void ThenSomethingCompensates() =>
        Assert.Contains(
            this.registry.GetAll().SelectMany(WorkflowGraph.Of),
            step => step.Compensation is not null);

    [Then("at least one step declares a retry policy")]
    public void ThenSomethingRetries() =>
        Assert.Contains(
            this.registry.GetAll().SelectMany(WorkflowGraph.Of),
            step => step.RetryPolicy.MaxAttempts > 1);

    [Then("one instance completed, one compensated, and one suspended")]
    public void ThenEveryStateIsCovered()
    {
        // The three the dashboard badges differently and the operator actions
        // key off. Seeding only completed runs would leave Resume, Retry and
        // the rollback view with nothing to act on.
        Assert.Contains(this.instances, instance => instance.Status == InstanceStatus.Completed);
        Assert.Contains(this.instances, instance => instance.Status == InstanceStatus.Compensated);
        Assert.Contains(this.instances, instance => instance.Status == InstanceStatus.Suspended);
    }

    [Then("the order fulfilment history shows a failed attempt followed by a successful one")]
    public async Task ThenTheOrderRetried()
    {
        var order = this.instances.First(instance => instance.DefinitionId == "order-fulfilment");
        var history = await world.Store.GetHistoryAsync(order.Id);

        var charge = history.Where(entry => entry.StepName == "charge-card").ToList();

        // Two entries for one step, in this order. A history with only the
        // success would mean the retry never happened, and the sample would be
        // teaching that a retry policy is decoration.
        Assert.Equal(2, charge.Count);
        Assert.Equal(StepStatus.Failed, charge[0].Status);
        Assert.Equal(1, charge[0].Attempt);
        Assert.Equal(StepStatus.Success, charge[1].Status);
        Assert.Equal(2, charge[1].Attempt);
    }

    [Then("the reconciliation instance recorded its ledger step as undone")]
    public async Task ThenTheLedgerWasUndone()
    {
        var reconciliation = this.instances.Single(instance =>
            instance.DefinitionId == "nightly-reconciliation");

        var record = await world.Store.FindAsync(reconciliation.Id);

        // Evidence in workflow data, not merely a Compensated badge. A
        // compensation that ran and undid nothing is the failure mode a sample
        // is most likely to hide.
        Assert.Equal(InstanceStatus.Compensated, reconciliation.Status);
        Assert.True(record!.Data.TryGetValue("ledger.undone", out var undone));
        Assert.True(undone is true, "the compensation ran but recorded nothing");
    }

    [Then("it completes and its approval was recorded")]
    public async Task ThenTheReviewCompleted()
    {
        Assert.Equal(InstanceStatus.Completed, this.review!.Status);

        var record = await world.Store.FindAsync(this.review.Id);

        // The conditional branch was taken, not merely present. Its step writes
        // this, so its absence would mean the resume skipped past the branch.
        Assert.True(record!.Data.ContainsKey("published"));
    }

    [Given("a host started without the samples flag")]
    public static void GivenNoFlag()
    {
        // Nothing to arrange: not setting it is the arrangement. ApiContext
        // hosts the shipping composition root in Development, which is exactly
        // the case an environment check alone would get wrong - seeding
        // business fiction into every API test's fixture.
    }

    [When(@"I GET \/api\/workflows")]
    public async Task WhenIListWorkflows() => await api.SendAsync(client => client.GetAsync("/api/workflows"));

    [Then("no definition is registered")]
    public void ThenNothingIsRegistered() => Assert.Equal("[]", api.Body);
}
