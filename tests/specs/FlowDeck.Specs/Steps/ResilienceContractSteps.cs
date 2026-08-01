using System.Text.Json;
using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Resilience/ResilienceContract.feature.
/// </summary>
[Binding]
[Scope(Tag = "M5")]
public sealed class ResilienceContractSteps(ApiContext api)
{
    private string section = string.Empty;
    private readonly List<string> effects = [];

    private static string Guide()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs")))
        {
            directory = directory.Parent;
        }

        return directory is null
            ? throw new InvalidOperationException("Could not locate the docs directory.")
            : File.ReadAllText(Path.Combine(directory.FullName, "docs", "guides", "defining-a-workflow.md"));
    }

    /// <summary>A heading's section, up to the next heading of the same level.</summary>
    private static string SectionOf(string heading)
    {
        var guide = Guide();
        var start = guide.IndexOf(heading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"the guide has no '{heading}' section");

        var section = guide[start..];
        var next = section.IndexOf("\n## ", StringComparison.Ordinal);

        return next > 0 ? section[..next] : section;
    }

    // ---------------------------------------------------- over HTTP (#122)

    [Given("a Compensated instance exists")]
    public async Task GivenACompensatedInstanceExists()
    {
        api.Declare(new SpecWorkflow("rolls-back", 1, builder => builder
            .AddStep("charge", () => new Noop()).WithCompensation(() => new Noop())
            .AddStep("ship", () => new Explodes())));

        api.InstanceId = (await api.Engine.StartAsync("rolls-back", 1)).Id;
    }

    [Given("instances in more than one terminal status")]
    public async Task GivenInstancesInMoreThanOneTerminalStatus()
    {
        // Both definitions declared before anything starts the host. The
        // registry is a singleton built at startup, so declaring one after the
        // first request reaches a registry that has already been built - the
        // definition is simply absent, and the scenario fails saying so.
        api.Declare(new SpecWorkflow("rolls-back", 1, builder => builder
            .AddStep("charge", () => new Noop()).WithCompensation(() => new Noop())
            .AddStep("ship", () => new Explodes())));

        api.Declare(new SpecWorkflow("plain-failure", 1, builder =>
            builder.AddStep("ship", () => new Explodes())));

        api.InstanceId = (await api.Engine.StartAsync("rolls-back", 1)).Id;
        await api.Engine.StartAsync("plain-failure", 1);
    }

    [When("I read it over HTTP")]
    public async Task WhenIReadItOverHttp() =>
        await api.SendAsync(client => client.GetAsync($"/api/instances/{api.InstanceId}"));

    [Then("its status serialises as {string}")]
    public void ThenItsStatusSerialisesAs(string expected)
    {
        // Asserted against the raw body, not a deserialised enum. Round-tripping
        // through the same converter would pass even if the wire value were the
        // integer 5, which is what a client without the converter would read.
        Assert.Contains($"\"status\":\"{expected}\"", api.Body, StringComparison.Ordinal);
    }

    [When("I filter by {word}")]
    public async Task WhenIFilterBy(string status) =>
        await api.SendAsync(client => client.GetAsync($"/api/instances?status={status}"));

    [Then("only compensated instances are listed")]
    public void ThenOnlyCompensatedInstancesAreListed()
    {
        var items = JsonDocument.Parse(api.Body).RootElement.GetProperty("items").EnumerateArray().ToArray();

        Assert.Single(items);
        Assert.Equal("Compensated", items[0].GetProperty("status").GetString());
    }

    // ------------------------------------------------- the guide (#108, #123)

    [Given("the retry section of the usage guide")]
    public void GivenTheRetrySection() => this.section = SectionOf("## Retry");

    [Given("the compensation section of the usage guide")]
    public void GivenTheCompensationSection() => this.section = SectionOf("## Compensation");

    [Then("it states that a retried step runs again in full")]
    public void ThenItStatesAStepRunsAgainInFull() =>
        Assert.Contains("runs again in full", this.section, StringComparison.Ordinal);

    [Then("it states that the engine offers no duplicate protection")]
    public void ThenItStatesNoDuplicateProtection() =>
        Assert.Contains("no duplicate protection", this.section, StringComparison.Ordinal);

    [Then("it shows an idempotency-key example")]
    public void ThenItShowsAnIdempotencyKeyExample()
    {
        Assert.Contains("idempotency key", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("var idempotencyKey =", this.section, StringComparison.Ordinal);
    }

    [Then("it shows how to declare a compensating action")]
    public void ThenItShowsHowToDeclareCompensation() =>
        Assert.Contains("WithCompensation", this.section, StringComparison.Ordinal);

    [Then("it states that rollback runs in reverse order")]
    public void ThenItStatesReverseOrder() =>
        Assert.Contains("reverse", this.section, StringComparison.OrdinalIgnoreCase);

    [Then("it states that rollback continues past a failing action")]
    public void ThenItStatesRollbackContinues()
    {
        Assert.Contains("continues", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("does not stop", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that compensation is best-effort")]
    public void ThenItStatesBestEffort() =>
        Assert.Contains("best-effort", this.section, StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------ the compiled examples

    [Given("a step deriving its idempotency key from the instance")]
    public void GivenAStepWithAStableKey()
    {
        // The guide's example, verbatim in shape: the key comes from the
        // instance and step, so every attempt sends the same one.
    }

    [Given("a gateway that charges before timing out")]
    public void GivenAGatewayThatChargesThenTimesOut() =>
        api.Declare(new SpecWorkflow("charging", 1, builder => builder.AddStep(
            "charge",
            () => new ChargesWithKey(this.effects),
            RetryPolicy.FixedDelay(3, TimeSpan.Zero))));

    [When("the step is retried")]
    public async Task WhenTheStepIsRetried() => await api.Engine.StartAsync("charging", 1);

    [Then("the card is charged exactly once")]
    public void ThenTheCardIsChargedExactlyOnce()
    {
        // Two calls reached the gateway; one charge resulted, because both
        // carried the same key. A stable key is what makes retry safe.
        Assert.Equal(2, this.effects.Count(effect => effect.StartsWith("call:", StringComparison.Ordinal)));
        Assert.Single(this.effects, effect => effect == "charged");
    }

    [Given("the guide's reserve, charge and ship workflow")]
    public void GivenTheGuidesWorkflow() =>
        api.Declare(new SpecWorkflow("fulfil-order", 1, builder => builder
            .AddStep("reserve-stock", () => new Effect(this.effects, "reserve"))
                .WithCompensation(() => new Effect(this.effects, "release"))
            .AddStep("charge", () => new Effect(this.effects, "charge"))
                .WithCompensation(() => new Effect(this.effects, "refund"))
            .AddStep("ship", () => new Explodes())));

    [When("shipping fails")]
    public async Task WhenShippingFails() => await api.Engine.StartAsync("fulfil-order", 1);

    [Then("the refund runs before the stock release")]
    public void ThenRefundRunsBeforeRelease()
    {
        // The charge happened last, so it is undone first. Releasing the stock
        // the charge paid for before refunding it would invert the dependency
        // the forward pass established.
        Assert.Equal(["reserve", "charge", "refund", "release"], this.effects);
    }

    private sealed class Noop : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Outcome.Next);
    }

    private sealed class Explodes : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("no carrier available");
    }

    private sealed class Effect(List<string> log, string name) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            log.Add(name);
            return ValueTask.FromResult(Outcome.Next);
        }
    }

    /// <summary>
    /// Charges through a gateway that honours idempotency keys, and loses the
    /// response the first time.
    /// </summary>
    private sealed class ChargesWithKey(List<string> log) : IStep
    {
        public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);

            var key = $"{context.InstanceId}:{context.StepName}";

            log.Add($"call:{key}");

            if (!log.Contains("charged"))
            {
                // The charge is recorded and then the response is lost. From
                // the step's point of view this failed - which is exactly what
                // makes retry dangerous without a stable key.
                log.Add("charged");
                throw new TimeoutException("gateway timed out");
            }

            // Same key, so the gateway recognises it and does not charge again.
            return ValueTask.FromResult(Outcome.Next);
        }
    }
}
