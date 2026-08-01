# ADR-0025: Observability

**Status:** Accepted · **Milestone:** M8 · **Issues:** #41

## Context

FlowDeck is, as of M7, **almost entirely silent**. The engine emits no logs at
all: the only `ILogger` in the codebase is in `DispatcherHostedService`, and it
says three things about polling. An operator whose workflow is stuck has the
dashboard, the store, and nothing else.

That is a gap in [NFR-2](../requirements.md), which requires a failed instance to
record what failed, where and when *without an operator needing to reproduce it*.
M2 satisfied the durable half of that — history, failure details, timestamps.
The live half has never existed. Nothing says an instance started, nothing says a
step is retrying, and nothing counts anything.

M6 and M7 made the gap worse rather than better. There are now several nodes,
leases changing hands, and branches running concurrently. "Which node ran this,
and when did it stop running it" is a question the system cannot currently
answer while it is happening.

This ADR settles how FlowDeck emits. Four decisions were the maintainer's and are
marked.

## Decisions

### 1. Instrumentation is BCL. Exporting is the host's job

**Decided by the maintainer, 2026-08-01.**

`FlowDeck.Core` emits through `ActivitySource`, `Meter` and `ILogger`. It takes
**no** OpenTelemetry dependency. `FlowDeck.Api` — a deployable, not a library —
takes the OpenTelemetry SDK and wires the exporters.

`ActivitySource` and `Meter` are in the BCL and cost nothing. OpenTelemetry is
one consumer of them, and a library that hard-wires its own exporter has decided
something that belongs to whoever hosts it. This is the same split as
[ADR-0015](0015-migrations-are-owned-by-the-host.md): the engine produces what a
host needs, and does not decide what the host does with it.

The practical test: an embedder who exports to something other than OTLP should
not have to fight FlowDeck, and should not carry OpenTelemetry's transitive graph
to do nothing with it.

**One consequence taken without asking:** `FlowDeck.Core` gains
`Microsoft.Extensions.Logging.Abstractions`. That is its **first** package
reference — the project has had none. [ADR-0010](0010-minimise-third-party-dependencies.md)
requires the judgement to be made per case rather than by rule, so: it is
first-party, it is abstractions-only with no implementation and no transitive
weight, and it is how a .NET library logs. The alternative — a hand-rolled
logging interface — would be a worse `ILogger` that every host has to adapt.

### 2. Metrics are instance lifecycle counters, and nothing else yet

**Decided by the maintainer, 2026-08-01.**

One counter per terminal outcome plus starts, tagged by definition id:

| Instrument | Kind | Tags |
| --- | --- | --- |
| `flowdeck.instances.started` | Counter | `definition.id`, `definition.version` |
| `flowdeck.instances.completed` | Counter | `definition.id`, `definition.version` |
| `flowdeck.instances.failed` | Counter | `definition.id`, `definition.version` |
| `flowdeck.instances.cancelled` | Counter | `definition.id`, `definition.version` |
| `flowdeck.instances.compensated` | Counter | `definition.id`, `definition.version`, `outcome` |

That answers throughput and failure rate, which is what #41's intent names.

**Deliberately not built**, each considered and each deferred:

- **Step duration histogram** (#198). The obvious next one, and the question an
  operator actually arrives with is "which step is slow". Deferred because
  history already records per-step start and completion, so the data exists — it
  is unaggregated, not missing.
- **Retry and compensation counters** (#199). M5 built both mechanisms and
  neither is visible without reading history per instance.
- **Cluster health gauges** (#200). Instances running, leases held, recoveries
  performed. M6's machinery is currently inferred from the dashboard.

These are scope, not oversight. They are recorded here so that the absence is a
decision with a date rather than a thing nobody thought of, and they are filed
rather than left in this list.

### 3. No workflow data reaches any signal

**Decided by the maintainer, 2026-08-01.**

Logs, spans and metric tags may carry: instance id, definition id and version,
step name, branch path, status, attempt number, duration, node id, and the type
and message of an exception the engine already records.

They may **not** carry workflow data — not a value, not a key, not a count of
keys.

[ADR-0014](0014-workflow-data-serialisation.md) already treats workflow data as
author-controlled and constrains what may be *persisted*, into a store the
operator owns. A span goes further: it leaves the process, crosses a network, and
lands in a backend that may be third-party, retained on someone else's schedule
and searchable by people who never had access to the database. A secret that
reaches a trace is a secret in a vendor's index.

Keys were considered and rejected with the values. Key names are author-chosen
and leak schema, and `customer-ssn` is a disclosure on its own.

This is a boundary that has to keep holding as instrumentation is added later, so
it is asserted by test rather than left as a convention — a scenario that renders
an instance's whole emitted output and fails if a canary value planted in
workflow data appears anywhere in it.

### 4. A scrape endpoint always; OTLP only when configured

**Decided by the maintainer, 2026-08-01.**

`GET /metrics` serves Prometheus text format and is always available. Tracing
exports over OTLP **only** when an endpoint is configured, and is otherwise not
wired at all.

The homelab runs two containers. Requiring a collector before any metric exists
would mean the default deployment observes nothing, and the operator most in need
of this is the one who has not built an observability stack. A scrape endpoint
costs no containers and a Prometheus that already exists can point at it.

**The exposition endpoint is hand-rolled, not exported by a package.**
`OpenTelemetry.Exporter.Prometheus.AspNetCore` has never shipped a stable
version — every release since 1.5 is `-beta.1`, currently `1.17.0-beta.1` — and
NFR-5's supply-chain posture is not one to spend on a prerelease that reaches the
deployed image. A `MeterListener` over FlowDeck's own `Meter`, rendered as
Prometheus text format, is small, dependency-free and testable against a format
that has not changed in years.

This is the ADR-0010 judgement running the other way from `TestTimeProvider`:
there, the capability outgrew the hand-rolled version and the package was taken.
Here the capability is a stable text format and one listener, and it does not.

### 5. Instance id is a span attribute and a log scope, never baggage

Taken without asking. #41 asked which, and the answer follows from where FlowDeck
executes.

Baggage propagates across process boundaries, and FlowDeck's steps run **in** the
engine's process. There is nothing downstream to propagate to that is not already
inside the same activity. Baggage also travels outbound: a step making an HTTP
call would send the instance id to whatever third party it calls, which is a
small leak nobody asked for.

So the instance id goes on the span as an attribute, and into an `ILogger` scope
so that every line emitted while a step runs carries it without each call site
remembering to.

### 6. One `ActivitySource` and one `Meter`, named for the assembly

Taken without asking. Both are named `FlowDeck.Core`, matching the convention the
BCL and every collector expect, so a host enables FlowDeck's telemetry by naming
the assembly rather than by knowing an invented string.

Spans are `workflow.instance` for a run and `workflow.step` for each execution,
with the step span a child of the instance span and the instance span a child of
whatever the caller had open — the HTTP request, for an instance started over the
API.

## Consequences

- The engine gains its first package reference. Small, first-party, and stated
  rather than slipped in.
- An operator can answer "is it running, how fast, and how often does it break"
  without opening the database.
- A step's span is a child of the API request that started it, so a slow endpoint
  and the step responsible appear in one trace.
- Instrumentation is on the engine's hot path. Counters and activities are cheap
  when nothing is listening — `ActivitySource.StartActivity` returns null with no
  listener — but this is the first code in the engine whose cost depends on
  something outside it.
- **Nothing observes concurrency yet.** Branches run in parallel since M7 and the
  step spans will nest correctly, but no metric distinguishes a fork's arms from
  a sequence.
- The `/metrics` endpoint is unauthenticated, like the rest of the API (#42). It
  exposes definition ids and counts, not workflow data — but it is one more
  reason #42 matters.

## Alternatives considered

**OpenTelemetry SDK throughout, including Core.** One idiom, no BCL/SDK seam, and
the instrumentation libraries would be available inside the engine. Rejected by
the maintainer: every consumer of the engine would inherit the dependency whether
or not they export anything, and the BCL types are the interface OpenTelemetry
itself consumes.

**`ILogger` only, no metrics or traces.** The smallest thing that would close the
NFR-2 gap, and logs alone genuinely answer "what happened to this instance".
Rejected because "how often does this fail" is not a question logs answer without
an aggregation pipeline the homelab does not have.

**Emit workflow data behind a per-definition allow-list.** The author names the
keys safe to emit, which is the most useful version of this and mirrors
ADR-0014's existing allow-list. Rejected by the maintainer: it is new public API
surface, and a mistaken entry is a leak that looks deliberate. The absence of a
mechanism cannot be misconfigured.

**Prometheus exporter package rather than a hand-rolled endpoint.** Standard
behaviour and less code to own. Rejected because it has only ever shipped as a
prerelease, and NFR-5 exists to be spent on capability rather than convenience.
