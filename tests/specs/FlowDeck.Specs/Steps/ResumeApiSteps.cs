using System.Net;
using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/Resume.feature.
/// </summary>
[Binding]
[Scope(Feature = "Resuming an instance over HTTP")]
public sealed class ResumeApiSteps(ApiContext api)
{
    private readonly List<string> log = [];

    private HttpStatusCode second;

    [Given("a suspended instance")]
    public async Task GivenASuspendedInstance()
    {
        // Parks once, then advances. So a resume visibly moves the instance on
        // rather than leaving it where it was, which is what the Then checks.
        api.Declare(new SpecWorkflow("orders", 1, builder => builder
            .AddStep("wait", () => new SuspendsOnce(this.log, "wait"))
            .AddStep("after", () => new SpecSteps.Recording(this.log, "after"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
    }

    [Given("a suspended instance whose step suspends every time")]
    public async Task GivenAnAlwaysSuspendingInstance()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder =>
            builder.AddStep("wait", () => new SpecSteps.Suspending(this.log, "wait"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
    }

    [Given("a completed instance")]
    public async Task GivenACompletedInstance()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder =>
            builder.AddStep("work", () => new SpecSteps.Recording(this.log, "work"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
    }

    [Given("an instance suspended by a host that has since gone")]
    public async Task GivenAnInstanceSuspendedByAnotherHost()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder => builder
            .AddStep("wait", () => new SuspendsOnce(this.log, "wait"))
            .AddStep("after", () => new SpecSteps.Recording(this.log, "after"))));

        // Started on an engine of its own over the API's store, then discarded.
        // That is what "a host that has since gone" is: the durable record
        // survives and the object that produced it does not (#68 - resume used
        // to be possible only from inside the process that started it).
        var elsewhere = new WorkflowEngine(api.Registry, store: api.RunningStore);

        api.InstanceId = (await elsewhere.StartAsync("orders", 1)).Id;
    }

    [When("I POST to its resume endpoint")]
    public async Task WhenIResume() =>
        await api.SendAsync(client =>
            client.PostAsync($"/api/instances/{api.InstanceId}/resume", content: null));

    [When("I POST to the resume endpoint of an unknown instance")]
    public async Task WhenIResumeAnUnknownInstance() =>
        await api.SendAsync(client =>
            client.PostAsync($"/api/instances/{Guid.NewGuid()}/resume", content: null));

    [When("a different host resumes it")]
    public async Task WhenADifferentHostResumes() => await this.WhenIResume();

    [When("two callers resume it one after the other")]
    public async Task WhenTwoCallersResume()
    {
        await this.WhenIResume();

        using var again = await api.Client
            .PostAsync($"/api/instances/{api.InstanceId}/resume", content: null);

        this.second = again.StatusCode;
    }

    [Then("the response status is (.*)")]
    public void ThenTheStatusIs(int expected) =>
        Assert.Equal(expected, (int)api.Response!.StatusCode);

    [Then("the instance has moved past the step it was parked on")]
    public async Task ThenItMovedOn()
    {
        Assert.Contains("after", this.log);

        var instance = await api.Engine.GetInstanceAsync(api.InstanceId);

        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    [Then("it continues from the step it was parked on")]
    public async Task ThenItContinued()
    {
        // Two "wait"s: the execution that parked, and the one the resume
        // re-entered. Resume returns to the suspending step rather than
        // skipping it, which is what makes "wait until X" expressible at all -
        // and the second host ran it, which is the whole point of #68.
        Assert.Equal(["wait", "wait", "after"], this.log);

        var instance = await api.Engine.GetInstanceAsync(api.InstanceId);

        Assert.Equal(InstanceStatus.Completed, instance.Status);
    }

    [Then("the response reports it as Suspended")]
    public void ThenItIsStillSuspended()
    {
        Assert.Equal(HttpStatusCode.Accepted, api.Response!.StatusCode);

        using var document = JsonDocument.Parse(api.Body);

        // Parking again is an ordinary outcome, not a failure. A workflow
        // waiting on something that has still not happened suspends once more.
        Assert.Equal(
            nameof(InstanceStatus.Suspended),
            document.RootElement.GetProperty("status").GetString());
    }

    [Then("the first succeeds and the second is refused")]
    public void ThenOnlyOneWins()
    {
        Assert.Equal(HttpStatusCode.Accepted, api.Response!.StatusCode);

        // 409, because by then the instance is no longer suspended. The
        // guard is the status check, not a lock: the second caller finds a
        // state its request cannot apply to, which is exactly what a conflict
        // means.
        Assert.Equal(HttpStatusCode.Conflict, this.second);

        // And the step ran once. Two callers both resuming would execute it
        // twice, which is what NFR-1 forbids.
        Assert.Equal(1, this.log.Count(entry => entry == "after"));
    }

    /// <summary>Parks on its first execution, advances on the next.</summary>
    /// <remarks>
    /// Counts from the log rather than a field, because the engine builds the
    /// step afresh for every execution - a field would reset each time and the
    /// step would suspend forever.
    /// </remarks>
    private sealed class SuspendsOnce(List<string> log, string name) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            var previous = log.Count(entry => entry == name);
            log.Add(name);

            return ValueTask.FromResult(previous == 0 ? Outcome.Suspend : Outcome.Next);
        }
    }
}
