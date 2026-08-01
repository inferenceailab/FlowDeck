using FlowDeck.Core;
using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Graph/Declaring.feature.
/// </summary>
/// <remarks>
/// Declaration only. Nothing here executes a branch — that is #164.
/// </remarks>
[Binding]
[Scope(Feature = "Declaring branches and forks")]
public sealed class DeclaringGraphSteps(EngineContext world)
{
    private IReadOnlyList<StepDeclaration>? compiled;

    private static IReadOnlyList<StepDeclaration> Compile(Action<IWorkflowBuilder> declare)
    {
        var builder = new WorkflowBuilder("graph");
        declare(builder);
        return builder.Build();
    }

    private StepDeclaration First => this.compiled![0];

    private static IStep Noop() => new SpecSteps.Recording([], "noop");

    // ------------------------------------------------------------ declaring

    [Given("a workflow whose step declares branches {string} and {string}")]
    public void GivenAStepWithTwoBranches(string first, string second) =>
        world.Declare("graph", 1, builder => builder
            .AddStep("check-stock", Noop)
                .Branch(first, b => b.AddStep($"{first}-work", Noop))
                .Branch(second, b => b.AddStep($"{second}-work", Noop)));

    [Given("a workflow declaring a branch when the order total exceeds {int}")]
    public void GivenAPredicateBranch(int threshold) =>
        world.Declare("graph", 1, builder => builder
            .AddStep("price", Noop)
                .BranchWhen(
                    "manual-approval",
                    data => data.TryGet<int>("total", out var total) && total > threshold,
                    b => b.AddStep("approve", Noop)));

    [Given("a workflow forking into two branches that rejoin")]
    public void GivenAFork() =>
        world.Declare("graph", 1, builder => builder
            .AddStep("prepare", Noop)
                .Fork(
                    b => b.AddStep("email", Noop),
                    b => b.AddStep("invoice", Noop))
            .AddStep("confirm", Noop));

    [Given("a workflow declaring two steps, the first with branches")]
    public void GivenTwoStepsFirstWithBranches() =>
        world.Declare("graph", 1, builder => builder
            .AddStep("first", Noop)
                .Branch("only", b => b.AddStep("inner", Noop))
            .AddStep("second", Noop));

    [When("the definition is compiled")]
    public void WhenTheDefinitionIsCompiled() => this.compiled = Compile(world.Only.Build);

    [Then("both branches are part of the graph")]
    public void ThenBothBranchesArePresent() => Assert.Equal(2, this.First.Branches.Count);

    [Then("neither carries a condition")]
    public void ThenNeitherCarriesACondition() =>
        // A step-decided branch has no predicate: the decision lives in the
        // step's own C#, which is the point of offering both mechanisms.
        Assert.All(this.First.Branches, branch => Assert.Null(branch.Condition));

    [Then("that branch carries a condition the graph can report")]
    public void ThenTheBranchCarriesACondition() =>
        Assert.NotNull(Assert.Single(this.First.Branches).Condition);

    [Then("the condition selects the branch for a total of {int}")]
    public void ThenTheConditionSelects(int total) =>
        // Evaluated, not merely non-null. A predicate that was stored but never
        // callable would satisfy the assertion above and nothing else.
        Assert.True(this.First.Branches[0].Condition!(DataWith(total)));

    [Then("it does not select the branch for a total of {int}")]
    public void ThenTheConditionDoesNotSelect(int total) =>
        Assert.False(this.First.Branches[0].Condition!(DataWith(total)));

    private static IWorkflowData DataWith(int total)
    {
        var data = new WorkflowData();
        data.Set("total", total);
        return data;
    }

    [Then("both are marked parallel")]
    public void ThenBothAreParallel() =>
        Assert.All(this.First.Branches, branch => Assert.True(branch.IsParallel));

    [Then("they converge on the step declared after the fork")]
    public void ThenTheyConverge()
    {
        // The join is implicit: whatever follows the forking step in the
        // enclosing sequence. There is no join to declare, and therefore no way
        // to declare one that does not converge.
        Assert.Equal(["prepare", "confirm"], this.compiled!.Select(step => step.Name));
        Assert.Equal(["email", "invoice"], this.First.Branches.Select(b => b.Steps[0].Name));
    }

    [Then("only the first step carries branches")]
    public void ThenOnlyTheFirstCarriesBranches()
    {
        Assert.Single(this.compiled![0].Branches);
        Assert.Empty(this.compiled[1].Branches);
    }

    // ------------------------------------------------------------ rejection

    [Given("a workflow calling Branch before AddStep")]
    public void GivenBranchBeforeAnyStep() =>
        world.Declare("graph", 1, builder => builder
            .Branch("early", b => b.AddStep("inner", Noop))
            .AddStep("later", Noop));

    [Given("a step declaring two branches both named {string}")]
    public void GivenDuplicateBranchNames(string name) =>
        world.Declare("graph", 1, builder => builder
            .AddStep("decide", Noop)
                .Branch(name, b => b.AddStep("first", Noop))
                .Branch(name, b => b.AddStep("second", Noop)));

    [Given("a step declaring an empty branch")]
    public void GivenAnEmptyBranch() =>
        world.Declare("graph", 1, builder => builder
            .AddStep("decide", Noop)
                .Branch("nowhere", _ => { }));

    [Given("a workflow reusing the step name {string} inside a branch")]
    public void GivenADuplicateStepNameInABranch(string name) =>
        world.Declare("graph", 1, builder => builder
            .AddStep(name, Noop)
                .Branch("retry", b => b.AddStep(name, Noop)));

    [When("a graph instance is started")]
    public async Task WhenAGraphInstanceIsStarted() =>
        await world.CapturingErrorAsync(async () =>
            world.Instance = await world.Engine().StartAsync("graph", 1));

    [Then("InvalidWorkflowDefinitionException is raised")]
    public void ThenInvalidWorkflowDefinitionIsRaised()
    {
        var error = Assert.IsType<InvalidWorkflowDefinitionException>(world.Error);

        // The message has to name what is wrong. "Invalid definition" sends an
        // author back to a builder chain to guess which line.
        Assert.False(string.IsNullOrWhiteSpace(error.Reason));
    }
}
