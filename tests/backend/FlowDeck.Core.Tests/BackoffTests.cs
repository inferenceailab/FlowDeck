using FlowDeck.Core;

namespace FlowDeck.Core.Tests;

/// <summary>
/// Issue #105 - Back off between attempts, with jitter.
///
/// Scenario: Exponential backoff grows the delay
/// Scenario: Jitter desynchronises instances
/// Scenario: A fixed policy waits the same each time
/// </summary>
public class BackoffTests
{
    /// <summary>
    /// A policy without jitter, so growth can be asserted exactly.
    /// </summary>
    private static RetryPolicy Exponential(TimeSpan baseDelay, TimeSpan? maxDelay = null) => new()
    {
        MaxAttempts = 10,
        BaseDelay = baseDelay,
        MaxDelay = maxDelay ?? TimeSpan.FromHours(1),
        Backoff = BackoffStrategy.Exponential,
    };

    [Fact]
    public void Exponential_backoff_doubles_each_attempt()
    {
        // Given an exponential policy with a base delay of 1 second
        var policy = Exponential(TimeSpan.FromSeconds(1));

        // When delays are computed for attempts 2, 3 and 4
        var delays = new[] { 2, 3, 4 }.Select(attempt => policy.DelayBefore(attempt)).ToArray();

        // Then each is at least double the previous
        Assert.Equal(TimeSpan.FromSeconds(1), delays[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), delays[1]);
        Assert.Equal(TimeSpan.FromSeconds(4), delays[2]);
    }

    [Fact]
    public void The_first_execution_is_not_a_retry_and_waits_nothing()
    {
        // Attempt 1 is the original execution. Delaying before it would make
        // every workflow with a retry policy slower to start for no reason.
        Assert.Equal(TimeSpan.Zero, Exponential(TimeSpan.FromSeconds(5)).DelayBefore(1));
        Assert.Equal(TimeSpan.Zero, RetryPolicy.FixedDelay(3, TimeSpan.FromSeconds(5)).DelayBefore(1));
    }

    [Fact]
    public void A_fixed_policy_waits_the_same_each_time()
    {
        var policy = RetryPolicy.FixedDelay(5, TimeSpan.FromSeconds(2));

        foreach (var attempt in new[] { 2, 3, 4 })
        {
            Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayBefore(attempt));
        }
    }

    [Fact]
    public void Growth_is_capped_so_an_instance_never_appears_hung()
    {
        // Without a ceiling, doubling reaches hours within a dozen attempts.
        var policy = Exponential(TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(10));

        Assert.Equal(TimeSpan.FromSeconds(8), policy.DelayBefore(5));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayBefore(6));
        Assert.Equal(TimeSpan.FromSeconds(10), policy.DelayBefore(20));
    }

    [Fact]
    public void Jitter_desynchronises_instances()
    {
        // The reason jitter exists. Exponential without it synchronises every
        // instance that failed at the same moment, so they all retry together
        // and hit the recovering service simultaneously.
        var policy = RetryPolicy.ExponentialBackoff(5, TimeSpan.FromSeconds(4));
        var random = new Random(12345);

        var delays = Enumerable
            .Range(0, 50)
            .Select(_ => policy.DelayBefore(3, random))
            .ToArray();

        Assert.True(delays.Distinct().Count() > 1, "jittered delays were all identical");
    }

    [Fact]
    public void Every_jittered_delay_stays_within_the_policy_bounds()
    {
        // Jitter must spread the delay, not escape the ceiling. A delay above
        // MaxDelay would defeat the cap that stops an instance appearing hung.
        var policy = RetryPolicy.ExponentialBackoff(
            10, TimeSpan.FromSeconds(1), maxDelay: TimeSpan.FromSeconds(30));

        var random = new Random(999);

        foreach (var attempt in Enumerable.Range(2, 12))
        {
            for (var i = 0; i < 25; i++)
            {
                var delay = policy.DelayBefore(attempt, random);

                Assert.True(delay > TimeSpan.Zero, $"attempt {attempt} produced a zero delay");
                Assert.True(delay <= policy.MaxDelay, $"attempt {attempt} produced {delay}, above MaxDelay");
            }
        }
    }

    [Fact]
    public void A_jittered_delay_is_never_zero()
    {
        // Full jitter draws from (0, window]. A retry that waits no time at all
        // is not a backoff, and would hammer a failing service as fast as the
        // loop can turn.
        var policy = RetryPolicy.ExponentialBackoff(5, TimeSpan.FromSeconds(10));

        // A generator that always returns the bottom of the range - the worst
        // case for this property.
        var alwaysZero = new StubRandom(0.0);

        Assert.True(policy.DelayBefore(3, alwaysZero) > TimeSpan.Zero);
    }

    /// <summary>Returns a fixed value, to pin a boundary case.</summary>
    private sealed class StubRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    [Fact]
    public void Jitter_is_reproducible_from_a_seed()
    {
        // So a backoff test can assert on an exact value rather than a range.
        // Randomness that cannot be pinned makes a test either flaky or
        // vacuous.
        var policy = RetryPolicy.ExponentialBackoff(5, TimeSpan.FromSeconds(4));

        var first = Enumerable.Range(0, 10).Select(_ => policy.DelayBefore(3, new Random(7))).ToArray();
        var second = Enumerable.Range(0, 10).Select(_ => policy.DelayBefore(3, new Random(7))).ToArray();

        Assert.Equal(first, second);
    }

    [Fact]
    public void Jitter_is_the_default_for_the_exponential_factory()
    {
        // Anyone reaching for exponential backoff wants jitter; making them ask
        // for it means most callers will not.
        Assert.Equal(BackoffStrategy.ExponentialWithJitter, RetryPolicy.ExponentialBackoff(3).Backoff);
    }

    [Fact]
    public async Task The_engine_waits_between_attempts()
    {
        // A policy that computes a delay is worth nothing if the engine ignores
        // it, so this asserts the engine actually honours it.
        //
        // FakeTimeProvider fakes CreateTimer as well as the clock, so
        // Task.Delay inside the engine completes when the clock is advanced
        // rather than when real time passes. With only a fake clock this test
        // slept for three seconds and its comment claimed otherwise.
        var clock = new TestTimeProvider(new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
        var step = new ThrowsTwice();

        var registry = new WorkflowRegistry();
        registry.Register(new OneStep(step));
        var engine = new WorkflowEngine(registry, clock, random: new Random(3));

        var run = engine.StartAsync("delayed", 1);

        // Each advance releases one pending backoff. The policy caps at one
        // minute, so a minute per turn is enough for any of them.
        while (!run.IsCompleted)
        {
            clock.Advance(TimeSpan.FromMinutes(1));

            // Yields so the engine's continuation can run; it does not wait.
            await Task.Yield();
        }

        var instance = await run;

        Assert.Equal(3, step.Executions);
        Assert.Equal(InstanceStatus.Completed, instance.Status);

        // The clock only moved because this test moved it, so a real backoff
        // cost the suite nothing.
        Assert.True(clock.GetUtcNow() > new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));
    }

    private sealed class ThrowsTwice : IStep
    {
        public int Executions { get; private set; }

        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            this.Executions++;

            if (this.Executions <= 2)
            {
                throw new InvalidOperationException("transient");
            }

            return ValueTask.FromResult(Outcome.Next);
        }
    }

    private sealed class OneStep(IStep body) : IWorkflowDefinition
    {
        public string Id => "delayed";

        public int Version => 1;

        public void Build(IWorkflowBuilder builder) =>
            builder.AddStep("work", () => body, RetryPolicy.ExponentialBackoff(3, TimeSpan.FromSeconds(2)));
    }
}
