using FlowDeck.Specs.Support;
using Reqnroll;

namespace FlowDeck.Specs.Steps;

/// <summary>
/// The provider-selection step shared by every feature that runs the same
/// scenario against both stores.
/// </summary>
[Binding]
public sealed class StoreProviderSteps(StoreContext stores)
{
    // A regex rather than {word}: the outline's second example is "EF Core",
    // and {word} captures a single word, so that row silently failed to bind.
    [Given(@"^the (.+) workflow store$")]
    public void GivenTheWorkflowStore(string provider) => stores.Use(provider);
}
