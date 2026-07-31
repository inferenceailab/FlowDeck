# Defining and running a workflow

Everything below works against `FlowDeck.Core` as it exists today. Nothing here
is aspirational.

> **Before you rely on this:** the engine is in-memory only. Instances are lost
> when the process exits, and `ResumeAsync` works only in the process that
> started the instance. See [known limitations](#known-limitations).

## A minimal workflow

A workflow is a class implementing `IWorkflowDefinition`. It declares an id, a
version, and its steps.

```csharp
using FlowDeck.Core;

public sealed class GreetWorkflow : IWorkflowDefinition
{
    public string Id => "greet";

    public int Version => 1;

    public void Build(IWorkflowBuilder builder) =>
        builder.AddStep("say-hello", () => new SayHello());
}

public sealed class SayHello : IStep
{
    public ValueTask<Outcome> ExecuteAsync(
        IStepContext context,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine($"Hello from instance {context.InstanceId}");
        return ValueTask.FromResult(Outcome.Next);
    }
}
```

Register it and start an instance:

```csharp
var registry = new WorkflowRegistry();
registry.Register(new GreetWorkflow());

var engine = new WorkflowEngine(registry);
var instance = await engine.StartAsync("greet", version: 1);

Console.WriteLine(instance.Status);   // Completed
```

## Step outcomes

A step returns an `Outcome` telling the engine what to do next.

| Outcome | Effect |
| --- | --- |
| `Outcome.Next` | Step is done. Advance to the next step. |
| `Outcome.Suspend` | Step is not done. Suspend here; resume later. |

A suspended instance stays positioned **on** the suspending step.
`ResumeAsync` re-enters that same step — it does not skip ahead.

```csharp
public sealed class WaitForApproval : IStep
{
    public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken ct)
    {
        if (!context.Data.TryGet<bool>("approved", out var approved) || !approved)
        {
            return ValueTask.FromResult(Outcome.Suspend);   // park here
        }

        return ValueTask.FromResult(Outcome.Next);
    }
}
```

## Sharing data between steps

`context.Data` is a key-value store scoped to one instance. Two instances of
the same workflow never see each other's values.

```csharp
// earlier step
context.Data.Set("orderId", 42);

// later step
var orderId = context.Data.Get<int>("orderId");        // 42
```

Reads are checked. Getting a key at the wrong type raises
`WorkflowDataTypeMismatchException` naming the key and both types, rather than a
bare cast error. A missing key raises `WorkflowDataKeyNotFoundException`.

Use `TryGet` when absence is expected:

```csharp
if (context.Data.TryGet<string>("note", out var note)) { /* ... */ }
```

A value explicitly set to `null` is **present**, not absent — `Contains` returns
true and `TryGet` succeeds. That lets a step distinguish "cleared" from "never
written".

### What can be stored

Once instances are persisted, workflow data has to survive a round trip through
storage. Types allowed out of the box:

`string` · `bool` · `byte` · `short` · `int` · `long` · `float` · `double` ·
`decimal` · `Guid` · `DateTime` · `DateTimeOffset` · `TimeSpan` · `byte[]`

Anything else must be registered:

```csharp
var serializer = new WorkflowDataSerializer(
    new WorkflowDataSerializerOptions().Allow<OrderDetails>());

var store  = new InMemoryWorkflowStore(serializer);
var engine = new WorkflowEngine(registry, store: store);
```

Storing an unregistered type raises `WorkflowDataSerializationException` naming
the key, **at the moment the value is stored** — not later when a read cannot
reconstruct it.

The allow-list is deliberate rather than convenient: a stored type name is
resolved on read, and resolving arbitrary names is how deserialisation
vulnerabilities work. See [ADR-0014](../adr/0014-workflow-data-serialisation.md).

## Typed input

Implement `IWorkflowDefinition<TInput>` to require input:

```csharp
public sealed record OrderRequest(int Id);

public sealed class FulfilOrder : IWorkflowDefinition<OrderRequest>
{
    public string Id => "fulfil-order";

    public int Version => 1;

    public void Build(IWorkflowBuilder builder) =>
        builder.AddStep("charge", () => new ChargeCard());
}

public sealed class ChargeCard : IStep
{
    public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken ct)
    {
        var request = context.GetInput<OrderRequest>();
        // ... use request.Id
        return ValueTask.FromResult(Outcome.Next);
    }
}
```

```csharp
await engine.StartAsync("fulfil-order", 1, new OrderRequest(7));
```

Validation is strict in **both** directions:

| Situation | Result |
| --- | --- |
| Typed workflow, correct input | starts |
| Typed workflow, wrong type | `InvalidInputTypeException` |
| Typed workflow, no input | `InvalidInputTypeException` |
| Untyped workflow, input supplied | `InvalidInputTypeException` |

The last case is deliberate — silently discarding input would let you believe it
was delivered when nothing can read it.

## Versioning

Identity is `(Id, Version)`, so two versions coexist:

```csharp
registry.Register(new FulfilOrderV1());   // version 1
registry.Register(new FulfilOrderV2());   // version 2

await engine.StartAsync("fulfil-order", 1);   // runs v1
await engine.StartAsync("fulfil-order", 2);   // runs v2
```

An instance pins its version at start, so deploying v2 does not change what an
in-flight v1 instance is executing. Registering the same `(id, version)` twice
raises `DuplicateDefinitionException`.

## Failure

A step that throws fails the instance. The exception does **not** propagate to
whoever called `StartAsync` — a workflow failing is a normal outcome.

```csharp
var instance = await engine.StartAsync("fulfil-order", 1, new OrderRequest(7));

if (instance.Status == InstanceStatus.Failed)
{
    Console.WriteLine(instance.FailedStepName);   // "charge"
    Console.WriteLine(instance.Error);            // the original exception
}
```

The original exception is preserved unwrapped, and the failing step name is
recorded separately from execution position.

**An instance you query later has no live exception.** An exception object
cannot be stored, so only its type and message survive:

```csharp
var reloaded = await engine.GetInstanceAsync(instance.Id);

reloaded.Error;         // null - always, on a queried instance
reloaded.ErrorType;     // "InvalidOperationException"
reloaded.ErrorMessage;  // "card declined"
```

`Error` is populated only on the instance `StartAsync` returned, in the process
that ran it.

Remaining steps do not execute. There is **no retry and no compensation** yet
(#37, #38): any failure is terminal.

## Querying and cancelling

```csharp
var instance = await engine.GetInstanceAsync(id);   // InstanceNotFoundException if unknown
var maybe    = await engine.FindInstanceAsync(id);  // null if unknown
var all      = await engine.ListInstancesAsync();   // newest first

await engine.CancelAsync(id);                       // Suspended/Running -> Cancelled
await engine.ResumeAsync(id);                       // Suspended -> continues
```

Filter and page the list:

```csharp
var failed = await engine.ListInstancesAsync(new InstanceFilter
{
    Status = InstanceStatus.Failed,
    Skip = 0,
    Take = 50,
});
```

Terminal states are final. Cancelling a `Completed`, `Failed` or already
`Cancelled` instance raises `InvalidStateTransitionException` carrying `From`
and `To`. Resuming a cancelled instance is refused for the same reason.

## Surviving a restart

Instances are checkpointed after every step, so a restart loses at most one
step of progress. `ResumeAsync` reloads state from the store and recompiles the
definition from the registry, which means **any host holding the same
definitions can continue an instance it never started**:

```csharp
// process A
var started = await engineA.StartAsync("approval", 1);   // suspends

// process B, later, over the same store
var resumed = await engineB.ResumeAsync(started.Id);
```

Two guarantees worth relying on:

- **A completed step is never re-executed.** Side effects that already happened
  do not happen twice.
- **A suspended step is re-entered, not skipped.** The instance stays positioned
  on it, so the step decides again whether it can proceed.

The recovering host must have the definition registered **at the version the
instance started on**. An instance pinned to v1 keeps running v1 even if v2 is
also registered.

Two things to know:

- Workflow data and input must be serialisable — see
  [what can be stored](#what-can-be-stored).
- A host that dies mid-step leaves its instance in `Running`, and nothing
  currently sweeps it back to `Suspended` (#39). Until then, such an instance
  will not resume.

## Instance lifecycle

| Status | Meaning |
| --- | --- |
| `Running` | Executing, or ready to continue |
| `Suspended` | Parked on a step, awaiting resume |
| `Completed` | Every step advanced |
| `Failed` | A step threw |
| `Cancelled` | Stopped by an operator |

`CreatedAt` is set at start; `CompletedAt` when terminal. Both are UTC.
`IsTerminal` covers `Completed`, `Failed` and `Cancelled`.

## Testing your workflows

`WorkflowEngine` takes an injectable `TimeProvider`, so tests never sleep:

```csharp
var clock = new TestTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
var engine = new WorkflowEngine(registry, clock);
```

## Rules the engine enforces

These fail fast rather than misbehaving quietly:

| Rule | Exception |
| --- | --- |
| A definition must declare at least one step | `InvalidWorkflowDefinitionException` |
| Step names must be unique within a definition | `InvalidWorkflowDefinitionException` |
| A definition id must not be blank | `ArgumentException` |
| A version must be positive | `ArgumentException` |
| `(id, version)` must be registered before starting | `DefinitionNotFoundException` |

Every engine exception derives from `FlowDeckException`, so engine faults can be
caught separately from faults thrown by your step code.

## Known limitations

| Limitation | Tracked by |
| --- | --- |
| Instances are lost on process restart | #13, #14 |
| The instance store grows without bound | #20 |
| `ResumeAsync` only works in the starting process | #14, #39 |
| No retry on step failure | #37 |
| No compensation or rollback | #38 |
| Single node only | #39 |
| No HTTP API | M3 |
| Input is not persisted | #15 |

## See also

- [Architecture](../architecture.md)
- [Decision records](../adr/README.md)
