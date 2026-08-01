using System.Text.RegularExpressions;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Engine/VersionDocumentation.feature.
/// </summary>
/// <remarks>
/// The prose is asserted against the file, following #108, #123, #150, #167 and
/// #190. Whitespace is collapsed before matching, because the guide is
/// hard-wrapped and re-wrapping a paragraph is a cosmetic edit that must not
/// fail a test about what the paragraph says.
/// </remarks>
[Binding]
[Scope(Feature = "The version lifecycle is documented")]
public sealed partial class VersionDocumentationSteps
{
    private string guide = string.Empty;
    private string section = string.Empty;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    private static string ReadGuide()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs")))
        {
            directory = directory.Parent;
        }

        var text = directory is null
            ? throw new InvalidOperationException("Could not locate the docs directory.")
            : File.ReadAllText(Path.Combine(directory.FullName, "docs", "guides", "defining-a-workflow.md"));

        return WhitespaceRuns().Replace(text, " ");
    }

    [Given("the workflow guide")]
    public void GivenTheGuide() => this.guide = ReadGuide();

    [Given("the versioning section of the usage guide")]
    public void GivenTheVersioningSection()
    {
        this.guide = ReadGuide();

        var start = this.guide.IndexOf("## Versioning", StringComparison.Ordinal);

        Assert.True(start >= 0, "the guide has no versioning section");

        // Bounded at the next top-level heading, so a claim made three sections
        // away does not satisfy an assertion about this one - a requirement
        // documented far from the feature that creates it is documented in name
        // only (#108).
        var rest = this.guide[start..];
        var next = rest.IndexOf(" ## ", StringComparison.Ordinal);

        this.section = next > 0 ? rest[..next] : rest;
    }

    [Then("it states that an in-flight instance runs to completion on its own version")]
    public void ThenItStatesRunToCompletion()
    {
        Assert.Contains(
            "runs to completion on the version it started",
            this.section,
            StringComparison.OrdinalIgnoreCase);

        // And that there is no migration at all, stated rather than left to be
        // inferred from its absence.
        Assert.Contains("There is no migration", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that a bug in a deployed version cannot be fixed for instances already running it")]
    public void ThenItStatesTheCost() =>

        // The sentence that matters most in the section. An author who assumes
        // redeploying fixes a stuck run will assume it exactly once.
        Assert.Contains(
            "cannot be fixed for instances already running it",
            this.section,
            StringComparison.OrdinalIgnoreCase);

    [Then("it says what the remedies actually are")]
    public void ThenItNamesTheRemedies()
    {
        // A limitation with no way out reads as "this engine is broken". These
        // two are the whole set, and saying so is kinder than leaving a reader
        // to search for a third.
        Assert.Contains("wait for it to finish", this.section, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cancel it and start again", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that retiring a version instances still hold is refused")]
    public void ThenItStatesRetirementIsRefused()
    {
        Assert.Contains("RetireAsync", this.section, StringComparison.Ordinal);
        Assert.Contains("Retirement therefore refuses", this.section, StringComparison.OrdinalIgnoreCase);

        // Why, not only that. The reason is the hazard it closes, and a reader
        // who does not know it will read the refusal as fussiness.
        Assert.Contains("unresumable", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it states that the refusal says how many")]
    public void ThenItStatesTheCountIsCarried()
    {
        Assert.Contains("DefinitionInUseException", this.section, StringComparison.Ordinal);
        Assert.Contains("how many", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("it says how to find out what is holding one")]
    public void ThenItSaysHowToLook()
    {
        Assert.Contains("activeInstances", this.section, StringComparison.Ordinal);
        Assert.Contains("safe to retire", this.section, StringComparison.OrdinalIgnoreCase);
    }

    [Then("the known limitations table names the absence of migration")]
    public void ThenLimitationsNameMigration()
    {
        var table = Section(this.guide, "## Known limitations");

        Assert.Contains("No migration", table, StringComparison.OrdinalIgnoreCase);

        // Pointing at a live issue, not a closed one. That is the process-debt
        // entry M7 recorded, applied rather than repeated.
        Assert.Contains("#67", table, StringComparison.Ordinal);
    }

    [Then("it names multi-tenancy as a decision rather than an omission")]
    public void ThenLimitationsNameTenancy()
    {
        var table = Section(this.guide, "## Known limitations");

        Assert.Contains("Multi-tenancy", table, StringComparison.OrdinalIgnoreCase);

        // "by decision", and pointing at the ADR that made it. A reader who
        // finds no tenant column should not have to guess whether anyone
        // thought about it.
        Assert.Contains("by decision", table, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("0027", table, StringComparison.Ordinal);
    }

    private static string Section(string guide, string heading)
    {
        var start = guide.IndexOf(heading, StringComparison.Ordinal);

        Assert.True(start >= 0, $"the guide has no '{heading}' section");

        var rest = guide[start..];
        var next = rest.IndexOf(" ## ", StringComparison.Ordinal);

        return next > 0 ? rest[..next] : rest;
    }
}
