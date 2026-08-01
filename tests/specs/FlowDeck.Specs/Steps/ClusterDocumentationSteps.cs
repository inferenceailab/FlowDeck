using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Cluster/Documentation.feature.
/// </summary>
/// <remarks>
/// The prose is asserted against the file, following #108 and #123. A limit that
/// quietly disappears in an unrelated edit is how an author comes to rely on a
/// guarantee the engine does not make — and the duplicate-execution warning
/// below is the one that would cost real money.
/// </remarks>
[Binding]
[Scope(Feature = "Multi-node operation is documented")]
public sealed class ClusterDocumentationSteps
{
    private string section = string.Empty;
    private string guide = string.Empty;

    private static string ReadGuide()
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

    [Given("the multi-node section of the usage guide")]
    public void GivenTheMultiNodeSection()
    {
        this.guide = ReadGuide();

        var start = this.guide.IndexOf("## Running on more than one node", StringComparison.Ordinal);

        Assert.True(start >= 0, "the guide has no multi-node section");

        var rest = this.guide[start..];
        var next = rest.IndexOf("\n## ", StringComparison.Ordinal);

        this.section = next > 0 ? rest[..next] : rest;
    }

    [Given("the workflow guide")]
    public void GivenTheWorkflowGuide() => this.guide = ReadGuide();

    [Then("it states that nodes are symmetric with no leader")]
    public void ThenItStatesSymmetry()
    {
        Assert.Contains("symmetric", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no leader", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that recovery is not load balancing")]
    public void ThenItStatesRecoveryIsNotLoadBalancing()
    {
        // The assumption an operator will otherwise make on seeing "cluster".
        Assert.Contains("not load balancing", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stays on that node", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that a lapsed lease can cause a duplicate step execution")]
    public void ThenItStatesDuplicateExecution()
    {
        // The one an author must not discover in production. ADR-0023 is
        // explicit that fencing bounds the damage rather than preventing it,
        // and the guide has to say the same thing in the same words.
        Assert.Contains("duplicate step execution", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bounds the damage", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("must be idempotent", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that nodes assume roughly agreed clocks")]
    public void ThenItStatesClockAssumption()
    {
        Assert.Contains("own clock", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NTP", this.section, StringComparison.Ordinal);
    }

    [Then("the known limitations no longer claim a crash strands an instance")]
    public void ThenTheLimitationsAreCurrent()
    {
        // These were true through M5 and are not any more. A limitations table
        // that keeps stale entries teaches a reader to distrust all of it.
        Assert.DoesNotContain("is never resumed", this.guide, StringComparison.Ordinal);
        Assert.DoesNotContain("Single node only", this.guide, StringComparison.Ordinal);
    }

    [Then("they record what multi-node execution still does not do")]
    public void ThenTheLimitationsRecordWhatRemains()
    {
        Assert.Contains(
            "Recovery is not load balancing",
            this.guide,
            StringComparison.OrdinalIgnoreCase);

        Assert.Contains(
            "A lapsed lease can cause a duplicate step execution",
            this.guide,
            StringComparison.OrdinalIgnoreCase);
    }
}
