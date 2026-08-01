using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Graph/Documentation.feature.
/// </summary>
/// <remarks>
/// The prose is asserted against the file, following #108, #123 and #150. The
/// warning that matters most here is the one about shared data: a lock on the
/// bag makes each call safe and makes nothing else safe, and an author who reads
/// "thread-safe" and stops will write a lost update.
/// </remarks>
[Binding]
[Scope(Feature = "Branching and parallel execution are documented")]
public sealed class GraphDocumentationSteps
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

    [Given("the branching section of the usage guide")]
    public void GivenTheBranchingSection()
    {
        this.guide = ReadGuide();

        var start = this.guide.IndexOf("## Branching and parallel execution", StringComparison.Ordinal);

        Assert.True(start >= 0, "the guide has no branching section");

        var rest = this.guide[start..];
        var next = rest.IndexOf("\n## ", StringComparison.Ordinal);

        this.section = next > 0 ? rest[..next] : rest;
    }

    [Given("the workflow guide")]
    public void GivenTheWorkflowGuide() => this.guide = ReadGuide();

    [Then("it shows how to declare a named branch and a predicate branch")]
    public void ThenItShowsBothMechanisms()
    {
        // Both, because the API has both and an author shown only one will
        // reach for it where the other fits (ADR-0024 decision 1).
        Assert.Contains(".Branch(", this.section, StringComparison.Ordinal);
        Assert.Contains(".BranchWhen(", this.section, StringComparison.Ordinal);

        // Called by their nature rather than only by their method name, so a
        // reader skimming headings can tell which one they want.
        Assert.Contains("named branch", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("predicate branch", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that parallel branches run genuinely concurrently")]
    public void ThenItStatesGenuineConcurrency()
    {
        Assert.Contains(".Fork(", this.section, StringComparison.Ordinal);
        Assert.Contains("genuinely concurrently", this.section, StringComparison.OrdinalIgnoreCase);

        // Not just the word. The reason an author would want it, and therefore
        // the reason they should expect the hazards that follow.
        Assert.Contains("as long as the slowest", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that workflow data is shared and only individually thread-safe")]
    public void ThenItStatesTheDataHazard()
    {
        Assert.Contains("shared", this.section, StringComparison.OrdinalIgnoreCase);

        // The sentence an author must not miss. A lock on the bag makes each
        // call safe and implies far more than it gives, so the guide has to say
        // what it does not cover rather than let "thread-safe" stand alone.
        Assert.Contains("lost update", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("two", this.section, StringComparison.OrdinalIgnoreCase);

        // And a way out, not only a warning. A hazard with no remedy reads as
        // "do not use forks".
        Assert.Contains("own key", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that a join waits for every branch and any failure fails the instance")]
    public void ThenItStatesTheJoinRule()
    {
        Assert.Contains("waits for every branch", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fails the instance", this.section, StringComparison.OrdinalIgnoreCase);

        // Including the part an author would otherwise assume away: siblings
        // that succeeded are rolled back too.
        Assert.Contains("sibling", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that a choice with no matching condition takes no branch")]
    public void ThenItStatesTheNoMatchRule()
    {
        // Otherwise an author writes a catch-all they do not need, or discovers
        // in production that a missing case is silent rather than an error.
        Assert.Contains("no matching condition", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that suspending inside a branch is not supported")]
    public void ThenItStatesTheSuspensionLimit()
    {
        Assert.Contains("Suspending inside a branch", this.section, StringComparison.OrdinalIgnoreCase);

        // That it *fails*, not merely that it is unsupported. "Not supported"
        // reads as "does nothing" to plenty of people.
        Assert.Contains("fails the instance", this.section, StringComparison.OrdinalIgnoreCase);
        // The issue that would lift the limit, not the one that happened to
        // introduce the guard. A limitation citing a closed issue reads as
        // already fixed.
        Assert.Contains("#179", this.section, StringComparison.Ordinal);
    }

    [Then("it states that compensation is ordered by completion, not by declaration")]
    public void ThenItStatesCompensationOrdering()
    {
        Assert.Contains("most recently", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("completed first", this.section, StringComparison.OrdinalIgnoreCase);

        // The consequence, not only the rule: siblings may unwind in either
        // order, so an author must not encode a dependency between them.
        Assert.Contains("either relative order", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("the known limitations table names suspending inside a branch")]
    public void ThenTheLimitationsTableNamesIt()
    {
        var table = Section(this.guide, "## Known limitations");

        // The table is where an author checks before relying on something, so a
        // limit documented only in prose is a limit half the readers miss.
        Assert.Contains("Suspending inside a branch", table, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Best-effort", table, StringComparison.OrdinalIgnoreCase);
    }

    [Then("the rules table names the branch declarations the engine rejects")]
    public void ThenTheRulesTableNamesBranchRules()
    {
        var table = Section(this.guide, "## Rules the engine enforces");

        Assert.Contains("across the whole graph", table, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("at least two branches", table, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("one way or the other", table, StringComparison.OrdinalIgnoreCase);
    }

    private static string Section(string guide, string heading)
    {
        var start = guide.IndexOf(heading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"the guide has no '{heading}' section");

        var rest = guide[start..];
        var next = rest.IndexOf("\n## ", StringComparison.Ordinal);

        return next > 0 ? rest[..next] : rest;
    }
}
