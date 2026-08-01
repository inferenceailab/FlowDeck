using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Persistence/VersionFilter.feature.
/// </summary>
[Binding]
[Scope(Feature = "Filtering instances by definition version")]
public sealed class VersionFilterSteps(StoreContext stores)
{
    private int counted;
    private IReadOnlyList<WorkflowInstanceRecord> listed = [];

    private static WorkflowInstanceRecord Record(int version, InstanceStatus status, string id = "orders") => new()
    {
        Id = Guid.NewGuid(),
        DefinitionId = id,
        DefinitionVersion = version,
        Status = status,
        CurrentStepIndex = 0,
        CurrentStepName = "work",
        CreatedAt = DateTimeOffset.UnixEpoch,
    };

    [Given("instances of \"(.*)\" v(.*) and \"(.*)\" v(.*)")]
    public async Task GivenTwoVersions(string firstId, int firstVersion, string secondId, int secondVersion)
    {
        var store = stores.UseInMemoryIfUnset();

        await store.CreateAsync(Record(firstVersion, InstanceStatus.Running, firstId));
        await store.CreateAsync(Record(firstVersion, InstanceStatus.Suspended, firstId));
        await store.CreateAsync(Record(secondVersion, InstanceStatus.Running, secondId));
    }

    [Given("a completed, a cancelled and a suspended instance of \"(.*)\" v(.*)")]
    public async Task GivenAMixOfStatuses(string id, int version)
    {
        var store = stores.UseInMemoryIfUnset();

        await store.CreateAsync(Record(version, InstanceStatus.Completed, id));
        await store.CreateAsync(Record(version, InstanceStatus.Cancelled, id));
        await store.CreateAsync(Record(version, InstanceStatus.Suspended, id));
    }

    [Given("a (.*) store holding instances of two versions")]
    public async Task GivenAProviderWithTwoVersions(string provider)
    {
        var store = stores.Use(provider);

        await store.CreateAsync(Record(1, InstanceStatus.Running));
        await store.CreateAsync(Record(2, InstanceStatus.Running));
    }

    [When("I count instances of \"(.*)\" v(.*)")]
    public async Task WhenICountAVersion(string id, int version) =>
        this.counted = await stores.Store.CountAsync(new InstanceFilter
        {
            DefinitionId = id,
            DefinitionVersion = version,
        });

    [When("I count the instances still holding \"(.*)\" v(.*)")]
    public async Task WhenICountActive(string id, int version) =>
        this.counted = await stores.Store.CountAsync(new InstanceFilter
        {
            DefinitionId = id,
            DefinitionVersion = version,
            ActiveOnly = true,
        });

    [When("they are listed with a version filter")]
    public async Task WhenListedWithAVersionFilter() =>
        this.listed = await stores.Store.ListAsync(new InstanceFilter
        {
            DefinitionId = "orders",
            DefinitionVersion = 1,
        });

    [Then("only the v1 instances are counted")]
    public void ThenOnlyV1IsCounted() => Assert.Equal(2, this.counted);

    [Then("only the suspended one is counted")]
    public void ThenOnlyTheSuspendedIsCounted() =>

        // Completed and Cancelled are terminal and keep their version forever.
        // Counting them would mean no version could ever be retired.
        Assert.Equal(1, this.counted);

    [Then("only that version's instances come back")]
    public void ThenOnlyThatVersionComesBack()
    {
        Assert.Single(this.listed);
        Assert.Equal(1, this.listed[0].DefinitionVersion);
    }
}
