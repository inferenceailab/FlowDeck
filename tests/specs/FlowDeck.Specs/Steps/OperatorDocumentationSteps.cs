using System.Text.RegularExpressions;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// Binds Features/Api/OperatorDocumentation.feature.
/// </summary>
/// <remarks>
/// The prose is asserted against the file, following #108, #123, #150, #167,
/// #190 and #207. Whitespace is collapsed first, so re-wrapping a paragraph
/// stays the cosmetic edit it is.
/// </remarks>
[Binding]
[Scope(Feature = "The operator actions are documented")]
public sealed partial class OperatorDocumentationSteps
{
    private string guide = string.Empty;

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRuns();

    [Given("the operations guide")]
    public void GivenTheGuide()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "docs")))
        {
            directory = directory.Parent;
        }

        var text = directory is null
            ? throw new InvalidOperationException("Could not locate the docs directory.")
            : File.ReadAllText(Path.Combine(directory.FullName, "docs", "guides", "operating-flowdeck.md"));

        this.guide = WhitespaceRuns().Replace(text, " ");
    }

    private void Contains(string expected) =>
        Assert.Contains(expected, this.guide, StringComparison.OrdinalIgnoreCase);

    [Then("it names resume, suspend, retry, cancel and cancel-and-roll-back")]
    public void ThenItNamesEveryAction()
    {
        // The routes, not only the words. An operator wiring a script needs the
        // paths, and a guide that named the actions without them would send
        // them to read the OpenAPI document instead.
        foreach (var route in new[]
        {
            "/resume", "/suspend", "/retry", "/retry-from-failed-step",
            "/cancel", "/cancel-and-roll-back", "/bulk/cancel", "/bulk/retry",
        })
        {
            this.Contains(route);
        }
    }

    [Then("it says which are irreversible")]
    public void ThenItSaysWhichAreIrreversible()
    {
        // The word, in the table and in prose. An operator scanning for what is
        // safe should not have to infer it from an endpoint name.
        this.Contains("Reversible?");
        this.Contains("The two cancels are irreversible");
    }

    [Then("it states that retry starts a new instance rather than reopening the old one")]
    public void ThenItStatesRetryIsANewInstance() =>
        this.Contains("Neither retry reopens the instance you called it on");

    [Then("it states that the instance id changes")]
    public void ThenItStatesTheIdChanges()
    {
        // The sentence an operator would otherwise learn from a broken link in
        // a ticket.
        this.Contains("So the instance id changes");
        this.Contains("retriedFromInstanceId");
    }

    [Then("it says which retry to use when")]
    public void ThenItSaysWhichRetry()
    {
        this.Contains("safe to run twice");
        this.Contains("charge a card twice");

        // Including the refusal, which is otherwise a 409 with no explanation
        // of what to do instead.
        this.Contains("refuses retry-from-failing-step");
    }

    [Then("it states that suspend does not stop the running step")]
    public void ThenItStatesSuspendTiming() =>
        this.Contains("It does **not** stop the step that is running");

    [Then("it says why the engine cannot interrupt one")]
    public void ThenItSaysWhy() =>

        // The reason, not only the rule. An operator who does not know why will
        // read the delay as a bug and file it.
        this.Contains("treats them as untrusted");

    [Then("it warns that the instance stays Running until that step finishes")]
    public void ThenItWarnsAboutTheLag() =>
        this.Contains("That is the request being honoured, not ignored");

    [Then("it states that bulk actions are not atomic")]
    public void ThenItStatesBulkIsNotAtomic() => this.Contains("not atomic");

    [Then("it states that the per-item report must be read")]
    public void ThenItStatesTheReportMatters()
    {
        this.Contains("Read the per-item report");

        // Why: the status code cannot express partial success, which is the
        // thing a caller will otherwise assume it does.
        this.Contains("is not something a status code can say");
    }

    [Then("it states the cap and what truncation means")]
    public void ThenItStatesTheCap()
    {
        this.Contains("At most 200 instances");
        this.Contains("truncated");
    }

    [Then("it states that workflow data cannot be edited")]
    public void ThenItStatesNoDataEditing()
    {
        this.Contains("Editing workflow data");

        // And why, so it reads as a decision rather than a gap somebody will
        // file a request for.
        this.Contains("nothing able to validate it");
    }

    [Then("it states that FlowDeck does not record who performed an action")]
    public void ThenItStatesNoAudit()
    {
        // The one an operator must not discover during a post-incident review.
        this.Contains("does not record *who* performed an action");
        this.Contains("#42");
    }
}
