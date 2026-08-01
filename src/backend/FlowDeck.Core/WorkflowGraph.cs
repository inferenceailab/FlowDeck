namespace FlowDeck.Core;

/// <summary>
/// What a definition declares, read without starting anything.
/// </summary>
/// <remarks>
/// The builder that turns <see cref="IWorkflowDefinition.Build"/> into
/// declarations is internal, so until now the only way to discover a
/// workflow's shape was to start an instance of it. A dashboard asking what a
/// workflow does cannot have side effects to find out, hence a deliberate
/// public entry point rather than making the builder public — which would turn
/// a compilation detail into something consumers depend on and this project
/// then has to keep.
///
/// <para>
/// <b>What comes back holds compiled code.</b>
/// <see cref="StepDeclaration.Factory"/> and
/// <see cref="BranchDeclaration.Condition"/> are delegates, so anything
/// crossing a process boundary has to project this into a shape it can
/// honestly serialise. That a branch carries a condition is the most that can
/// be said of one; what the condition tests cannot be recovered from a
/// delegate, and claiming otherwise would put an invented answer in front of
/// an operator.
/// </para>
/// </remarks>
public static class WorkflowGraph
{
    /// <summary>
    /// Compiles <paramref name="definition"/> into the steps it declares, in
    /// declaration order.
    /// </summary>
    /// <remarks>
    /// Runs the definition's own <see cref="IWorkflowDefinition.Build"/>, which
    /// is what the engine does at every instance start. So a definition that
    /// composes its steps from injected dependencies describes exactly what it
    /// would execute — and one whose <c>Build</c> has side effects has them
    /// here too, which is the same bargain starting an instance already made.
    /// </remarks>
    /// <exception cref="InvalidWorkflowDefinitionException">
    /// The definition declares something the engine cannot execute. Describing
    /// fails for the reason starting would: a shape that cannot run is not a
    /// shape worth drawing.
    /// </exception>
    public static IReadOnlyList<StepDeclaration> Of(IWorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var builder = new WorkflowBuilder(definition.Id);
        definition.Build(builder);

        return builder.Build();
    }
}
