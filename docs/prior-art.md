# Prior art: what is borrowed, what differs

The project brief directs FlowDeck to "cherry-pick the best design patterns"
from WorkflowCore, Hangfire and Elsa v3. This document records what was
actually taken, what was deliberately not taken, and where FlowDeck diverges.

## Provenance

**No source code from any of these projects has been copied into FlowDeck.**
Every type in `FlowDeck.Core` was written against the Given/When/Then scenarios
in this repository's issues.

What *was* taken is at the level of ideas and, in two places, vocabulary:

| Borrowed | From | Kind |
| --- | --- | --- |
| `IStep` as the name for a unit of work | WorkflowCore | **Naming** |
| `Next` / `Persist` as step outcomes | WorkflowCore | **Naming + concept** |
| Definition identity includes a version | Elsa v3, WorkflowCore | Concept |
| Suspend-and-resume as a first-class outcome | Elsa v3 (bookmarks) | Concept |
| Durable state written between units of work | Hangfire | Concept |
| Operator dashboard with run inspection | Octopus Deploy, Hangfire | Concept |

The two naming borrowings are called out because they are verbatim matches to
WorkflowCore's public API vocabulary. See
[ADR-0011](adr/0011-api-vocabulary-borrowed-from-workflowcore.md).

> **Accuracy caveat.** The characterisations of other projects below are from
> general familiarity, not from a fresh reading of their current source. This
> repository is public, so before any of this is used as a public comparison —
> a README claim, a blog post, a talk — each statement should be verified
> against the current version of the library it describes. Where FlowDeck's own
> behaviour is described, that is verified by tests.

## Where FlowDeck differs

### vs. WorkflowCore

| Aspect | WorkflowCore | FlowDeck | Why |
| --- | --- | --- | --- |
| Flow shape | Branching, parallel, `While`, `If` | **Strictly linear** | M1 scope. Branching is not designed yet and pretending otherwise in the API would be dishonest. |
| Step construction | Resolved from a DI container | **`Func<IStep>` factory** | Core stays container-agnostic. A factory can be backed by a container without changing the interface. See [ADR-0002](adr/0002-step-bodies-from-factories.md). |
| Data flow | `Input()`/`Output()` mapping expressions | **Keyed data bag with checked reads** | Expression mapping is powerful and adds a second language to learn. Checked reads name the key and both types on mismatch. See [ADR-0005](adr/0005-workflow-data-is-checked-not-cast.md). |
| Builder | Generic fluent `Then<TStep>()` | **`AddStep(name, factory)`** | Step names are part of the contract: they appear in history, errors and dashboards. Naming them explicitly makes that visible. |
| Outcome model | `ExecutionResult` object with branching values | **Flat `Outcome` enum** | Two outcomes are all a linear engine can act on. The enum grows when branching does. |

### vs. Hangfire

| Aspect | Hangfire | FlowDeck | Why |
| --- | --- | --- | --- |
| Unit of work | Independent background jobs | **Ordered sequence with shared state** | Different problem. Hangfire schedules work; FlowDeck sequences it. |
| Execution trigger | Server pool polls persisted queue | **Inline on the calling thread** (M1) | Deliberate M1 limitation, not a design position. #39 revisits it. |
| Continuations | Job chaining via continuations | **Steps within one definition** | State belongs to the instance, not to a chain of independent jobs. |
| Failure default | Automatic retry with backoff | **No retry; failure is terminal** | Retry is M5 (#37). Shipping a default retry before deciding its semantics would bake in an accidental policy. |
| Dashboard | Primarily observational | **Operator actions intended** (M4) | Cancel already exists in the engine; the dashboard will expose it. |

### vs. Elsa v3

| Aspect | Elsa v3 | FlowDeck | Why |
| --- | --- | --- | --- |
| Authoring | Designer-first, JSON definitions | **C# only** | The brief specifies steps implemented directly in C# initially. A designer is M7 and may start read-only. |
| Expressions | JavaScript / Liquid expression engine | **None** | Adding an expression language means a second execution model, a sandbox and a security surface. Not warranted for C#-authored workflows. |
| Flow shape | Arbitrary activity graph | **Linear list** | As above. |
| Suspension | Bookmarks with typed resume payloads | **`Outcome.Suspend`, resume re-enters the same step** | Simpler and sufficient. A typed resume payload has no story requiring it yet. |
| Versioning | Definition versions with published/draft states | **`(Id, Version)` composite key, no lifecycle states** | Draft/published is an authoring-tool concern, which FlowDeck does not yet have. |

### vs. Temporal

Temporal is not in the brief but is worth recording, because the persistence
decision in M2 (#60) is essentially a choice between its model and Hangfire's.

| Aspect | Temporal | FlowDeck |
| --- | --- | --- |
| Durability model | Event-sourced deterministic replay | **Undecided — the open question in #60** |
| Author constraints | Workflow code must be deterministic | **No determinism constraint today** |
| Versioning | Patching API for in-flight workflows | Deferred to #43 |

Replay imposes real constraints on step authors (no clocks, no random, no
uncontrolled I/O). Taking that on requires deciding it deliberately, which is
why #60 requires the ADR before #13 is implemented.

## Where FlowDeck is *not* different

Claiming novelty that does not exist would be worse than claiming none.

- **Versioned definition identity** is essentially Elsa's and WorkflowCore's
  idea. FlowDeck makes version part of the registry key rather than metadata,
  which is a sharper commitment, but the concept is theirs.
- **Suspend and resume** is conceptually Elsa's bookmarks with less machinery.
- **Catching step exceptions in an executor** is what every one of these engines
  does. FlowDeck's contribution is only that it is a single named component with
  one documented exception ([ADR-0004](adr/0004-cancellation-is-not-step-failure.md)).
- **An append-only execution history** (#18) is standard across all of them.

## What FlowDeck does that is genuinely its own

Small list, honestly kept:

- **Terminal states are strictly final.** Cancelling a completed, failed or
  already-cancelled instance is refused rather than being idempotent, because a
  silent second cancel would overwrite the first timestamp and make the audit
  trail lie. Several engines make cancel idempotent.
  See [ADR-0008](adr/0008-terminal-states-are-final.md).
- **Cancellation is excluded from failure by construction**, so a deployment
  cannot mark healthy instances `Failed`.
  See [ADR-0004](adr/0004-cancellation-is-not-step-failure.md).
- **Input validation rejects both directions** — including supplying input to a
  workflow that declares none, which is usually ignored.
  See [ADR-0006](adr/0006-input-type-is-declared.md).
- **Executable documentation.** The usage guide's samples and tables are
  compiled and asserted as tests.

## What is deliberately not taken

| Not taken | From | Reason |
| --- | --- | --- |
| Expression languages | Elsa | Second execution model, sandbox, security surface |
| Dynamic/JSON workflow definitions | Elsa | Brief specifies C#-authored steps |
| Automatic retry defaults | Hangfire | Retry semantics are M5's decision, not an accident |
| Container-resolved steps in core | WorkflowCore | Keeps `FlowDeck.Core` dependency-free |
| Cron/recurring scheduling | Hangfire | Scheduling is a separate concern from sequencing |
| Determinism constraints on authors | Temporal | Not decided; see #60 |

## Licensing

FlowDeck contains no third-party source. Its runtime dependencies are the .NET
base class library and, from M2, EF Core. Test dependencies are xUnit.

The projects above are variously MIT and Apache-2.0 licensed. Since no code is
taken from them, their licence terms do not attach to FlowDeck. If code is ever
adapted from one of them, that must be recorded here with the licence, the file
and the upstream commit.
