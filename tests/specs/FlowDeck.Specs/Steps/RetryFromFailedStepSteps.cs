using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/RetryFromFailedStep.feature.
/// </summary>
[Binding]
[Scope(Feature = "Retrying from the step that failed")]
public sealed class RetryFromFailedStepSteps(ApiContext api)
{
    private readonly List<string> log = [];

    /// <summary>
    /// Which steps have already failed once, per scenario.
    /// </summary>
    /// <remarks>
    /// Held here rather than in the step class so it resets between scenarios,
    /// and shared across executions so a retry gets <i>past</i> the step the
    /// original died on. That is the situation an operator retries in:
    /// something outside the workflow was fixed.
    /// </remarks>
    private readonly HashSet<string> alreadyFailed = new(StringComparer.Ordinal);

    private Guid retried;

    [Given("an instance that ran A and B and failed at C")]
    public async Task GivenAFailureAtC()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder => builder
            .AddStep("A", () => new SpecSteps.Recording(this.log, "A"))
            .AddStep("B", () => new SpecSteps.Recording(this.log, "B"))
            .AddStep("C", () => new FailsOnce(this.log, "C", this.alreadyFailed))
            .AddStep("D", () => new SpecSteps.Recording(this.log, "D"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
        this.log.Clear();
    }

    [Given("an instance whose earlier steps wrote to workflow data before it failed")]
    public async Task GivenDataWrittenBeforeFailure()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder => builder
            .AddStep("write", () => new SpecSteps.Writing("reference", "REF-77"))
            .AddStep("use", () => new ReadingOnce(this.log, "use", "reference", this.alreadyFailed))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
        this.log.Clear();
    }

    [Given("a forked instance that failed on a branch step")]
    public async Task GivenAForkedFailure()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder => builder
            .AddStep("split", () => new SpecSteps.Recording(this.log, "split"))
            .Fork(
                left => left.AddStep("left", () => new FailsOnce(this.log, "left", this.alreadyFailed)),
                right => right.AddStep("right", () => new SpecSteps.Recording(this.log, "right")))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
        this.log.Clear();
    }

    [Given("an instance that failed and was rolled back")]
    public async Task GivenARolledBackInstance()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder => builder
            .AddStep("reserve", () => new SpecSteps.Recording(this.log, "reserve"))
                .WithCompensation(() => new SpecSteps.Recording(this.log, "undo-reserve"))
            .AddStep("charge", () => new SpecSteps.Throwing(this.log, "charge"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
        this.log.Clear();
    }

    [Given("an instance that failed on its first step")]
    public async Task GivenAFirstStepFailure()
    {
        api.Declare(new SpecWorkflow("orders", 1, builder => builder
            .AddStep("first", () => new FailsOnce(this.log, "first", this.alreadyFailed))
            .AddStep("second", () => new SpecSteps.Recording(this.log, "second"))));

        api.InstanceId = (await api.Engine.StartAsync("orders", 1)).Id;
        this.log.Clear();
    }

    [When("I POST to its retry-from-failed-step endpoint")]
    public async Task WhenIRetryFromTheFailure()
    {
        await api.SendAsync(client =>
            client.PostAsync($"/api/instances/{api.InstanceId}/retry-from-failed-step", content: null));

        if (api.Response!.IsSuccessStatusCode)
        {
            using var document = JsonDocument.Parse(api.Body);

            this.retried = document.RootElement.GetProperty("id").GetGuid();
        }
    }

    [Then("the response status is (.*)")]
    public void ThenTheStatusIs(int expected) =>
        Assert.Equal(expected, (int)api.Response!.StatusCode);

    [Then("the new instance runs C onward")]
    public void ThenItRunsFromC() => Assert.Equal(["C", "D"], this.log);

    [Then("A and B do not run again")]
    public void ThenEarlierStepsDoNotRepeat()
    {
        // The point of the action. Repeating a step that already succeeded is
        // not free - it may charge a card twice - which is why an operator
        // reaches for this rather than a plain retry.
        Assert.DoesNotContain("A", this.log);
        Assert.DoesNotContain("B", this.log);
    }

    [Then("the new instance sees what those steps wrote")]
    public void ThenDataCameOver() =>

        // Carried from the original's record, so the failing step reads what
        // the steps before it produced without those steps running again.
        Assert.Contains("use:REF-77", this.log);

    [Then("the original is still Failed")]
    public async Task ThenTheOriginalIsUntouched() =>
        Assert.Equal(InstanceStatus.Failed, (await api.Engine.GetInstanceAsync(api.InstanceId)).Status);

    [Then("the new instance records which one it was retried from")]
    public async Task ThenItRecordsItsOrigin() =>
        Assert.Equal(
            api.InstanceId,
            (await api.Engine.GetInstanceAsync(this.retried)).RetriedFromInstanceId);

    [Then("only the branch that failed runs again")]
    public void ThenOnlyTheFailedBranchRuns()
    {
        // Names are unique across the whole graph (#162), so reconstructing the
        // position from FailedStepName lands inside the branch without this
        // needing machinery of its own - it is the crash-recovery path.
        Assert.Contains("left", this.log);
        Assert.DoesNotContain("right", this.log);
        Assert.DoesNotContain("split", this.log);
    }

    [Then("the new instance runs from the beginning")]
    public void ThenItRunsFromTheStart() =>

        // Nothing to skip, so "from the failing step" and "from the start" are
        // the same run. Refusing would be pedantry.
        Assert.Equal(["first", "second"], this.log);

    /// <summary>Throws the first time it runs, then advances.</summary>
    private sealed class FailsOnce(List<string> log, string name, HashSet<string> alreadyFailed) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            if (alreadyFailed.Add(name))
            {
                throw new InvalidOperationException($"{name} failed once");
            }

            log.Add(name);

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>Throws the first time, then records what it read.</summary>
    private sealed class ReadingOnce(
        List<string> log,
        string name,
        string key,
        HashSet<string> alreadyFailed) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            if (alreadyFailed.Add(name))
            {
                throw new InvalidOperationException($"{name} failed once");
            }

            log.Add($"{name}:{(context.Data.TryGet<string>(key, out var value) ? value : "missing")}");

            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
