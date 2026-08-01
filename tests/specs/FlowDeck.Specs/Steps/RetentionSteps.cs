using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Persistence/Retention.feature.
/// </summary>
[Binding]
public sealed class RetentionSteps(EngineContext world)
{
    private static readonly DateTimeOffset Now = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private TimeSpan retention = TimeSpan.FromDays(30);
    private Guid subject;

    [Given("retention is configured to {int} days")]
    public void GivenRetentionIsConfigured(int days) => this.retention = TimeSpan.FromDays(days);

    [Given("a completed instance finished {int} days ago")]
    public async Task GivenACompletedInstanceFinishedDaysAgo(int days)
    {
        this.subject = Guid.NewGuid();

        await world.Store.CreateAsync(new WorkflowInstanceRecord
        {
            Id = this.subject,
            DefinitionId = "order",
            DefinitionVersion = 1,
            Status = InstanceStatus.Completed,
            CurrentStepIndex = 0,
            CreatedAt = Now.AddDays(-days - 1),
            CompletedAt = Now.AddDays(-days),
        });
    }

    [Given("a suspended instance created {int} days ago")]
    public async Task GivenASuspendedInstanceCreatedDaysAgo(int days)
    {
        this.subject = Guid.NewGuid();

        await world.Store.CreateAsync(new WorkflowInstanceRecord
        {
            Id = this.subject,
            DefinitionId = "order",
            DefinitionVersion = 1,
            Status = InstanceStatus.Suspended,
            CurrentStepIndex = 0,
            CurrentStepName = "wait",
            CreatedAt = Now.AddDays(-days),
        });
    }

    [When("the purge job runs")]
    public async Task WhenThePurgeJobRuns() =>
        world.Captured["purged"] = await world.Store.PurgeAsync(Now - this.retention);

    [Then("that instance is removed")]
    public async Task ThenThatInstanceIsRemoved()
    {
        Assert.Null(await world.Store.FindAsync(this.subject));
        Assert.Equal(1, world.Captured["purged"]);
    }

    [Then("that instance is retained")]
    public async Task ThenThatInstanceIsRetained()
    {
        // Age is not evidence that work is finished. A ninety-day-old suspended
        // instance is waiting for something, and purging it would silently
        // destroy in-flight work.
        Assert.NotNull(await world.Store.FindAsync(this.subject));
        Assert.Equal(0, world.Captured["purged"]);
    }
}
