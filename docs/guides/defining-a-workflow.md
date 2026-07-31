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

Remaining steps do not execute. Without a retry policy any failure is terminal;
with one, see [Retry](#retry) below. There is **no compensation** yet (#38).

## Retry

A step declares how many times it may be attempted and how long to wait between
attempts:

```csharp
builder.AddStep("charge", () => new ChargeCard(gateway),
    RetryPolicy.ExponentialBackoff(3, TimeSpan.FromSeconds(2)));
```

Or once for every step in the workflow:

```csharp
builder
    .WithRetryPolicy(RetryPolicy.ExponentialBackoff(3, TimeSpan.FromSeconds(2)))
    .AddStep("charge", () => new ChargeCard(gateway))
    .AddStep("ship", () => new Ship());
```

A step policy overrides the workflow default, and `RetryPolicy.None` opts a step
out of it. The default applies only to steps declared **after** it.

| Factory | Behaviour |
| --- | --- |
| `RetryPolicy.None` | One attempt. The default. |
| `RetryPolicy.FixedDelay(n, delay)` | The same wait between every attempt |
| `RetryPolicy.ExponentialBackoff(n, base)` | Doubling waits, with jitter |

`MaxAttempts` counts **attempts, not retries** — `3` means the step runs at most
three times. Delays are capped by `MaxDelay` so an instance never appears hung,
and exponential backoff jitters by default, so a hundred instances that failed
together do not retry together.

### Your step must be idempotent

**A retried step runs again in full.** The step is the unit of retry, not the
line that threw, so everything the step did before throwing happens a second
time:

```csharp
public ValueTask<Outcome> ExecuteAsync(IStepContext context, CancellationToken ct)
{
    ReserveStock();      // runs on attempt 1
    ShipOrder();         // throws on attempt 1
    return ValueTask.FromResult(Outcome.Next);
}
// attempt 2 reserves the stock *again*
```

**The engine provides no duplicate protection.** It does not deduplicate step
executions, track side effects, or know what your step did. It cannot: a step is
arbitrary C#, and the engine has no way to tell a database write from a wire
transfer. Only you can make a retry safe.

This bites hardest where you would least like it to. A charge that reaches the
payment gateway and then times out on the response *succeeded* — the money moved
— but from the step's point of view it failed, so it retries and charges again.

Pass an idempotency key derived from something stable:

```csharp
public sealed class ChargeCard(IPaymentGateway gateway) : IStep
{
    public async ValueTask<Outcome> ExecuteAsync(
        IStepContext context,
        CancellationToken cancellationToken = default)
    {
        // Derived from the instance id, so every attempt at this step sends the
        // same key. A key generated per execution would be a new key on every
        // retry, which is the same as having none.
        var idempotencyKey = $"{context.InstanceId}:{context.StepName}";

        await gateway.ChargeAsync(idempotencyKey, amount: 4200, cancellationToken);

        return Outcome.Next;
    }
}
```

`Guid.NewGuid()` as a key is the mistake worth naming: it is different on every
attempt, so the gateway sees each retry as a new charge and an idempotent
gateway cannot help you.

Three ways to make a step safe, in rough order of preference:

1. **Give the downstream service an idempotency key**, as above. Best, because
   the guarantee lives with the side effect.
2. **Check before acting** — `if (await OrderExists(id)) return Outcome.Next;`.
   Cheaper, and racy if anything else can act between the check and the write.
3. **Split the step** so the non-repeatable part is its own step with
   `RetryPolicy.None`. A completed step is never re-executed, so nothing before
   it repeats.

If a step cannot be made idempotent, do not give it a retry policy. An
un-retried failure you can see is better than a duplicate side effect you
cannot.

### Attempts are visible afterwards

Every attempt appends its own history entry, carrying its own error and its
attempt number:

```csharp
var history = await engine.GetHistoryAsync(instance.Id);

foreach (var entry in history)
{
    Console.WriteLine($"{entry.StepName} attempt {entry.Attempt}: {entry.Status}");
}
```

`Attempt` starts at 1, including for a step with no policy. A step re-entered
after a resume reports attempt 1 again — it never failed, so counting it as a
retry would report a failure that did not happen.

### What retry does not do yet

- **The wait blocks the caller.** `StartAsync` does not return during a backoff,
  so a policy with long delays holds the calling thread's task for that long.
  Releasing the worker needs a scheduler (#39).
- **A host that dies mid-retry does not recover.** The attempt count is durable,
  so the ceiling still applies, but the instance is left `Running` and nothing
  resumes it yet (#39).
- **Nothing is undone by retry alone.** A step that exhausts its attempts fails
  the instance. Undoing what already succeeded is [Compensation](#compensation),
  below.

## Compensation

A workflow that fails partway has already done things. An order workflow that
charged a card and then failed to ship has taken money for goods it cannot
deliver.

Declare how a step is undone, beside the step:

```csharp
builder
    .AddStep("reserve-stock", () => new ReserveStock(orders))
        .WithCompensation(() => new ReleaseStock(orders))
    .AddStep("charge", () => new Charge(orders))
        .WithCompensation(() => new Refund(orders))
    .AddStep("ship", () => new Ship(orders));
```

If `ship` throws, the engine runs `Refund` and then `ReleaseStock`, and the
instance ends as `Compensated`.

**Declaring the action is the whole opt-in.** There is no second switch — a
workflow carrying undo actions that do not run would look protected and not be.

`WithCompensation` applies to the step **just declared**, unlike
`WithRetryPolicy`, which sets a forward default for steps after it. A retry
policy is a sensible thing to apply broadly; an undo action is specific to the
one thing it undoes.

### Rollback runs in reverse

Most recent first. Later steps may depend on what earlier ones did, so releasing
the stock before refunding the charge that paid for it inverts a dependency the
forward pass established.

Steps with no compensating action are **skipped, not failed**. Most steps have
nothing to undo.

Steps that never executed are not compensated either — undoing work that never
happened would act on the world based on nothing.

### A step that exhausted its retries is still compensated

Exactly **once**, however many attempts it made.

*Not zero*, because a step that never reported success may still have had an
effect — the charge that reached the gateway and then timed out on the response.
Skipping it would be wrong exactly where it matters most.

*Not once per attempt*, because [retried steps must be idempotent](#your-step-must-be-idempotent),
which means the attempts shared one idempotency key and therefore one side
effect. One undo covers it.

### Rollback does not stop at a failing action

If a compensating action throws, the engine records it and **continues** to the
next one.

Stopping would leave *more* un-undone work than continuing: refusing to refund
the card because releasing stock failed adds a second unresolved side effect to
the first.

The cost is real. If one action failed because a service is down, the next may
fail for the same reason, and the engine will keep trying anyway. This is the
better default, not a safe one.

Note the asymmetry: the **forward** pass stops at the first failure, the
**reverse** pass does not.

### Compensation is best-effort

The engine tries everything and reports honestly. It does not guarantee the
world is back where it started, and no engine can — your compensating action
talks to systems FlowDeck knows nothing about.

Practically, that means:

- A compensating action **must be idempotent** too, for the same reason a
  retried step must be. Nothing prevents it running after a partial earlier run.
- `CompensationFailed` means **you have work to do**. The engine cannot say how
  partly an instance rolled back; only its history can, so read the timeline.
- If undoing a step reliably matters more than the engine can promise, build
  reconciliation. Compensation reduces how often you need it; it does not
  remove the need.

### The statuses it produces

| Status | Meaning |
| --- | --- |
| `Failed` | A step failed and nothing was rolled back |
| `Compensated` | A step failed and every compensating action succeeded |
| `CompensationFailed` | A step failed and at least one action also failed |

All three are terminal. The instance stays `Running` *during* the rollback, so
compensation always happens before a terminal state, never after.

`Compensated` requires something to have been undone: a workflow with no
compensating actions still reports `Failed`.

The original failure survives the rollback. `FailedStepName`, `ErrorType` and
`ErrorMessage` describe the step that broke, not a compensating action that also
failed — you need to know why it failed, not just that the cleanup did.

### Rollback in the history

Each compensating action appends an entry named `compensate:<step>`:

```csharp
foreach (var entry in await engine.GetHistoryAsync(instance.Id))
{
    Console.WriteLine($"{entry.StepName}: {entry.Status}");
}

// reserve-stock: Success
// charge: Success
// ship: Failed
// compensate:charge: Success
// compensate:reserve-stock: Failed
```

That last line is the whole point of recording them: the refund happened, the
stock release did not, and an operator can see which is which.

### Cancelling does not compensate

Cancelling an instance stops it. It does **not** roll it back.

An operator cancelling a workflow may be stopping it to fix forward, and an
automatic rollback would destroy work they meant to keep. Whether that should be
an explicit choice is #124, not settled by the engine's default.

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
| `Failed` | A step threw, and nothing was rolled back |
| `Cancelled` | Stopped by an operator |
| `Compensated` | A step threw, and every compensating action succeeded |
| `CompensationFailed` | A step threw, and a compensating action failed too |

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
| A retry backoff blocks the calling task | #39 |
| An instance left `Running` by a crash is never resumed | #39 |
| A rollback interrupted by a crash does not resume | #39 |
| Cancelling an instance does not roll it back | #124 |
| Single node only | #39 |
| A suspended instance cannot be resumed over HTTP | #68 |
| No authentication on the API | #42 |
| Definitions are C# classes registered at startup | #40 |

Earlier entries here claimed instances were lost on restart, that there was no
HTTP API, and that input was not persisted. All three were fixed in M2 and M3
and the table was not updated — which is its own lesson about documentation that
nothing verifies.

## See also

- [Architecture](../architecture.md)
- [Decision records](../adr/README.md)
