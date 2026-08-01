using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Microsoft.Extensions.Logging;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Observability/InstanceLogging.feature.
/// </summary>
/// <remarks>
/// Asserts on <see cref="EventId.Name"/> and on structured fields rather than on
/// message text. The rendered message is prose and will be reworded; the event
/// name and the fields are the contract an operator's alert rule and a
/// structured sink actually key on.
/// </remarks>
[Binding]
[Scope(Feature = "Instance lifecycle logging")]
public sealed class InstanceLoggingSteps(EngineContext world)
{
    private WorkflowInstance? completed;
    private WorkflowInstance? failed;
    private WorkflowInstance? cancelled;

    [Given("a definition \"(.*)\" version (.*) with one step")]
    public void GivenADefinition(string id, int version) =>
        world.Declare(id, version, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

    [Given("a definition whose only step throws")]
    public void GivenAThrowingDefinition() =>
        world.Declare("order-fulfilment", 3, builder =>
            builder.AddStep("charge", () => new SpecSteps.Throwing(world.Log, "charge")));

    [Given("a definition that completes, one that fails and one that is cancelled")]
    public void GivenThreeDefinitions()
    {
        world.Declare("completes", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(world.Log, "work")));

        world.Declare("fails", 1, builder =>
            builder.AddStep("charge", () => new SpecSteps.Throwing(world.Log, "charge")));

        // Suspends rather than completes, so there is still a live instance for
        // the cancel to act on. Cancelling a terminal instance is refused
        // (ADR-0008), so a completed one would fail the arrangement, not the
        // assertion.
        world.Declare("cancels", 1, builder =>
            builder.AddStep("wait", () => new SpecSteps.Suspending(world.Log, "wait")));
    }

    [When("an instance is started")]
    public async Task WhenAnInstanceIsStarted()
    {
        var declaration = world.Only;

        world.Instance = await world.Engine().StartAsync(declaration.Id, declaration.Version);
    }

    [When("an instance is started on an engine given no logger")]
    public async Task WhenStartedWithoutALogger()
    {
        var declaration = world.Only;

        world.Instance = await world.UnloggedEngine().StartAsync(declaration.Id, declaration.Version);
    }

    [When("each reaches its terminal state")]
    public async Task WhenEachReachesTerminal()
    {
        var engine = world.Engine();

        this.completed = await engine.StartAsync("completes", 1);
        this.failed = await engine.StartAsync("fails", 1);

        var suspended = await engine.StartAsync("cancels", 1);
        this.cancelled = await engine.CancelAsync(suspended.Id);
    }

    [Then("it completes")]
    public void ThenItCompletes() =>
        Assert.Equal(InstanceStatus.Completed, world.Instance!.Status);

    [Then("a log entry records that it started")]
    public void ThenStartIsLogged() => Assert.Single(world.Logger.Named("InstanceStarted"));

    [Then("that entry carries the instance id, definition id and version")]
    public void ThenTheStartEntryCarriesIdentity()
    {
        var entry = world.Logger.Named("InstanceStarted").Single();

        Assert.Equal(world.Instance!.Id, entry.Field("InstanceId"));
        Assert.Equal("order-fulfilment", entry.Field("DefinitionId"));
        Assert.Equal(3, entry.Field("DefinitionVersion"));
    }

    [Then("each logs its own outcome and not another's")]
    public void ThenEachOutcomeIsDistinct()
    {
        // By instance, not merely by count. Three entries of the right names
        // would pass a count check even if all three described one instance.
        Assert.Equal(this.completed!.Id, Only("InstanceCompleted"));
        Assert.Equal(this.failed!.Id, Only("InstanceFailed"));
        Assert.Equal(this.cancelled!.Id, Only("InstanceCancelled"));

        // And nothing claimed an outcome that did not happen: no instance here
        // rolls anything back, because none declares a compensating action.
        Assert.Empty(world.Logger.Named("InstanceCompensated"));

        object? Only(string eventName) => world.Logger.Named(eventName).Single().Field("InstanceId");
    }

    [Then("the failure logs the failing step name and the error type")]
    public void ThenTheFailureNamesTheStep()
    {
        var entry = world.Logger.Named("InstanceFailed").Single();

        Assert.Equal("charge", entry.Field("FailedStepName"));
        Assert.Equal(nameof(InvalidOperationException), entry.Field("ErrorType"));
    }

    [Then("every entry the engine emitted carries that instance id")]
    public void ThenEveryEntryCarriesTheId()
    {
        Assert.NotEmpty(world.Logger.Entries);

        // Every one, without each call site repeating it - which is what a
        // scope buys and why a field per message would eventually miss one.
        Assert.All(
            world.Logger.Entries,
            entry => Assert.Equal(world.Instance!.Id, entry.Field("InstanceId")));
    }

    [Then("the failure is logged as an error")]
    public void ThenTheFailureIsAnError() =>
        Assert.Equal(LogLevel.Error, world.Logger.Named("InstanceFailed").Single().Level);

    [Then("no ordinary progress entry is logged as an error")]
    public void ThenProgressIsNotAnError()
    {
        // Otherwise "log the failure at Error" is satisfied by logging
        // everything at Error, and an operator's alert rule matches every run.
        var errors = world.Logger.Entries.Where(entry => entry.Level >= LogLevel.Error).ToArray();

        Assert.All(errors, entry => Assert.Equal("InstanceFailed", entry.EventId.Name));
    }
}
