# Defining and running a workflow

Everything below works against `FlowDeck.Core` as it exists today. Nothing here
is aspirational.

> **Before you rely on this:** instances are durable, survive a restart, and are
> recovered by another node if the one running them dies. The limits worth
> knowing are that a step may run **twice** — on retry, and on lease expiry — so
> [steps must be idempotent](#your-step-must-be-idempotent), and that a cluster
> recovers work rather than spreading it. See
> [known limitations](#known-limitations).

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

## Branching and parallel execution

A workflow is not only a straight line. A step can send execution down one of
several **branches**, or fan out into branches that all run at the same time.

### A named branch

The step decides, and the branch is selected by name:

```csharp
builder
    .AddStep("check-stock", () => new CheckStock())
        .Branch("in-stock",  b => b.AddStep("charge", () => new Charge()))
        .Branch("backorder", b => b.AddStep("notify", () => new NotifyCustomer()));
```

### A predicate branch

The condition is a plain function of workflow data, so the shape of the workflow
is readable without running it:

```csharp
builder
    .AddStep("total", () => new CalculateTotal())
        .BranchWhen("large", data => data.Get<int>("total") > 1000,
            b => b.AddStep("manual-approval", () => new Approve()))
        .BranchWhen("small", data => data.Get<int>("total") <= 1000,
            b => b.AddStep("auto-approve", () => new AutoApprove()));
```

Conditions are tested in declaration order and the first match wins, so you read
them the way you read an `if` / `else if` chain.

Both attach **backwards**, to the step just declared — the same rule as
`WithCompensation`. A branch belongs to the decision that selects it.

### A parallel fork

```csharp
builder
    .AddStep("prepare", () => new Prepare())
        .Fork(
            a => a.AddStep("reserve-stock", () => new ReserveStock()),
            b => b.AddStep("authorise-payment", () => new AuthorisePayment()),
            c => c.AddStep("notify-warehouse", () => new NotifyWarehouse()));
```

**Parallel branches run genuinely concurrently** — on separate tasks, at the same
time. Three slow HTTP calls take as long as the slowest, not their sum. That is
the reason to reach for a fork, and it is also the reason the rest of this
section exists.

The join is implicit. When the branches finish, execution continues with whatever
was declared after the branching step; there is no join to declare and no way to
declare one that does not converge.

### Workflow data is shared, and only individually thread-safe

Every branch of a fork reads and writes **the same** `IWorkflowData`. Each
individual `Get`, `Set`, `TryGet` and `Contains` is safe to call from several
branches at once; the bag is locked internally.

That is all it gives you. A `Get` followed by a `Set` is **two** operations, and
two branches doing that to the same key is a lost update — exactly as it would be
in any other shared state:

```csharp
// Not safe. Two branches can both read 3 and both write 4.
var count = context.Data.Get<int>("processed");
context.Data.Set("processed", count + 1);
```

Give each branch its own key and combine after the join, which is safe because
the join has already waited for both:

```csharp
// In branch A                          // In branch B
context.Data.Set("processed-a", 3);     context.Data.Set("processed-b", 4);

// After the join
var total = context.Data.Get<int>("processed-a") + context.Data.Get<int>("processed-b");
```

### A join waits for every branch, and any failure fails the instance

Every branch runs to completion. If one fails, the instance fails **once the
others have finished**, and compensation unwinds what completed — including work
done on sibling branches that succeeded.

A failing branch does not abandon its siblings mid-step. Abandoning one would not
stop its side effects; it would only stop FlowDeck recording them.

There is deliberately no way to express best-effort work that may fail without
stopping the workflow. If you need that, catch the failure inside your own step
and return `Outcome.Next`.

### A choice with no matching condition takes no branch

Execution simply continues past the branching step. This is not an error: a
conditional with no matching case is an ordinary shape, and failing would make
every branch set implicitly require a catch-all.

If a branch is genuinely mandatory, assert it in the step that decides.

### Compensation is ordered by completion, not by declaration

Reverse execution order stops being well defined once two branches ran at the
same time. Rollback therefore walks what actually happened, **most recently
completed first**.

Two consequences worth knowing:

- Sibling branches' compensating actions are independent, so they may run in
  either relative order.
- A step on a branch the instance never took is never compensated. Undoing work
  that never happened would act on the world based on nothing.

### Suspending inside a branch is not supported

`Outcome.Suspend` from a step inside a branch **fails the instance**, with a
message saying so. It does not suspend.

The unsettled question is not *where* a suspended fork would resume from — the
position has been set-valued since #166 — but what "suspended" should mean while
sibling branches are still running. Failure has an answer: the siblings run on
and the join fails. Suspension has none, so the engine refuses rather than
parking an instance in a state no rule covers. Tracked by #179 — suspend from the
top-level sequence in the meantime.

### What a crash does to a fork

Recovery resumes each branch where it stopped, and skips branches that had
already finished. The step that opened the fork is not re-run.

A recovered choice stays on the branch it had taken rather than re-evaluating its
condition, because the data the condition read may have changed since.

## Running on more than one node

Every FlowDeck node runs the same code and polls the same database for work
nobody is holding. There is **no leader and no election**: nodes are symmetric,
and a node dying costs only the leases it held.

```csharp
builder.Services.AddSingleton(new ClusterOptions
{
    NodeId = Environment.MachineName,          // defaults to machine:process
    LeaseDuration = TimeSpan.FromSeconds(30),
    RenewalInterval = TimeSpan.FromSeconds(10),
    PollInterval = TimeSpan.FromSeconds(5),
});
```

A node claims an instance by writing its id and a lease expiry onto the record,
and renews while it works. An expired lease is what an orphan *is* — claiming
and orphan detection are one mechanism rather than two that must agree.

### This is recovery, not load balancing

`StartAsync` still runs the workflow **inline, on the node that received the
request**, exactly as it always has. The dispatcher exists for work whose node
died, and for suspended instances waiting to be continued.

An instance started on a busy node stays on that node. Adding nodes adds
resilience, not throughput for work already in flight.

### A lapsed lease can cause a duplicate step execution

The dangerous case is a lease expiring while its owner is **still working** — a
slow step, a paused process, a clock that jumped. Two nodes then believe they
own the instance.

Every checkpoint is guarded by the same concurrency token that protects any
write, so the node that lost its lease also loses the race to save and stops.

**That bounds the damage; it does not prevent it.** Both nodes may have
*executed* the same step before either tried to write. Fencing means at most one
of them records progress, not that the step ran once.

So the requirement retry already imposes now has a second reason behind it: a
step that may run twice **must be idempotent**. See
[Your step must be idempotent](#your-step-must-be-idempotent) — everything there
applies to lease expiry as well as to retry.

If a step cannot be made idempotent, run one node.

### Nodes assume roughly agreed clocks

Lease expiry is compared against **each node's own clock**, not the database's.
FlowDeck's store depends only on `EntityFrameworkCore.Relational`, and there is
no portable way to ask for a server timestamp across SQLite, PostgreSQL and SQL
Server without provider-specific SQL.

A node whose clock runs fast will reclaim work that is still running. Run NTP.

### Tuning the lease

| Setting | Too small | Too large |
| --- | --- | --- |
| `LeaseDuration` | healthy work gets stolen | recovery after a crash waits |
| `RenewalInterval` | needless database writes | a slow node loses its lease |
| `PollInterval` | every node hammers the database | abandoned work sits longer |

`RenewalInterval` must be **shorter** than `LeaseDuration`, or a healthy node
loses its lease before it can renew. FlowDeck rejects that at startup rather
than producing a cluster that thrashes and looks like a network problem.

### Shutting a node down

A node that stops **gracefully** hands its leases back, so a peer can pick the
work up immediately rather than waiting out the lease. A node that is killed
does not — and the lease lapsing is the backstop that makes correctness
independent of the graceful path ever running.

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
| Step names must be unique across the whole graph, branches included | `InvalidWorkflowDefinitionException` |
| A branch must be declared after a step, and must declare at least one step | `InvalidWorkflowDefinitionException` |
| A step must not declare two branches with the same name | `InvalidWorkflowDefinitionException` |
| A step branches one way or the other: a choice must not be mixed with a fork | `InvalidWorkflowDefinitionException` |
| A fork must declare at least two branches | `InvalidWorkflowDefinitionException` |
| A definition id must not be blank | `ArgumentException` |
| A version must be positive | `ArgumentException` |
| `(id, version)` must be registered before starting | `DefinitionNotFoundException` |

Every engine exception derives from `FlowDeckException`, so engine faults can be
caught separately from faults thrown by your step code.

## Known limitations

| Limitation | Tracked by |
| --- | --- |
| A retry backoff blocks the calling task | #39 |
| Suspending inside a branch fails the instance rather than suspending it | #179 |
| Best-effort branches: any branch failure fails the instance | — |
| A lapsed lease can cause a duplicate step execution | [above](#a-lapsed-lease-can-cause-a-duplicate-step-execution) |
| Recovery is not load balancing: a started instance stays on its node | — |
| Cancelling an instance does not roll it back | #124 |
| A suspended instance cannot be resumed over HTTP | #68 |
| No authentication on the API | #42 |
| Definitions are C# classes registered at startup | #183 |

Three earlier entries have gone because M6 fixed them: an instance left `Running`
by a crash is now recovered by another node's dispatcher, so are interrupted
rollbacks, and FlowDeck is no longer single-node.

What replaced them is narrower and worth reading twice: recovery is **not**
load balancing, and a lapsed lease can run a step twice.

Earlier entries here claimed instances were lost on restart, that there was no
HTTP API, and that input was not persisted. All three were fixed in M2 and M3
and the table was not updated — which is its own lesson about documentation that
nothing verifies.

## See also

- [Architecture](../architecture.md)
- [Decision records](../adr/README.md)
