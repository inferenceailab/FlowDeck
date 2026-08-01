using FlowDeck.Core;
using FlowDeck.Core.Persistence;
using FlowDeck.Specs.Support;
using Microsoft.Extensions.Time.Testing;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Resilience/Retry.feature.
/// </summary>
[Binding]
[Scope(Tag = "M5")]
public sealed class RetrySteps(EngineContext world)
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);

    private RetryPolicy policy = RetryPolicy.None;
    private RetryPolicy? workflowDefault;
    private TimeSpan[] delays = [];
    private FakeTimeProvider? clock;

    private int Executions(string name) => world.Log.Count(entry => entry == name);

    // ------------------------------------------------------- declaring (#103, #104)

    [Given("a step declared without a retry policy")]
    public void GivenAStepWithoutAPolicy() => this.policy = RetryPolicy.None;

    [Given("a step declared with a retry policy allowing {int} attempts")]
    public void GivenAStepWithAPolicy(int attempts) =>
        this.policy = RetryPolicy.FixedDelay(attempts, TimeSpan.Zero);

    // Issues #106 and #107 phrase the same arrangement more briefly than #103
    // does. Kept as written in each issue rather than harmonised, so a reader
    // comparing a scenario to the story that asked for it finds the same words.
    [Given("a step with a policy allowing {int} attempts")]
    public void GivenAStepWithAPolicyBriefly(int attempts) => this.GivenAStepWithAPolicy(attempts);

    [Given("a workflow declaring a default policy of {int} attempts")]
    public void GivenAWorkflowDefault(int attempts) =>
        this.workflowDefault = RetryPolicy.FixedDelay(attempts, TimeSpan.Zero);

    [Given("a step declared without its own policy that always throws")]
    public void GivenAStepWithoutItsOwnPolicy() => this.DeclareAlwaysThrows(policy: null);

    [Given("a step declaring a policy of {int} attempts that always throws")]
    public void GivenAStepDeclaringItsOwnPolicy(int attempts) =>
        this.DeclareAlwaysThrows(RetryPolicy.FixedDelay(attempts, TimeSpan.Zero));

    [Given("a step declaring RetryPolicy.None that always throws")]
    public void GivenAStepOptingOut() => this.DeclareAlwaysThrows(RetryPolicy.None);

    [Given("the step always throws")]
    public void GivenTheStepAlwaysThrows() => this.DeclareAlwaysThrows(this.policy);

    [Given("the step throws once and then succeeds")]
    public void GivenTheStepThrowsOnce() => this.DeclareFailsThenSucceeds(this.policy, failures: 1);

    [Given("the step throws twice and then succeeds")]
    public void GivenTheStepThrowsTwice() => this.DeclareFailsThenSucceeds(this.policy, failures: 2);

    private void DeclareAlwaysThrows(RetryPolicy? policy) =>
        world.Declare("retrying", 1, builder =>
        {
            if (this.workflowDefault is { } fallback)
            {
                builder.WithRetryPolicy(fallback);
            }

            builder.AddStep("work", () => new SpecSteps.Throwing(world.Log, "work"), policy);
        });

    private void DeclareFailsThenSucceeds(RetryPolicy policy, int failures) =>
        world.Declare("retrying", 1, builder => builder.AddStep(
            "work",
            () => new FailsThenSucceeds(world.Log, "work", failures),
            policy));

    [When("a retrying instance is started")]
    public async Task WhenARetryingInstanceIsStarted() =>
        world.Instance = await world.Engine().StartAsync("retrying", 1);

    [Then("the step executes exactly {int} time(s)")]
    public void ThenTheStepExecutesExactly(int expected) => Assert.Equal(expected, this.Executions("work"));

    [Then("the retrying instance status becomes {word}")]
    public void ThenTheRetryingInstanceStatusBecomes(string expected) =>
        Assert.Equal(Enum.Parse<InstanceStatus>(expected), world.Instance!.Status);

    // ---------------------------------------------------------- backoff (#105)

    [Given("an exponential policy with a base delay of {int} second")]
    public void GivenAnExponentialPolicy(int seconds) => this.policy = new RetryPolicy
    {
        MaxAttempts = 10,
        BaseDelay = TimeSpan.FromSeconds(seconds),
        MaxDelay = TimeSpan.FromHours(1),

        // Without jitter, so growth can be asserted exactly. The jitter
        // scenario below is where randomness is the subject.
        Backoff = BackoffStrategy.Exponential,
    };

    [Given("an exponential policy with jitter")]
    public void GivenAnExponentialPolicyWithJitter() =>
        this.policy = RetryPolicy.ExponentialBackoff(10, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(30));

    [Given("a fixed policy of {int} seconds")]
    public void GivenAFixedPolicy(int seconds) =>
        this.policy = RetryPolicy.FixedDelay(5, TimeSpan.FromSeconds(seconds));

    [When("delays are computed for attempts {int}, {int} and {int}")]
    public void WhenDelaysAreComputed(int first, int second, int third) =>
        this.delays = [.. new[] { first, second, third }.Select(attempt => this.policy.DelayBefore(attempt))];

    [Then("each delay is at least double the previous")]
    public void ThenEachDelayIsAtLeastDoubleThePrevious()
    {
        for (var i = 1; i < this.delays.Length; i++)
        {
            Assert.True(
                this.delays[i] >= this.delays[i - 1] * 2,
                $"attempt {i + 1} waited {this.delays[i]}, not double {this.delays[i - 1]}");
        }

        // Attempt 1 is the original execution, so it waits nothing. Asserted
        // rather than left implicit: without it the doubling check above is
        // satisfied by a policy that delays before the first execution too.
        Assert.Equal(TimeSpan.Zero, this.delays[0]);
    }

    [Then("every delay is {int} seconds")]
    public void ThenEveryDelayIs(int seconds) =>
        Assert.All(this.delays, delay => Assert.Equal(TimeSpan.FromSeconds(seconds), delay));

    [When("the delay for the same attempt is computed many times")]
    public void WhenTheDelayIsComputedManyTimes()
    {
        var random = new Random(12345);

        this.delays = [.. Enumerable.Range(0, 50).Select(_ => this.policy.DelayBefore(3, random))];
    }

    [Then("the values are not all identical")]
    public void ThenTheValuesAreNotAllIdentical() =>
        Assert.True(this.delays.Distinct().Count() > 1, "every jittered delay was the same");

    [Then("every value is within the policy bounds")]
    public void ThenEveryValueIsWithinBounds() =>
        Assert.All(this.delays, delay =>
        {
            // Never zero: a retry that waits no time is not a backoff, and
            // would hammer a failing service as fast as the loop turns.
            Assert.True(delay > TimeSpan.Zero, "a jittered delay was zero");
            Assert.True(delay <= this.policy.MaxDelay, $"{delay} exceeded MaxDelay");
        });

    [Given("a step with a policy allowing {int} attempts and a {int} second delay")]
    public void GivenAStepWithADelayedPolicy(int attempts, int seconds) =>
        this.policy = RetryPolicy.FixedDelay(attempts, TimeSpan.FromSeconds(seconds));

    [When("a retrying instance runs on a controlled clock")]
    public async Task WhenARetryingInstanceRunsOnAControlledClock()
    {
        this.clock = new FakeTimeProvider(T0);

        var run = world.Engine(this.clock).StartAsync("retrying", 1);

        // Advancing releases each pending backoff. FakeTimeProvider fakes
        // CreateTimer as well as the clock, so Task.Delay inside the engine
        // completes when the clock moves rather than when real time passes.
        while (!run.IsCompleted)
        {
            this.clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Yield();
        }

        world.Instance = await run;
    }

    [Then("the gap between attempts is {int} seconds")]
    public async Task ThenTheGapBetweenAttemptsIs(int seconds)
    {
        var history = await world.Engine(this.clock).GetHistoryAsync(world.Instance!.Id);
        var starts = history.Select(entry => entry.StartedAt).ToArray();

        Assert.Equal(3, starts.Length);

        // At least the configured delay, not exactly it. A backoff guarantees a
        // minimum - retrying sooner would hammer a failing service, retrying
        // later would not - and with jitter a minimum is all it can promise.
        //
        // It is also all this scenario can observe: it drives the clock from
        // outside the engine, so it may advance past the delay before the
        // engine has registered its timer. Asserting equality passed on one
        // machine and reported a four second gap on another.
        for (var attempt = 1; attempt < starts.Length; attempt++)
        {
            var gap = starts[attempt] - starts[attempt - 1];

            Assert.True(
                gap >= TimeSpan.FromSeconds(seconds),
                $"attempt {attempt + 1} began {gap} after the previous one, less than the {seconds}s delay");
        }
    }

    [Then("no real time passes")]
    public void ThenNoRealTimePasses()
    {
        // The clock only moved because the scenario moved it. A suite that
        // slept for its own backoffs is one nobody runs.
        Assert.True(this.clock!.GetUtcNow() > T0);
    }

    // ------------------------------------------------- durable attempts (#106)

    [Given("the step has already failed twice")]
    public async Task GivenTheStepHasAlreadyFailedTwice()
    {
        // Declares the workflow as well as seeding the state. The preceding
        // Given only chooses the policy - the step it applies to is described
        // by this sentence, and without the declaration the restart resumes an
        // instance whose definition the new host has never heard of.
        this.DeclareAlwaysThrows(this.policy);

        // Written straight to the store. The engine only ever suspends with a
        // zero count, so no execution path produces a suspended instance
        // mid-retry; a crash leaves it Running and nothing resumes that yet
        // (#39). Seeding is how the load path is exercised before the recovery
        // path exists - not a claim that the loop is closed.
        var id = Guid.NewGuid();

        await world.Store.CreateAsync(new WorkflowInstanceRecord
        {
            Id = id,
            DefinitionId = "retrying",
            DefinitionVersion = 1,
            Status = InstanceStatus.Suspended,
            CurrentStepIndex = 0,
            CurrentStepName = "work",
            CreatedAt = T0,
            StepAttempts = 2,
        });

        world.Captured["seeded"] = id;
    }

    [When("the host restarts and the instance resumes")]
    public async Task WhenTheHostRestartsAndResumes() =>
        world.Instance = await world.RestartedHost().ResumeAsync((Guid)world.Captured["seeded"]!);

    [Given("a step that failed once and then succeeded")]
    public void GivenAStepThatFailedOnceThenSucceeded()
    {
        // Held until the next Given adds the later step: the scenario describes
        // one workflow across two sentences.
        world.Captured["first-step"] = true;
    }

    [Given("a later step that always throws with {int} attempts allowed")]
    public void GivenALaterStepThatAlwaysThrows(int attempts) =>
        world.Declare("retrying", 1, builder => builder
            .WithRetryPolicy(RetryPolicy.FixedDelay(attempts, TimeSpan.Zero))
            .AddStep("first", () => new FailsThenSucceeds(world.Log, "first", failures: 1))
            .AddStep("later", () => new SpecSteps.Throwing(world.Log, "later")));

    [Then("the later step is attempted {int} times")]
    public void ThenTheLaterStepIsAttempted(int expected)
    {
        // Its own full allowance. Inheriting the first step's count would leave
        // it one attempt, and the log would read first, first, later.
        Assert.Equal(expected, this.Executions("later"));
        Assert.Equal(2, this.Executions("first"));
    }

    // -------------------------------------------------- attempt history (#107)

    [Then("the history contains three entries for that step")]
    public async Task ThenTheHistoryContainsThreeEntries()
    {
        var history = await world.Engine().GetHistoryAsync(world.Instance!.Id);

        Assert.Equal(3, history.Count(entry => entry.StepName == "work"));
    }

    [Then("each records its own error")]
    public async Task ThenEachRecordsItsOwnError()
    {
        var history = await world.Engine().GetHistoryAsync(world.Instance!.Id);

        Assert.All(history, entry =>
        {
            Assert.Equal(StepStatus.Failed, entry.Status);
            Assert.Equal("InvalidOperationException", entry.ErrorType);
            Assert.False(string.IsNullOrWhiteSpace(entry.ErrorMessage));
        });
    }

    [Then("the history reports attempts {int}, {int} and {int}")]
    public async Task ThenTheHistoryReportsAttempts(int first, int second, int third)
    {
        var history = await world.Engine().GetHistoryAsync(world.Instance!.Id);

        Assert.Equal([first, second, third], history.Select(entry => entry.Attempt));
    }

    /// <summary>Fails its first N executions, counting from the shared log.</summary>
    /// <remarks>
    /// Counts from the log rather than a field, because the engine builds the
    /// step afresh for every execution - a field would reset each time and the
    /// step would fail forever.
    /// </remarks>
    private sealed class FailsThenSucceeds(List<string> log, string name, int failures) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            var previous = log.Count(entry => entry == name);
            log.Add(name);

            return previous < failures
                ? throw new InvalidOperationException($"{name} transient {previous + 1}")
                : ValueTask.FromResult(Outcome.Next);
        }
    }
}
